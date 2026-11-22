import { useState } from 'react'
import { applyImports, maxBatchFiles, maxFileBytes, parseHoldingsCsv, saveBook, suggestTarget, suggestedName,
  summarizePortfolios, type Book, type ImportDraft, type Portfolio } from '@/lib/csv-portfolios'
import { Button, Card, CardContent, CardDescription, CardHeader, CardTitle, Input } from './ui/primitives'
import { PortfolioPrices } from './portfolio-prices'
import { Warning } from './shared'
import { PortfolioHeading, PortfolioHints } from './portfolio-tools'

export const csvPortfoliosPath = '/portfolios'
export const pathForPortfolio = (id: string) => `${csvPortfoliosPath}?${new URLSearchParams({ portfolio: id })}`
type ImportError = { file: string; message: string }
type FileDraft = ImportDraft & { entryName?: string }

export function CsvPortfolios({ book, bookError, portfolioId, onSave, onOverview }: {
  book: Book | null; bookError: string; portfolioId: string | null; onSave: (book: Book) => void; onOverview: () => void
}) {
  const [drafts, setDrafts] = useState<FileDraft[]>([])
  const [fileErrors, setFileErrors] = useState<ImportError[]>([])
  const [excluded, setExcluded] = useState<string[]>([])
  const [groupByName, setGroupByName] = useState(false)
  const [mode, setMode] = useState<'read' | 'write' | null>(null)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')

  const portfolios = book?.portfolios ?? []
  const active = portfolios.find(portfolio => portfolio.id === portfolioId)
  const selected = portfolioId !== null ? (active ? [active] : []) : portfolios.filter(portfolio => !excluded.includes(portfolio.id))
  const summary = summarizePortfolios(selected, groupByName)
  const replacements = drafts.filter(draft => draft.targetId).length

  async function prepare(files: File[]) {
    if (!book || mode || !files.length) return
    setError('')
    setNotice('')
    setDrafts([])
    setFileErrors([])
    if (files.length > maxBatchFiles) {
      setError('Выберите не больше 20 файлов за один раз.')
      return
    }
    setMode('read')
    const prepared: FileDraft[] = []
    const failures: ImportError[] = []
    for (const file of files) {
      try {
        let text: string
        let entryName: string | undefined
        if (/\.zip$/i.test(file.name)) {
          const { readOverviewFromZip } = await import('@/lib/portfolio-archive')
          const archive = await readOverviewFromZip(file, maxFileBytes)
          text = archive.text
          entryName = archive.entryName
        } else {
          if (/_transactions\.csv$/i.test(file.name)) throw new Error('Это история операций. Для остатков выберите *_overview.csv.')
          if (file.size > maxFileBytes) throw new Error('Размер CSV должен быть не больше 1 МБ.')
          try { text = new TextDecoder('utf-8', { fatal: true }).decode(await file.arrayBuffer()) }
          catch { throw new Error('Не удалось прочитать файл. Нужен текстовый CSV в UTF-8.') }
        }
        prepared.push({
          ...parseHoldingsCsv(text), sourceName: file.name, entryName,
          name: suggestedName(file.name), targetId: suggestTarget(book.portfolios, file.name),
        })
      } catch (failure) { failures.push({ file: file.name, message: (failure as Error).message }) }
    }
    setDrafts(prepared)
    setFileErrors(failures)
    setMode(null)
  }

  function editDraft(index: number, changes: Partial<Pick<ImportDraft, 'name' | 'targetId'>>) {
    setDrafts(previous => previous.map((draft, i) => i === index ? { ...draft, ...changes } : draft))
    setError('')
  }

  async function commitImports() {
    if (!book || mode || !drafts.length) return
    setMode('write')
    setError('')
    setNotice('')
    try {
      const next = applyImports(book.portfolios, drafts, () => crypto.randomUUID())
      const saved = await saveBook(book, next)
      onSave(saved)
      setDrafts([])
      setNotice(`Добавлено: ${drafts.length - replacements}. Обновлено: ${replacements}.`)
    } catch (failure) { setError((failure as Error).message) }
    finally { setMode(null) }
  }

  async function remove(portfolio: Portfolio) {
    if (!book || mode || !window.confirm(`Удалить «${portfolio.name}» из браузера? Исходный файл на диске останется.`)) return
    setMode('write')
    setError('')
    setNotice('')
    try {
      onSave(await saveBook(book, book.portfolios.filter(item => item.id !== portfolio.id)))
      setExcluded(previous => previous.filter(id => id !== portfolio.id))
      if (portfolioId === portfolio.id) onOverview()
      setNotice('Портфель удалён из браузера.')
    } catch (failure) { setError((failure as Error).message) }
    finally { setMode(null) }
  }

  function toggle(id: string, checked: boolean) {
    setExcluded(previous => checked ? previous.filter(item => item !== id) : [...previous, id])
  }

  return <div className="grid min-w-0 gap-5">
    <PortfolioHeading title={active?.name ?? 'Мои портфели'} source="Локальные данные"
      description="Остатки из файлов остаются в браузере. Рыночные котировки в USD — от CoinGecko.">
      {portfolioId !== null && <Button variant="outline" onClick={onOverview}>Все мои портфели</Button>}
    </PortfolioHeading>
    <PortfolioHints>Чтобы обновить остатки, импортируйте новый снимок и выберите замену этого портфеля.
      Для нескольких портфелей откройте сводку и отметьте нужные снимки.</PortfolioHints>
    {book && portfolioId !== null && !active && <Warning title="Портфель не найден">Выберите другой сохранённый портфель или откройте сводку.</Warning>}
    <Card className="min-w-0">
      <CardHeader><CardTitle>Импорт остатков</CardTitle>
        <CardDescription>Выберите CSV с остатками (<code>Name,Amount</code> или <code>asset,quantity</code>) либо ZIP с <code>*_overview.csv</code>. История операций не импортируется.</CardDescription></CardHeader>
      <CardContent className="grid min-w-0 gap-4">
        <label className="grid max-w-md gap-1.5 text-sm font-medium">Файлы портфелей (CSV или ZIP)
          <Input type="file" accept=".csv,text/csv,.zip,application/zip" multiple disabled={!book || !!mode} onChange={event => {
            const files = Array.from(event.currentTarget.files ?? [])
            event.currentTarget.value = ''
            void prepare(files)
          }} />
        </label>
        {mode === 'read' && <p role="status" className="text-sm">Читаем файлы…</p>}
        {fileErrors.length > 0 && <Warning title="Файлы пропущены"><ul className="list-disc space-y-1 pl-4">
          {fileErrors.map(({ file, message }, i) => <li key={i} className="break-words">{file}: {message}</li>)}
        </ul></Warning>}
        {(bookError || error) && <Warning title="Не удалось сохранить изменения">{bookError || error}</Warning>}
        {drafts.length > 0 && <div className="grid min-w-0 gap-3">
          <h2 className="font-semibold">Проверка перед импортом</h2>
          {drafts.map((draft, i) => <fieldset key={i} disabled={!!mode} className="grid min-w-0 gap-2 rounded-md border p-3">
            <p className="break-words text-sm font-medium">{draft.sourceName}</p>
            {draft.entryName && <p className="break-words text-xs text-muted-foreground">Остатки из архива: {draft.entryName}</p>}
            <p className="text-xs text-muted-foreground">Позиций: {draft.holdings.length}{draft.exportedAt ? ` · ${draft.exportedAt}` : ''}
              {draft.holdings.length === 0 && ' · пустой снимок'}</p>
            <label className="grid gap-1 text-sm">Куда импортировать файл {i + 1}
              <select value={draft.targetId} onChange={event => editDraft(i, { targetId: event.target.value })}
                className="h-9 w-full min-w-0 rounded-md border bg-background px-2">
                <option value="">Новый портфель</option>
                {portfolios.map(portfolio => <option key={portfolio.id} value={portfolio.id}>Заменить: {portfolio.name}</option>)}
              </select>
            </label>
            {!draft.targetId && <label className="grid gap-1 text-sm">Название портфеля {i + 1}
              <Input maxLength={120} value={draft.name} onChange={event => editDraft(i, { name: event.target.value })} />
            </label>}
            <Button size="sm" variant="ghost" className="justify-self-start" aria-label={`Убрать файл ${i + 1} из импорта`}
              onClick={() => { setDrafts(previous => previous.filter((_, k) => k !== i)); setError('') }}>Убрать из импорта</Button>
          </fieldset>)}
          <p className="text-sm text-muted-foreground">
            Новых: {drafts.length - replacements}. Замен: {replacements}. При замене старые остатки не складываются с новыми.
          </p>
          <div className="flex flex-wrap gap-2">
            <Button disabled={!!mode} onClick={() => void commitImports()}>Подтвердить импорт</Button>
            <Button disabled={!!mode} variant="ghost" onClick={() => { setDrafts([]); setFileErrors([]); setError('') }}>Отмена</Button>
          </div>
        </div>}
        {notice && <p role="status" className="text-sm">{notice}</p>}
      </CardContent>
    </Card>
    {(active || portfolioId === null && portfolios.length > 0) && <>
      {portfolioId === null && <Card className="min-w-0">
        <CardHeader><CardTitle>Сохранённые портфели</CardTitle><CardDescription>Отметьте портфели для сводки. Не выбирайте два портфеля с одними и теми же средствами.</CardDescription></CardHeader>
        <CardContent className="grid min-w-0 gap-2">
          {portfolios.map(portfolio => <div key={portfolio.id} className="flex min-w-0 flex-wrap items-center justify-between gap-2 rounded-md border p-2">
            <label className="flex min-w-0 items-start gap-2 text-sm">
              <input type="checkbox" className="mt-1" checked={!excluded.includes(portfolio.id)} onChange={event => toggle(portfolio.id, event.target.checked)} />
              <span className="min-w-0 break-words"><strong>{portfolio.name}</strong><br />
                <span className="text-xs text-muted-foreground">Позиций: {portfolio.holdings.length}{portfolio.exportedAt ? ` · ${portfolio.exportedAt}` : ''}</span></span>
            </label>
            <Button size="sm" variant="ghost" disabled={!!mode || drafts.length > 0} aria-label={`Удалить портфель ${portfolio.name}`}
              onClick={() => void remove(portfolio)}>Удалить</Button>
          </div>)}
        </CardContent>
      </Card>}
      <Card className="min-w-0">
        <CardHeader><CardTitle>Сводка остатков</CardTitle>
          <CardDescription>Выбрано портфелей: {selected.length}. Активы с одинаковым ID объединяются. Названия без ID по умолчанию не объединяются.</CardDescription></CardHeader>
        <CardContent className="grid min-w-0 gap-3">
          {portfolioId === null && <label className="flex items-start gap-2 text-sm"><input type="checkbox" className="mt-1"
            checked={groupByName} onChange={event => setGroupByName(event.target.checked)} />
            <span>Объединять позиции без ID по совпадающим названиям<br /><span className="text-xs text-muted-foreground">Включайте только если убедились, что это один и тот же актив.</span></span>
          </label>}
          {summary.length === 0 ? <p className="text-sm text-muted-foreground">{selected.length ? 'В выбранных портфелях нет позиций.' : 'Выберите хотя бы один портфель.'}</p> :
            <PortfolioPrices summary={summary} />}
        </CardContent>
      </Card>
    </>}
  </div>
}
