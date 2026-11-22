import { expect, test } from '@playwright/test'
import { parseHoldingsCsv } from '../src/lib/csv-portfolios'

const bookKey = 'portfel.csv-portfolio-book.v1'
const book = { version: 1, portfolios: [
  { id: 'first', name: 'Основной', sourceName: 'first.csv', ...parseHoldingsCsv('asset,quantity\nBitcoin,2\nНеизвестный актив,5') },
  { id: 'second', name: 'Резерв', sourceName: 'second.csv', ...parseHoldingsCsv('asset,quantity\nDash,3') },
] }

test('one profile switches between saved portfolios and their summary', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 850 })
  await page.addInitScript(({ bookKey, book }) => {
    if (!localStorage.getItem(bookKey)) localStorage.setItem(bookKey, JSON.stringify(book))
  }, { bookKey, book })
  await page.route('**/api/v1/public/market/coins', route => route.fulfill({ json: [
    { id: 'bitcoin', name: 'Bitcoin', symbol: 'btc' }, { id: 'dash', name: 'Dash', symbol: 'dash' },
  ] }))
  await page.route('**/api/v1/public/market/quotes?*', route => {
    const url = new URL(route.request().url())
    const ids = url.searchParams.get('ids')!.split(',')
    expect(ids.every(id => ['bitcoin', 'dash'].includes(id))).toBe(true)
    expect([...url.searchParams.keys()]).toEqual(['ids'])
    expect(route.request().postData()).toBeNull()
    return route.fulfill({ json: ids.map(id => ({ id, usd: '10', as_of: new Date().toISOString() })) })
  })
  await page.goto('/')
  await page.getByLabel('Имя профиля').fill('Личный профиль')
  await page.getByRole('button', { name: 'Сохранить имя' }).click()
  await expect(page.getByRole('status')).toContainText('Имя профиля сохранено.')
  await page.getByRole('link', { name: /Основной/ }).click()
  await expect(page.getByRole('heading', { name: 'Основной' })).toBeVisible()
  const picker = page.getByLabel('Выбрать портфель')
  const positions = page.getByRole('table', { name: 'Сводка остатков' })
  const value = page.getByTestId('portfolio-value')
  await expect(value).toHaveAttribute('title', '20')
  await expect(page.getByText('Не все монеты распознаны.', { exact: false })).toBeVisible()
  await expect(positions.getByText('Dash', { exact: true })).toHaveCount(0)
  await page.getByLabel('Поиск по позициям').fill('BTC')
  await expect(positions.getByRole('row')).toHaveCount(2)
  await expect(positions.getByText('Bitcoin', { exact: true })).toBeVisible()
  await page.getByLabel('Поиск по позициям').clear()
  await page.getByRole('checkbox', { name: 'Только без оценки' }).check()
  await expect(positions.getByText('Неизвестный актив')).toBeVisible()
  await expect(positions.getByText('Bitcoin')).toHaveCount(0)
  await expect(value).toHaveAttribute('title', '20')
  await page.getByRole('checkbox', { name: 'Только без оценки' }).uncheck()

  await picker.selectOption({ label: 'Все мои портфели' })
  await expect(value).toHaveAttribute('title', '50')
  await expect(positions.getByText('Solana')).toHaveCount(0)
  await expect(picker.locator('option').filter({ hasText: /демо/i })).toHaveCount(0)
  await picker.selectOption({ label: 'Резерв' })
  await expect(page.getByRole('heading', { name: 'Резерв' })).toBeVisible()
  await expect(value).toHaveAttribute('title', '30')
  await page.getByText('Подсказки по портфелю', { exact: true }).click()
  await page.getByRole('link', { name: 'Все подсказки' }).click()
  await expect(page.getByRole('heading', { name: 'Подсказки' })).toBeVisible()
  await expect(page.getByText('Не складывайте два снимка одних и тех же средств.', { exact: false })).toBeVisible()
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBe(0)
  expect(await page.evaluate(key => JSON.parse(localStorage.getItem(key)!), bookKey)).toEqual(book)
  await page.goto('/')
  await expect(page.getByLabel('Имя профиля')).toHaveValue('Личный профиль')
})

test('the profile and import remain accessible when the API is unavailable', async ({ page }) => {
  await page.route('**/api/**', route => route.abort())
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Портфели', exact: true })).toBeVisible()
  await page.getByRole('link', { name: 'Открыть свои портфели' }).click()
  await page.getByLabel('Файлы портфелей (CSV или ZIP)').setInputFiles({
    name: 'Local.csv', mimeType: 'text/csv', buffer: Buffer.from('asset,quantity\nExample,3'),
  })
  await page.getByRole('button', { name: 'Подтвердить импорт' }).click()
  await expect(page.getByRole('table').getByRole('cell', { name: '3', exact: true })).toBeVisible()
  await page.getByLabel('Выбрать портфель').selectOption({ label: 'Local' })
  await expect(page.getByRole('heading', { name: 'Local' })).toBeVisible()
  await page.reload()
  await expect(page.getByRole('heading', { name: 'Local' })).toBeVisible()
})
