export const maxFileBytes = 1024 * 1024
export const maxBatchFiles = 20
const maxPortfolios = 50
const maxHoldings = 500
const maxCsvRecords = 2000
const storageKey = 'portfel.csv-portfolio-book.v1'
const quantityPattern = /^(?:0|[1-9]\d{0,63})(?:\.\d{1,96})?$/
const assetIdPattern = /^[A-Za-z0-9_-]+:[A-Za-z0-9._:/-]+$/

export type Holding = { name: string; assetId: string | null; quantity: string }
export type Snapshot = { holdings: Holding[]; exportedAt: string | null }
export type Portfolio = Snapshot & { id: string; name: string; sourceName: string }
export type ImportDraft = Snapshot & { sourceName: string; name: string; targetId: string }
export type Book = { portfolios: Portfolio[]; raw: string | null }

function readCsv(text: string) {
  const rows: string[][] = []
  let row: string[] = []
  let field = ''
  let state: 'field' | 'quoted' | 'closed' = 'field'
  const pushField = () => { row.push(field); field = ''; state = 'field' }
  const pushRow = () => {
    if (rows.length >= maxCsvRecords) throw new Error('В CSV больше 2000 записей, включая строки сводки.')
    pushField()
    rows.push(row)
    row = []
  }
  for (let i = text.charCodeAt(0) === 0xfeff ? 1 : 0; i < text.length; i++) {
    const char = text[i]
    if (state === 'quoted') {
      if (char !== '"') field += char
      else if (text[i + 1] === '"') { field += '"'; i++ }
      else state = 'closed'
    } else if (char === ',') pushField()
    else if (char === '\r' || char === '\n') {
      if (char === '\r' && text[i + 1] === '\n') i++
      pushRow()
    } else if (char === '"' && !field && state === 'field') state = 'quoted'
    else if (char === '"' || state === 'closed') throw new Error(`Некорректные кавычки в записи ${rows.length + 1}.`)
    else field += char
  }
  if (state === 'quoted') throw new Error(`Незакрытые кавычки в записи ${rows.length + 1}.`)
  if (field || row.length || state === 'closed') pushRow()
  return rows
}

function validText(value: unknown, limit = 200): value is string {
  return typeof value === 'string' && value.length <= limit && !!value.trim() && !/[\u0000-\u001f\u007f]/.test(value)
}

function validAssetId(value: unknown): value is string {
  return typeof value === 'string' && value.length <= 160 && assetIdPattern.test(value)
}

function canonicalQuantity(value: string) {
  if (!/^(?:\d+|\d{1,3}(?:,\d{3})+)(?:\.\d+)?$/.test(value)) return null
  const quantity = value.replaceAll(',', '').replace(/^0+(?=\d)/, '').replace(/(\.\d*?)0+$/, '$1').replace(/\.$/, '')
  return quantityPattern.test(quantity) ? quantity : null
}

function holdingKey(holding: Holding) {
  return holding.assetId ? `id:${holding.assetId}` : `name:${holding.name}`
}

export function parseHoldingsCsv(text: string): Snapshot {
  if (text.length > maxFileBytes) throw new Error('Размер CSV должен быть не больше 1 МБ.')
  const rows = readCsv(text)
  const headers = rows.map(row => row.map(cell => cell.trim().toLowerCase()))
  const headerAt = headers.findIndex(row => (row.includes('name') || row.includes('asset'))
    && (row.includes('amount') || row.includes('quantity')))
  if (headers.some((row, i) => (headerAt < 0 || i <= headerAt) && row.includes('type')
    && (row.includes('token') || row.includes('asset') || row.includes('name'))
    && (row.includes('amount') || row.includes('quantity')))) {
    throw new Error('Это история операций. Для остатков выберите *_overview.csv или таблицу позиций.')
  }
  if (headerAt < 0) throw new Error('Не найдена таблица остатков: нужны столбцы Name и Amount или asset и quantity.')
  const columns = headers[headerAt]
  if (new Set(columns).size !== columns.length) throw new Error('В таблице повторяются названия столбцов.')
  if ([['name', 'asset'], ['amount', 'quantity'], ['asset id', 'asset_id']].some(aliases => aliases.every(alias => columns.includes(alias))))
    throw new Error('В таблице есть несколько столбцов для одного поля.')
  const nameAt = columns.findIndex(column => column === 'name' || column === 'asset')
  const amountAt = columns.findIndex(column => column === 'amount' || column === 'quantity')
  const idAt = columns.findIndex(column => column === 'asset id' || column === 'asset_id')
  const holdings: Holding[] = []
  const seen = new Set<string>()
  for (const [index, row] of rows.slice(headerAt + 1).entries()) {
    if (row.every(value => !value.trim())) continue
    const name = row[nameAt]?.trim().normalize()
    const raw = row[amountAt]?.trim()
    const quantity = raw ? canonicalQuantity(raw) : null
    const assetId = row[idAt]?.trim() || null
    const record = headerAt + 2 + index
    if (row.length !== columns.length || !validText(name) || quantity === null || (assetId !== null && !validAssetId(assetId)))
      throw new Error(`Некорректное название, количество или ID актива в записи ${record}.`)
    const holding = { name, quantity, assetId }
    const key = holdingKey(holding)
    if (seen.has(key)) throw new Error(`Повтор актива в записи ${record}. Укажите одно количество на актив или различите их по Asset ID.`)
    seen.add(key)
    holdings.push(holding)
    if (holdings.length > maxHoldings) throw new Error('Поддерживается до 500 позиций в одном портфеле.')
  }
  const stamp = rows[0]?.[1]
  const zone = rows[0]?.[0]?.match(/\((?:UTC|GMT)[+-]\d{1,2}:\d{2}\)$/)?.[0]
  const exportedAt = stamp && /^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d$/.test(stamp) ? `${stamp}${zone ? ` ${zone}` : ''}` : null
  return { holdings, exportedAt }
}

export function suggestedName(fileName: string) {
  const base = /\.zip$/i.test(fileName) ? fileName.slice(0, -4)
    : fileName.replace(/\.csv$/i, '').replace(/[_-]overview(?: \(\d+\))?$/i, '')
  const stem = base.trim().normalize().slice(0, 120)
  return validText(stem, 120) ? stem : 'Портфель'
}

function nameKey(name: string) { return name.normalize().trim().toLowerCase() }

export function suggestTarget(portfolios: Portfolio[], sourceName: string) {
  const byFile = portfolios.filter(portfolio => nameKey(portfolio.sourceName) === nameKey(sourceName))
  if (byFile.length === 1) return byFile[0].id
  if (byFile.length > 1) return ''
  return portfolios.find(portfolio => nameKey(portfolio.name) === nameKey(suggestedName(sourceName)))?.id ?? ''
}

export function applyImports(portfolios: Portfolio[], drafts: ImportDraft[], newId: () => string) {
  const next = [...portfolios]
  const replaced = new Set<string>()
  const names = new Set(portfolios.map(portfolio => nameKey(portfolio.name)))
  for (const draft of drafts) {
    const { holdings, exportedAt, sourceName, targetId } = draft
    if (!validText(sourceName, 512)) throw new Error('Имя файла содержит недопустимые символы или слишком длинное.')
    const snapshot = { holdings, exportedAt, sourceName }
    if (targetId) {
      if (replaced.has(targetId)) throw new Error('Два файла выбраны для замены одного портфеля. Оставьте только один из них.')
      const index = portfolios.findIndex(portfolio => portfolio.id === targetId)
      if (index < 0) throw new Error('Портфель для замены не найден.')
      replaced.add(targetId)
      next[index] = { ...next[index], ...snapshot }
    } else {
      const name = draft.name.trim().normalize()
      if (!validText(name, 120)) throw new Error('Введите название нового портфеля (до 120 символов).')
      const key = nameKey(name)
      if (names.has(key)) throw new Error('Название уже занято. Выберите существующий портфель для замены или задайте другое имя.')
      names.add(key)
      next.push({ id: newId(), name, ...snapshot })
    }
  }
  if (next.length > maxPortfolios) throw new Error('Можно сохранить не больше 50 портфелей в этом браузере.')
  return next
}

export function readBook(): Book {
  const raw = localStorage.getItem(storageKey)
  if (raw === null) return { portfolios: [], raw }
  try {
    const book = JSON.parse(raw) as { version: number; portfolios: Portfolio[] }
    if (book.version !== 1 || !Array.isArray(book.portfolios) || book.portfolios.length > maxPortfolios) throw new Error()
    const ids = new Set<string>()
    const names = new Set<string>()
    for (const portfolio of book.portfolios) {
      if (!validText(portfolio.id, 64) || ids.has(portfolio.id)
        || !validText(portfolio.name, 120) || names.has(nameKey(portfolio.name)) || !validText(portfolio.sourceName, 512)
        || (portfolio.exportedAt !== null && !validText(portfolio.exportedAt, 40))
        || !Array.isArray(portfolio.holdings) || portfolio.holdings.length > maxHoldings) throw new Error()
      ids.add(portfolio.id)
      names.add(nameKey(portfolio.name))
      const assets = new Set<string>()
      for (const holding of portfolio.holdings) {
        if (!validText(holding.name) || (holding.assetId !== null && !validAssetId(holding.assetId))
          || typeof holding.quantity !== 'string' || !quantityPattern.test(holding.quantity) || assets.has(holdingKey(holding))) throw new Error()
        assets.add(holdingKey(holding))
      }
    }
    return { portfolios: book.portfolios, raw }
  } catch { throw new Error('Сохранённые портфели не читаются. Запись заблокирована, чтобы не перезаписать данные.') }
}

export async function saveBook(previous: Book, portfolios: Portfolio[]): Promise<Book> {
  function write() {
    if (localStorage.getItem(storageKey) !== previous.raw)
      throw new Error('Портфели изменились в другой вкладке. Обновите страницу перед сохранением.')
    const raw = JSON.stringify({ version: 1, portfolios })
    try { localStorage.setItem(storageKey, raw) }
    catch { throw new Error('Не удалось сохранить портфели. Проверьте настройки и свободное место хранилища браузера.') }
    return { portfolios, raw }
  }
  return navigator.locks ? navigator.locks.request(storageKey, write) : write()
}

export type SummaryRow = Holding & { key: string; portfolios: string[]; mappingKeys: string[] }

function addQuantities(left: string, right: string) {
  const [a, af = ''] = left.split('.')
  const [b, bf = ''] = right.split('.')
  const scale = Math.max(af.length, bf.length)
  const total = (BigInt(a + af.padEnd(scale, '0')) + BigInt(b + bf.padEnd(scale, '0'))).toString()
  if (!scale) return total
  const digits = total.padStart(scale + 1, '0')
  const fraction = digits.slice(-scale).replace(/0+$/, '')
  return digits.slice(0, -scale) + (fraction ? `.${fraction}` : '')
}

export function summarizePortfolios(portfolios: Portfolio[], groupByName: boolean): SummaryRow[] {
  const rows = new Map<string, SummaryRow>()
  for (const portfolio of portfolios) {
    for (const holding of portfolio.holdings) {
      const key = holding.assetId || groupByName ? holdingKey(holding) : `portfolio:${portfolio.id}:${holdingKey(holding)}`
      const mappingKey = holding.assetId ? holdingKey(holding) : JSON.stringify([portfolio.id, holding.name])
      const row = rows.get(key)
      if (row) {
        row.quantity = addQuantities(row.quantity, holding.quantity)
        row.portfolios.push(portfolio.name)
        if (!row.mappingKeys.includes(mappingKey)) row.mappingKeys.push(mappingKey)
      } else rows.set(key, { ...holding, key, portfolios: [portfolio.name], mappingKeys: [mappingKey] })
    }
  }
  return [...rows.values()].sort((a, b) => a.name.localeCompare(b.name, 'ru'))
}
