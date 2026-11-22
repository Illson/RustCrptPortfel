import { expect, test } from '@playwright/test'

const api = '/api/v1/internal/market/coinmarketcap'
const fetched = '2026-11-23T10:00:00Z'
const retry = '2026-11-23T10:00:30Z'

test('CoinMarketCap monitor requires a workspace session', async ({ request }) => {
  expect((await request.get(`${api}/monitor`)).status()).toBe(401)
  expect((await request.get(`${api}/charts/dash`)).status()).toBe(401)
  const session = await (await request.get('/api/v1/auth/session')).json()
  expect((await request.post('/api/v1/auth/demo', { headers: { 'X-CSRF-Token': session.csrf_token } })).status()).toBe(204)
  expect((await request.get(`${api}/monitor`)).status()).toBe(200)
  expect((await request.get(`${api}/charts/invalid_slug`)).status()).toBe(400)
})

test('cache and request journal do not fetch charts until explicitly requested', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 900 })
  let chartRequests = 0
  await page.route(`**${api}/monitor`, route => route.fulfill({ json: {
    as_of: fetched, global_next_attempt_at: retry,
    coins: [{ slug: 'dash', has_platform: true, fetched_at: null, next_attempt_at: null }],
    requests: [{ id: 'fixture', slug: 'dash', kind: 'platform', outcome: 'succeeded', http_status: 200, started_at: fetched, finished_at: fetched }],
  } }))
  await page.route(`**${api}/charts/dash`, route => {
    chartRequests++
    return route.fulfill({
      status: 202, json: { status: 'platform_cached' },
      headers: { 'Retry-After': '30', 'X-Chart-Next-Attempt-At': retry },
    })
  })
  await page.goto('/monitoring/coinmarketcap')
  await page.getByRole('button', { name: 'Открыть демо-портфель' }).click()
  await expect(page.getByRole('heading', { name: 'Кэш истории CoinMarketCap' })).toBeVisible()
  await expect(page.getByRole('cell', { name: 'ID найден' })).toBeVisible()
  await page.getByRole('link', { name: 'Журнал запросов', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Журнал запросов CoinMarketCap' })).toBeVisible()
  await expect(page.getByRole('table').getByRole('cell', { name: 'Поиск ID' })).toBeVisible()
  await expect(page.getByRole('table').getByRole('cell', { name: 'Успешно' })).toBeVisible()
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBe(0)
  await page.getByRole('button', { name: 'Обновить мониторинг' }).click()
  expect(chartRequests).toBe(0)
  await page.getByRole('link', { name: 'Кэш монет' }).click()
  await page.getByLabel('Slug монеты').fill('dash')
  await page.getByRole('button', { name: 'Запросить историю' }).click()
  await expect(page.getByRole('status')).toContainText('ID монеты сохранён.')
  expect(chartRequests).toBe(1)
})
