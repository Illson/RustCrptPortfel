import { expect, test } from '@playwright/test'
import { parseHoldingsCsv, summarizePortfolios } from '../src/lib/csv-portfolios'
import { makeCoinIndex, resolveCoin, valuePortfolio } from '../src/lib/portfolio-valuation'

const now = '2026-11-23T12:00:00Z'
const bitcoin = { id: 'bitcoin', name: 'Bitcoin', symbol: 'btc' }
const alpha = { id: 'alpha-one', name: 'Alpha', symbol: 'x' }
const alpha2 = { id: 'alpha-two', name: 'Alpha', symbol: 'xx' }
const coins = [bitcoin, alpha, alpha2]
const holdings = 'asset,quantity,asset_id\nBitcoin,0.125000000000000001,\nAlpha,2,\nDifferent Name,3,coingecko:alpha-two\nBTC,0,'
const bookKey = 'portfel.csv-portfolio-book.v1'
const book = { version: 1, portfolios: [
  { id: 'synthetic', name: 'Synthetic', sourceName: 'Synthetic.csv', ...parseHoldingsCsv(holdings) },
] }
const summary = summarizePortfolios(book.portfolios, false)

test('valuation consumes only fresh quotes, preserves exact values and never guesses from a ticker', () => {
  const index = makeCoinIndex(coins)
  const rows = summary.map(row => ({ row, coin: resolveCoin(row, index, {}) }))
  expect(Object.fromEntries(rows.map(({ row, coin }) => [row.name, coin?.id]))).toEqual({
    Alpha: undefined, Bitcoin: 'bitcoin', BTC: undefined, 'Different Name': 'alpha-two',
  })
  const quotes = new Map([
    ['bitcoin', { id: 'bitcoin', usd: '80000', as_of: now }],
    ['alpha-two', { id: 'alpha-two', usd: '10', as_of: '2026-11-23T11:44:59Z' }],
  ])
  expect(valuePortfolio(rows, quotes, Date.parse(now))).toMatchObject({
    total: '10000.00000000000008', complete: false, covered: 2,
  })
  expect(valuePortfolio(rows, quotes, Date.parse(now)).rows.every(row => row.share === null)).toBe(true)
  const bitcoinAt = (offsetMs: number) => valuePortfolio(rows, quotes, Date.parse(now) + offsetMs).rows.find(row => row.coin?.id === 'bitcoin')
  expect(bitcoinAt(-60_000)).toMatchObject({ status: 'ok', quote: { as_of: now } })
  expect(bitcoinAt(-60_001)?.status).toBe('stale')
  expect(bitcoinAt(15 * 60_000)?.status).toBe('ok')
  expect(bitcoinAt(15 * 60_000 + 1)?.status).toBe('stale')

  const complete = rows.map(row => row.row.name === 'Alpha' ? { ...row, coin: alpha } : row)
  quotes.set(alpha.id, { id: alpha.id, usd: '15', as_of: now })
  quotes.set(alpha2.id, { id: alpha2.id, usd: '10', as_of: now })
  const valuation = valuePortfolio(complete, quotes, Date.parse(now))
  expect(valuation).toMatchObject({ total: '10060.00000000000008', complete: true, covered: 4 })
  expect(valuation.rows.find(row => row.coin?.id === 'bitcoin')?.share).toBe('99.4')
})

test('canonical names match verified listings rather than lookalikes', () => {
  const rows = summarizePortfolios([
    { id: 'safe', name: 'Safe', sourceName: 'Safe.csv', ...parseHoldingsCsv('asset,quantity\nDash,1\nTerra Classic,1') },
  ], false)
  const listings = [
    { id: 'dash-fork', name: 'Dash', symbol: 'dash' },
    { id: 'dash', name: 'Dash', symbol: 'dash' },
    { id: 'terra-luna', name: 'Terra Luna Classic', symbol: 'lunc' },
    { id: 'terra-luna-2', name: 'Terra', symbol: 'luna' },
  ]
  expect(rows.map(row => resolveCoin(row, makeCoinIndex(listings), {})?.id)).toEqual(['dash', 'terra-luna'])
  expect(resolveCoin(rows[0], makeCoinIndex(listings.filter(coin => coin.id !== 'dash')), {})).toBeUndefined()
})

test('prices already saved portfolios, resolves ambiguity locally and keeps fresh values if refresh fails', async ({ page }) => {
  await page.clock.install({ time: new Date(now) })
  await page.addInitScript(({ bookKey, book }) => {
    if (!localStorage.getItem(bookKey)) localStorage.setItem(bookKey, JSON.stringify(book))
  }, { bookKey, book })
  const requests: string[] = []
  let fail = false
  await page.route('**/api/**', async route => {
    const incoming = route.request()
    const url = new URL(incoming.url())
    if (url.pathname === '/api/v1/auth/session') return route.abort()
    requests.push(url.pathname + url.search)
    expect(incoming.method()).toBe('GET')
    expect(incoming.postData()).toBeNull()
    if (url.pathname.endsWith('/coins')) {
      expect(url.search).toBe('')
      return route.fulfill({ json: coins })
    }
    expect(url.pathname).toBe('/api/v1/public/market/quotes')
    const values = url.searchParams.get('ids')!.split(',')
    expect(values.every(id => coins.some(coin => coin.id === id))).toBe(true)
    expect([...url.searchParams.keys()]).toEqual(['ids'])
    if (fail) return route.fulfill({ status: 429, json: { message: 'Лимит запросов' } })
    return route.fulfill({ json: values.map(id => ({
      id, usd: id === 'bitcoin' ? '80000' : '10', as_of: id === 'bitcoin' ? '2026-11-23T12:00:27Z' : now,
    })) })
  })
  await page.setViewportSize({ width: 390, height: 900 })
  await page.goto('/portfolios')
  const total = page.getByTestId('portfolio-value')
  await expect(total).toHaveAttribute('title', '10030.00000000000008')
  await expect(page.getByText('Оценено позиций: 3 из 4.', { exact: false })).toBeVisible()
  const alphaRow = page.getByRole('row', { name: /^Alpha(?:\s|$)/ })
  await expect(alphaRow).toContainText('Нет сопоставления')
  await alphaRow.getByRole('button', { name: 'Выбрать монету' }).click()
  await alphaRow.getByRole('button', { name: 'Alpha (X) · alpha-one' }).click()
  await expect(total).toHaveAttribute('title', '10050.00000000000008')
  await expect(page.getByRole('heading', { name: /^Общая стоимость/ })).toBeVisible()
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBe(0)
  await page.reload()
  await expect(total).toHaveAttribute('title', '10050.00000000000008')
  const stored = await page.evaluate(key => JSON.parse(localStorage.getItem(key)!), bookKey)
  expect(stored).toEqual(book)

  fail = true
  await page.getByRole('button', { name: 'Обновить котировки' }).click()
  await expect(page.getByRole('alert')).toContainText('Лимит запросов')
  await expect(total).toHaveAttribute('title', '10050.00000000000008')
  await page.clock.fastForward(16 * 60 * 1000)
  await expect(page.getByRole('table').getByText('Устарела')).toHaveCount(3)
  await expect(total).not.toHaveAttribute('title')
  expect(requests.filter(url => url.startsWith('/api/v1/public/market/quotes?')).length).toBeGreaterThanOrEqual(3)
})
