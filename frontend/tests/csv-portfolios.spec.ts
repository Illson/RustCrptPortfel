import { expect, test } from '@playwright/test'
import { TextReader, Uint8ArrayWriter, ZipWriter } from '@zip.js/zip.js/lib/zip-core-native.js'
import { parseHoldingsCsv, summarizePortfolios } from '../src/lib/csv-portfolios'

const overview = [
  'Time Snapshot (UTC+3:00),2026-11-22 15:00:00',
  'Currency,USD',
  '',
  'Assets',
  'Name,Price (USD),Holdings (USD),Amount',
  'Bitcoin,25,"1,000.00",0.125000000000000001',
  '"Ether, example",500,1000,"1,234.5000"',
].join('\r\n')
const file = (name: string, text: string) => ({ name, mimeType: 'text/csv', buffer: Buffer.from(text) })
const transactions = 'Time (UTC+3:00),Token,Type,Price (USD),Amount\n2026-11-22 15:00:00,BTC,Buy,500,1'
const fileLabel = 'Файлы портфелей (CSV или ZIP)'

test.beforeEach(async ({ page }) => {
  await page.route('**/api/v1/public/market/coins', route => route.fulfill({ json: [] }))
})

async function archive(name: string, entries: [string, string][], level = 6) {
  const zip = new ZipWriter(new Uint8ArrayWriter(), { useWebWorkers: false, level })
  for (const [path, content] of entries) await zip.add(path, new TextReader(content))
  return { name, mimeType: 'application/zip', buffer: Buffer.from(await zip.close()) }
}

test('reads overview or generic CSV as exact balances, not as transactions or USD values', () => {
  expect(parseHoldingsCsv(`\ufeff${overview}`)).toEqual({
    exportedAt: '2026-11-22 15:00:00 (UTC+3:00)',
    holdings: [{ name: 'Bitcoin', quantity: '0.125000000000000001', assetId: null },
      { name: 'Ether, example', quantity: '1234.5', assetId: null }],
  })
  expect(parseHoldingsCsv('asset_id,quantity,asset\nchain:solana,000.000,Solana').holdings[0]).toEqual({
    name: 'Solana', quantity: '0', assetId: 'chain:solana',
  })
  expect(parseHoldingsCsv('asset,quantity').holdings).toEqual([])
  expect(() => parseHoldingsCsv(transactions)).toThrow(/история операций/)
  expect(() => parseHoldingsCsv('asset,type,quantity\nBTC,Buy,1')).toThrow(/история операций/)
  expect(() => parseHoldingsCsv(overview.replace('1,234.5000', '1,23.5000'))).toThrow(/Некорректное/)
  expect(() => parseHoldingsCsv(`${overview}\r\n"unclosed`)).toThrow(/Незакрытые кавычки/)
  expect(() => parseHoldingsCsv('Name,Amount\nBitcoin,-1')).toThrow(/Некорректное/)
  expect(() => parseHoldingsCsv('asset,quantity,asset_id\nEther,1,chain:eth\nETH,2,chain:eth')).toThrow(/Повтор актива/)
})

test('summaries merge only explicit stable IDs by default and use exact addition', () => {
  const make = (id: string, csv: string) => ({ ...parseHoldingsCsv(csv), name: id, id, sourceName: `${id}.csv` })
  const left = make('Left', 'asset,quantity,asset_id\nEthereum,0.000000000000000001,chain:eth\nToken,10,')
  const right = make('Right', 'asset,quantity,asset_id\nETH,0.999999999999999999,chain:eth\nToken,20,\nToken,5,chain:another-token')
  expect(summarizePortfolios([left, right], false).map(row => row.quantity)).toEqual(['1', '10', '20', '5'])
  expect(summarizePortfolios([left, right], true).map(row => row.quantity)).toEqual(['1', '30', '5'])
})

test('imports distinct portfolios together, summarizes a selection and updates a snapshot without doubling', async ({ page }) => {
  const apiRequests: string[] = []
  await page.route('**/api/**', route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/v1/auth/session') return route.fulfill({ json: { authenticated: false } })
    if (path === '/api/v1/public/market/coins') return route.fulfill({ json: [] })
    apiRequests.push(route.request().url())
    return route.abort()
  })
  await page.setViewportSize({ width: 390, height: 820 })
  await page.goto('/portfolios')
  await expect(page.getByRole('heading', { name: 'Мои портфели' })).toBeVisible()
  const input = page.getByLabel(fileLabel)
  const commit = page.getByRole('button', { name: 'Подтвердить импорт' })
  const summary = page.getByRole('table', { name: 'Сводка остатков' })
  const bitcoinRows = summary.getByRole('row').filter({ hasText: 'Bitcoin' })
  await input.setInputFiles([
    file('First_overview.csv', overview), file('Second.csv', 'asset,quantity\nBitcoin,0.874999999999999999'),
    file('unrelated.csv', transactions),
  ])
  await expect(page.getByText('Новых: 2. Замен: 0.', { exact: false })).toBeVisible()
  await expect(page.getByText(/unrelated.csv: Это история операций/)).toBeVisible()
  await expect(summary).toHaveCount(0)
  await commit.click()
  await expect(page.getByRole('status')).toHaveText('Добавлено: 2. Обновлено: 0.')
  await expect(bitcoinRows).toHaveCount(2)
  const group = page.getByRole('checkbox', { name: /Объединять позиции/ })
  await group.check()
  await expect(bitcoinRows.getByRole('cell', { name: '1', exact: true })).toBeVisible()
  await page.getByRole('checkbox', { name: /^Second/ }).uncheck()
  await expect(bitcoinRows.getByRole('cell', { name: '0.125000000000000001', exact: true })).toBeVisible()
  await page.getByRole('checkbox', { name: /^Second/ }).check()
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBe(0)

  await input.setInputFiles(file('First_overview (1).csv', 'Name,Amount\nBitcoin,3'))
  await expect(page.getByLabel('Куда импортировать файл 1').locator('option:checked')).toHaveText('Заменить: First')
  await expect(bitcoinRows.getByRole('cell', { name: '1', exact: true })).toBeVisible()
  await commit.click()
  await expect(page.getByRole('status')).toHaveText('Добавлено: 0. Обновлено: 1.')
  await expect(bitcoinRows.getByRole('cell', { name: '3.874999999999999999', exact: true })).toBeVisible()
  await expect(summary.getByText('Ether, example')).toHaveCount(0)

  await page.reload()
  await expect(page.getByRole('checkbox', { name: /^First/ })).toBeChecked()
  await expect(page.getByRole('checkbox', { name: /^Second/ })).toBeChecked()
  await expect(bitcoinRows).toHaveCount(2)
  await group.check()
  await expect(bitcoinRows.getByRole('cell', { name: '3.874999999999999999', exact: true })).toBeVisible()
  expect(apiRequests).toEqual([])
})

test('conflicting replacements and cross-tab changes do not overwrite saved snapshots', async ({ page, context }) => {
  await page.goto('/portfolios')
  const input = page.getByLabel(fileLabel)
  const commit = page.getByRole('button', { name: 'Подтвердить импорт' })
  await input.setInputFiles(file('First_overview.csv', 'Name,Amount\nBitcoin,1'))
  await commit.click()
  await expect(page.getByRole('table').getByRole('cell', { name: '1', exact: true })).toBeVisible()

  await input.setInputFiles([
    file('First_overview (1).csv', 'Name,Amount\nBitcoin,2'),
    file('First_overview (2).csv', 'Name,Amount\nBitcoin,3'),
  ])
  await commit.click()
  await expect(page.getByRole('alert')).toContainText('Два файла выбраны для замены одного портфеля')
  await expect(page.getByRole('table').getByRole('cell', { name: '1', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Убрать файл 2 из импорта' }).click()

  const second = await context.newPage()
  await second.goto('/portfolios')
  await second.getByLabel(fileLabel).setInputFiles(file('renamed.csv', 'Name,Amount\nBitcoin,4'))
  await second.getByLabel('Куда импортировать файл 1').selectOption({ label: 'Заменить: First' })
  await second.getByRole('button', { name: 'Подтвердить импорт' }).click()
  await expect(second.getByRole('table').getByRole('cell', { name: '4', exact: true })).toBeVisible()

  await commit.click()
  await expect(page.getByRole('alert')).toContainText('изменились в другой вкладке')
  await page.reload()
  await expect(page.getByRole('table').getByRole('cell', { name: '4', exact: true })).toBeVisible()
})

test('imports a ZIP as one portfolio named after the archive, ignoring its transactions CSV', async ({ page }) => {
  const apiRequests: string[] = []
  await page.route('**/api/**', route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/v1/auth/session') return route.fulfill({ json: { authenticated: false } })
    if (path === '/api/v1/public/market/coins') return route.fulfill({ json: [] })
    apiRequests.push(route.request().url())
    return route.abort()
  })
  await page.goto('/portfolios')
  const input = page.getByLabel(fileLabel)
  const commit = page.getByRole('button', { name: 'Подтвердить импорт' })
  const summary = page.getByRole('table')
  await input.setInputFiles([
    await archive('Synthetic_Portfolio.zip', [['data/other_transactions.csv', transactions], ['data/other_overview.csv', overview]]),
    file('Extra.csv', 'asset,quantity\nSynthetic coin,5'),
  ])
  await expect(page.getByLabel('Название портфеля 1')).toHaveValue('Synthetic_Portfolio')
  await expect(page.getByText('Остатки из архива: other_overview.csv')).toBeVisible()
  await expect(page.getByLabel('Название портфеля 2')).toHaveValue('Extra')
  await expect(page.getByRole('alert')).toHaveCount(0)
  await commit.click()
  await expect(page.getByRole('status')).toHaveText('Добавлено: 2. Обновлено: 0.')
  await expect(summary.getByRole('cell', { name: '0.125000000000000001', exact: true })).toBeVisible()
  await expect(summary.getByRole('cell', { name: '1', exact: true })).toHaveCount(0)

  await input.setInputFiles(await archive('Synthetic_Portfolio.zip', [['renamed_overview.csv', 'asset,quantity\nBitcoin,2']], 0))
  await expect(page.getByLabel('Куда импортировать файл 1').locator('option:checked')).toHaveText('Заменить: Synthetic_Portfolio')
  await commit.click()
  await expect(page.getByRole('status')).toHaveText('Добавлено: 0. Обновлено: 1.')
  await page.reload()
  await expect(summary.getByRole('cell', { name: '2', exact: true })).toBeVisible()
  await expect(page.getByRole('checkbox', { name: /^Synthetic_Portfolio/ })).toHaveCount(1)
  expect(apiRequests).toEqual([])
})

test('rejects ambiguous, missing, damaged and oversized ZIP entries without changing saved data', async ({ page }) => {
  await page.goto('/portfolios')
  const input = page.getByLabel(fileLabel)
  const commit = page.getByRole('button', { name: 'Подтвердить импорт' })
  await input.setInputFiles(file('Existing.csv', 'asset,quantity\nBitcoin,1'))
  await commit.click()
  await expect(page.getByRole('status')).toHaveText('Добавлено: 1. Обновлено: 0.')
  const tooLong = 'x'.repeat(1024 * 1024 + 1)
  await input.setInputFiles([
    await archive('Missing.zip', [['transactions.csv', transactions]]),
    await archive('Ambiguous.zip', [['first_overview.csv', overview], ['second_overview.csv', overview]]),
    { name: 'Damaged.zip', mimeType: 'application/zip', buffer: Buffer.from('not a zip') },
    await archive('Oversized.zip', [['overview.csv', tooLong]]),
  ])
  const warning = page.getByRole('alert')
  await expect(warning).toContainText('Missing.zip: В ZIP не найден')
  await expect(warning).toContainText('Ambiguous.zip: В ZIP несколько файлов обзора')
  await expect(warning).toContainText('Damaged.zip: Не удалось прочитать ZIP')
  await expect(warning).toContainText('Oversized.zip: Размер CSV с остатками после распаковки')
  await expect(commit).toHaveCount(0)
  await page.reload()
  await expect(page.getByRole('table').getByRole('cell', { name: '1', exact: true })).toBeVisible()
})
