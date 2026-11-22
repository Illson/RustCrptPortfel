import { expect, test, type Page } from '@playwright/test'

async function go(page: Page, name: string) {
  const navigation = page.getByRole('navigation', { name: 'Основная навигация' })
  const toggle = page.getByRole('button', { name: 'Меню', exact: true })
  if (await toggle.getAttribute('aria-expanded') === 'false') await toggle.click()
  await navigation.getByRole('link', { name, exact: true }).click()
  if (page.viewportSize()!.width < 768) {
    await expect(toggle).toHaveAttribute('aria-expanded', 'false')
    await expect(page.getByRole('dialog', { name: 'Меню', exact: true })).toHaveCount(0)
  }
}

async function noHorizontalOverflow(page: Page) {
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBe(0)
}

for (const width of [390, 1440]) {
  test(`portfolio, coin, feed, sources and status at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 })
    const errors: string[] = []
    page.on('pageerror', error => errors.push(error.message))
    await page.goto('/')
    await expect(page.getByRole('button', { name: 'Войти через CoinMarketCap' })).toBeDisabled()
    await expect(page.getByText('BLOCKED_CMC_AUTH', { exact: true })).toBeVisible()
    await expect(page.getByText('BLOCKED_CMC_ACCESS', { exact: true })).toBeVisible()
    await page.getByRole('button', { name: 'Открыть демо-портфель' }).click()
    await expect(page.getByRole('heading', { name: 'Портфель', exact: true })).toBeVisible()
    await expect(page.getByRole('link', { name: 'Solana SOL' })).toBeVisible({ timeout: 15_000 })
    await noHorizontalOverflow(page)
    const table = page.getByRole('table')
    await expect(table.getByText('Количество', { exact: true })).toBeVisible()
    if (width === 390)
      expect(await table.evaluate(node => node.parentElement!.scrollWidth > node.parentElement!.clientWidth)).toBe(true)
    await page.getByRole('link', { name: 'Solana SOL' }).click()
    await page.getByRole('tab', { name: 'Экономика' }).click()
    await expect(page.getByText('Выручка сети', { exact: true })).toBeVisible()
    await expect(page.getByText('Выручка приложений', { exact: true })).toBeVisible()
    await noHorizontalOverflow(page)
    await page.getByRole('tab', { name: 'Предложение' }).click()
    await expect(page.getByRole('cell', { name: '365 дней', exact: true })).toBeVisible()
    await page.getByRole('tab', { name: 'Проект и события' }).click()
    await expect(page.getByText('Предыдущая версия', { exact: true })).toBeVisible()

    await go(page, 'Лента')
    await expect(page.getByRole('heading', { name: 'Лента', exact: true })).toBeVisible()
    await expect(page.getByText('SOL · демонстрационный обзор', { exact: true })).toBeVisible({ timeout: 15_000 })
    await page.getByRole('button', { name: 'Открыть анализ' }).first().click()
    await expect(page.getByRole('dialog').getByText('Факты и источники')).toBeVisible()
    await page.keyboard.press('Escape')
    await expect(page.getByRole('dialog')).toHaveCount(0)
    if (width === 390) {
      await page.getByRole('button', { name: 'Сообщить об ошибке' }).first().click()
      await page.getByLabel('Что нужно исправить?').fill('Синтетическое замечание для проверки')
      await page.getByRole('button', { name: 'Сохранить замечание' }).click()
      await expect(page.getByText('Замечание сохранено.', { exact: true })).toBeVisible()
      await page.keyboard.press('Escape')
      await expect(page.getByRole('dialog')).toHaveCount(0)
    }
    await noHorizontalOverflow(page)
    await go(page, 'Источники')
    await expect(page.getByText('CoinMarketCap · портфель', { exact: true })).toBeVisible()
    await expect(page.getByText('Чтение портфеля недоступно', { exact: true })).toBeVisible()
    await go(page, 'AI-подключения')
    await expect(page.getByRole('button', { name: 'Подключить ChatGPT' })).toBeDisabled()
    await noHorizontalOverflow(page)
    expect(errors).toEqual([])
  })
}

test('session, CSRF, idempotency and API fallback', async ({ request }) => {
  const index = await request.get('/')
  expect(index.ok()).toBe(true)
  expect(index.headers()['content-security-policy']).toContain("default-src 'self'")
  expect(index.headers()['content-security-policy']).toContain("frame-ancestors 'none'")
  const root = '/api/v1'
  expect((await request.get(`${root}/portfolio`)).status()).toBe(401)
  expect((await (await request.get(`${root}/auth/cmc/start`)).json()).code).toBe('CMC_AUTH_UNVERIFIED')
  expect((await request.get(`${root}/internal/analysis-snapshots/test`)).status()).toBe(404)
  let session = await (await request.get(`${root}/auth/session`)).json()
  expect((await request.post(`${root}/auth/demo`)).status()).toBe(403)
  const signIn = await request.post(`${root}/auth/demo`, { headers: { 'X-CSRF-Token': session.csrf_token } })
  expect(signIn.status()).toBe(204)
  session = await (await request.get(`${root}/auth/session`)).json()
  expect(session.authenticated).toBe(true)
  expect((await request.post(`${root}/portfolio/sync`)).status()).toBe(403)
  const headers = { 'X-CSRF-Token': session.csrf_token, 'Idempotency-Key': `e2e-${Date.now()}` }
  const queued = await Promise.all(Array.from({ length: 5 }, () => request.post(`${root}/portfolio/sync`, { headers })))
  expect(queued.map(response => response.status())).toEqual([202, 202, 202, 202, 202])
  const ids = (await Promise.all(queued.map(response => response.json()))).map(receipt => receipt.job_id)
  expect(new Set(ids).size).toBe(1)
  expect((await (await request.post(`${root}/portfolio/sync`, { headers, data: {} })).json()).job_id).toBe(ids[0])
  expect((await request.post(`${root}/portfolio/sync`, { headers, data: { invalid: true } })).status()).toBe(409)
  expect((await request.post(`${root}/auth/logout`, { headers })).status()).toBe(204)
  expect((await request.get(`${root}/portfolio`)).status()).toBe(401)
})
