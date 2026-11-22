import { expect, test } from '@playwright/test'

const api = '/api/v1/internal/market/coinmarketcap'
const fetched = '2026-11-23T10:00:00Z'
const retry = '2026-11-23T10:00:30Z'

test('market tools protect the request log and the cached response', async ({ request }) => {
  expect((await request.get(`${api}/monitor`)).status()).toBe(401)
  expect((await request.get(`${api}/charts/dash`)).status()).toBe(401)
  expect((await request.get('/api/v1/internal/market/requests')).status()).toBe(401)
  expect((await request.get(`${api}/cached/dash`)).status()).toBe(401)
  expect((await request.post('/api/v1/auth/market-tools')).status()).toBe(403)
  const session = await (await request.get('/api/v1/auth/session')).json()
  expect(session).toMatchObject({ market_tools_available: true, market_tools_authenticated: false })
  expect((await request.post('/api/v1/auth/market-tools', { headers: { 'X-CSRF-Token': session.csrf_token } })).status()).toBe(204)
  const local = await (await request.get('/api/v1/auth/session')).json()
  expect(local).toMatchObject({ market_tools_authenticated: true })
  expect((await request.get(`${api}/monitor`)).status()).toBe(200)
  expect((await request.get(`${api}/charts/invalid_slug`)).status()).toBe(400)
  expect((await request.get(`${api}/cached/fixture-not-fetched`)).status()).toBe(404)
  expect((await (await request.get('/api/v1/internal/market/requests')).json()).requests).toBeInstanceOf(Array)
  expect((await request.post('/api/v1/auth/logout', { headers: { 'X-CSRF-Token': local.csrf_token } })).status()).toBe(204)
  expect((await request.get('/api/v1/internal/market/requests')).status()).toBe(401)
})

test('cache and request journal do not fetch charts until explicitly requested', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 900 })
  let chartRequests = 0
  await page.route('**/api/v1/internal/market/requests', route => route.fulfill({ json: {
    as_of: fetched, global_next_attempt_at: retry,
    coins: [{ slug: 'dash', has_platform: true, fetched_at: fetched, next_attempt_at: null }],
    requests: [
      { id: 'fixture-cmc', provider: 'CoinMarketCap', slug: 'dash', kind: 'platform', outcome: 'succeeded', http_status: 200,
        started_at: fetched, finished_at: fetched, response: { cryptocurrency_id: 131 } },
      { id: 'fixture-cg', provider: 'CoinGecko', slug: null, kind: 'quotes', outcome: 'succeeded', http_status: 200,
        started_at: fetched, finished_at: fetched, response: { requested: 15, available: 10 } },
      { id: 'fixture-limit', provider: 'CoinGecko', slug: null, kind: 'quotes', outcome: 'rate_limited', http_status: 429,
        started_at: fetched, finished_at: fetched, response: { error: 'rate_limited' } },
    ],
  } }))
  await page.route(`**${api}/charts/dash`, route => {
    chartRequests++
    return route.fulfill({
      status: 202, json: { status: 'platform_cached' },
      headers: { 'Retry-After': '30', 'X-Chart-Next-Attempt-At': retry },
    })
  })
  await page.goto('/monitoring/requests')
  await page.getByRole('button', { name: 'Включить рыночные инструменты' }).click()
  await expect(page.getByRole('heading', { name: 'Журнал рыночных запросов' })).toBeVisible()
  await expect(page.getByRole('cell', { name: '429', exact: true })).toBeVisible()
  await expect(page.getByText('Котировок: 10 из 15')).toBeVisible()
  await page.getByText('Котировок: 10 из 15').click()
  await expect(page.getByText(/"available": 10/)).toBeVisible()
  await page.getByLabel('Источник').selectOption('CoinMarketCap')
  await expect(page.getByRole('table').getByRole('cell', { name: 'Поиск ID', exact: false })).toBeVisible()
  await expect(page.getByRole('cell', { name: '429', exact: true })).toHaveCount(0)
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBe(0)
  await page.getByRole('button', { name: 'Обновить мониторинг' }).click()
  expect(chartRequests).toBe(0)
  await page.getByRole('link', { name: 'Кэш монет' }).click()
  await expect(page.getByRole('heading', { name: 'Кэш истории CoinMarketCap' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Открыть JSON' })).toHaveAttribute('href', `${api}/cached/dash`)
  await page.getByLabel('Slug монеты').fill('dash')
  await page.getByRole('button', { name: 'Запросить историю' }).click()
  await expect(page.getByRole('status')).toContainText('HTTP 202: ID монеты сохранён.')
  expect(chartRequests).toBe(1)
})
