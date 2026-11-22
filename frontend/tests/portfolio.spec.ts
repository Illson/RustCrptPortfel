import { expect, test } from '@playwright/test'

test('old demo entry points are unavailable; the local profile remains accessible', async ({ request, page }) => {
  const index = await request.get('/')
  expect(index.headers()['content-security-policy']).toContain("default-src 'self'")
  expect(index.headers()['content-security-policy']).toContain("frame-ancestors 'none'")
  for (const [method, path] of [
    ['POST', '/api/v1/auth/demo'],
    ['GET', '/api/v1/portfolio'],
    ['POST', '/api/v1/portfolio/sync'],
    ['GET', '/api/v1/feed'],
    ['GET', '/api/v2/feed'],
    ['GET', '/api/v1/auth/cmc/start'],
  ]) {
    const response = await request.fetch(path, { method })
    expect(response.status()).toBe(404)
    expect((await response.json()).code).toBe('not_found')
  }
  await page.goto('/demo')
  await expect(page.getByRole('heading', { name: 'Портфели', exact: true })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Открыть свои портфели' })).toBeVisible()
  await expect(page.getByRole('main')).not.toContainText(/демо/i)
})
