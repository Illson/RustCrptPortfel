import { expect, test } from '@playwright/test'

const api = '/api/v1/chatgpt-lab'
const pagePath = '/chatgpt-lab'
const source = 'https://issuer.example.org/updates'

test('ChatGPT lab selects a coin, queues an opinion and keeps the balance in the browser', async ({ page }) => {
  let posted = false
  let reads = 0
  const job = { id: '', coin_id: 'solana', model: 'fixture-web', created_at: '2026-11-23T12:00:00Z', status: 'queued', error: null, post: null, sources: null }
  const completed = () => ({ ...job, status: 'succeeded', sources: [{ url: source, title: 'Объявление проекта' }],
    decision: { action: 'hold', reason: 'Проект выпускает обновления и торги продолжаются.', revisit: 'Если уберут выкупы из протокола.' },
    post: { headline: 'Обзор Solana', summary: 'Проверены публичные данные.', opinion: 'Риски зависят от сроков обновлений.',
      watch: 'Следить за объявлениями.', sections: [
        { block: 'economics', status: 'insufficient', text: 'Нет надёжных данных.', source_urls: [] },
        { block: 'supply', status: 'insufficient', text: 'Оценка недоступна.', source_urls: [] },
        { block: 'markets', status: 'insufficient', text: 'Недостаточно данных о ликвидности.', source_urls: [] },
        { block: 'project', status: 'reported', text: 'Команда сообщила об обновлении.', source_urls: [source] },
      ] } })
  await page.route(`**${api}/**`, async route => {
    const request = route.request()
    const part = new URL(request.url()).pathname.slice(api.length)
    if (part === '/session') return route.fulfill({ json: { status: 'connected', email: null, csrf_token: 'fixture-csrf' } })
    if (part === '/models') return route.fulfill({ json: [{ id: 'fixture-web', name: 'Web Fixture' }] })
    if (part === '/jobs' && request.method() === 'POST') {
      const body = request.postDataJSON()
      expect(body).toEqual({ id: expect.any(String), coin_id: 'solana', model: 'fixture-web' })
      expect(request.headers()['x-csrf-token']).toBe('fixture-csrf')
      posted = true
      job.id = body.id
      return route.fulfill({ status: 202, json: job })
    }
    expect(part).toBe('/jobs')
    return route.fulfill({ json: !posted ? [] : ++reads === 1 ? [{ ...job, status: 'running' }] : [completed()] })
  })
  await page.goto(`${pagePath}?connection=connected`)
  await expect(page.getByRole('dialog')).toContainText('Используется подписка ChatGPT')
  await page.getByRole('button', { name: 'Понятно' }).click()
  await page.getByRole('combobox', { name: 'Монета', exact: true }).selectOption('solana')
  await page.getByLabel('Количество на балансе (необязательно)').fill('12,500000000000000001')
  await page.getByRole('button', { name: 'Сформировать мнение' }).click()
  await expect(page.getByText('Задание отправлено.', { exact: false })).toBeVisible()
  await expect(page.getByText('Обзор Solana')).toBeVisible()
  await expect(page.getByText('12,500000000000000001')).toBeVisible()
  await expect(page.getByText('Удерживаем', { exact: true })).toBeVisible()
  await expect(page.getByText('Проект выпускает обновления и торги продолжаются.', { exact: false })).toBeVisible()
  await expect(page.getByText('Если уберут выкупы из протокола.', { exact: false })).toBeVisible()
  await expect(page.getByText('Мнение и риски')).toBeHidden()
  await page.getByText('Аргументы и источники', { exact: true }).click()
  await expect(page.getByText('Мнение и риски')).toBeVisible()
  await expect(page.getByRole('link', { name: 'Объявление проекта' })).toHaveAttribute('href', source)
  await expect(page.getByText('Недостаточно данных', { exact: true })).toHaveCount(3)
  await page.reload()
  await expect(page.getByText('Обзор Solana')).toBeVisible()
  await expect(page.getByText('12,500000000000000001')).toBeVisible()
  await expect(page.getByRole('dialog')).toHaveCount(0)
})

test('local lab OAuth uses PKCE and cannot complete from another browser or repeat a state', async ({ request, playwright, baseURL }) => {
  test.skip(process.env.PORTFEL_E2E_CHATGPT_LAB !== '1', 'Run local.cmd test -ChatGptLab to enable HTTP smoke test')
  const session = await request.get(`${api}/session`)
  expect(session.ok()).toBeTruthy()
  expect(session.headers()['set-cookie']).toContain('httponly')
  const { status, csrf_token } = await session.json()
  expect(status).toBe('not_connected')
  expect((await request.post(`${api}/connect`)).status()).toBe(403)
  const headers = { 'X-CSRF-Token': csrf_token }
  const start = await request.post(`${api}/connect`, { headers })
  expect(start.ok()).toBeTruthy()
  const auth = new URL((await start.json()).url)
  expect(auth.origin + auth.pathname).toBe('https://auth.openai.com/api/accounts/authorize')
  expect(auth.searchParams.get('code_challenge_method')).toBe('S256')
  expect(auth.searchParams.get('code_challenge')).toMatch(/^[\w-]{43}$/)
  expect(auth.searchParams.get('nonce')).toMatch(/^[\w-]{43}$/)
  expect(auth.searchParams.get('client_id')).toBe('dynamic_agent_client')
  expect(auth.searchParams.get('agent_name_hint')).toBe('dotnetCrptPortfel')
  expect(auth.searchParams.get('scope')).toContain('chatgpt.tokens.use.direct')
  expect(auth.searchParams.get('redirect_uri')).toBe(`${baseURL}${api}/callback`)

  const state = auth.searchParams.get('state')!
  const callback = `${api}/callback?error=access_denied&state=${state}`
  const another = await playwright.request.newContext({ baseURL })
  try {
    await another.get(`${api}/session`)
    expect((await another.get(callback, { maxRedirects: 0 })).headers().location).toContain('connection=invalid_state')
  } finally { await another.dispose() }
  expect((await request.get(callback, { maxRedirects: 0 })).headers().location).toContain('connection=denied')
  expect((await request.get(callback, { maxRedirects: 0 })).headers().location).toContain('connection=invalid_state')
  const data = { id: '00000000-0000-4000-a000-000000000001', coin_id: 'solana', model: 'fixture' }
  const unexpectedField = await request.post(`${api}/jobs`, { headers, data: { ...data, balance: '123' } })
  expect(unexpectedField.status()).toBe(422)
  expect((await unexpectedField.json()).code).toBe('invalid_request')
  expect((await request.post(`${api}/jobs`, { headers, data })).status()).toBe(401)
  const batch = { id: data.id, model: data.model, coin_ids: ['bitcoin', 'solana'] }
  const duplicates = await request.post(`${api}/batches`, { headers, data: { ...batch, coin_ids: ['solana', 'solana'] } })
  expect((await duplicates.json()).code).toBe('lab_bad_batch')
  const privateFields = await request.post(`${api}/batches`, { headers, data: { ...batch, portfolio_name: 'private', balances: ['1', '2'] } })
  expect((await privateFields.json()).code).toBe('invalid_request')
  expect((await request.post(`${api}/batches`, { headers, data: batch })).status()).toBe(401)
  expect((await request.post(`${api}/batches/${data.id}/cancel`, { headers })).status()).toBe(404)
  expect(await (await request.get(`${api}/jobs`)).json()).toEqual([])
})

test('chatgpt lab shows stream diagnostics and the raw model fragment without publishing it', async ({ page }) => {
  await page.route(`**${api}/**`, route => {
    if (new URL(route.request().url()).pathname.endsWith('/session'))
      return route.fulfill({ json: { status: 'not_connected', email: null, csrf_token: 'fixture-csrf' } })
    return route.fulfill({ json: [{
      id: 'fixture-error', coin_id: 'bitcoin', model: 'fixture-web', status: 'failed', created_at: '2026-11-23T00:10:00Z',
      error: 'plan_invalid_sse', provider_http_status: 200, provider_request_id: 'req_fixture', post: null, sources: null,
      raw_output: { text: '{"headline":"<b>Незавершённый текст</b>"}', partial: true, truncated: false },
    }] })
  })
  await page.goto(pagePath)
  await expect(page.getByText('В потоке ChatGPT повреждено событие ответа.', { exact: false })).toBeVisible()
  await page.getByText('Диагностика', { exact: true }).click()
  await expect(page.getByText('plan_invalid_sse · HTTP 200 · req_fixture', { exact: true })).toBeVisible()
  await page.getByText('Ответ GPT', { exact: true }).click()
  await expect(page.getByText('Полученный фрагмент. Этот текст не опубликован как пост.', { exact: true })).toBeVisible()
  await expect(page.locator('pre')).toHaveText('{"headline":"<b>Незавершённый текст</b>"}')
  await expect(page.locator('pre b')).toHaveCount(0)
  await expect(page.getByText('Мнение и риски', { exact: true })).toHaveCount(0)
})

test('a well-formed but incomplete response is readable as an unlinked draft', async ({ page }) => {
  await page.route(`**${api}/**`, route => {
    if (new URL(route.request().url()).pathname.endsWith('/session'))
      return route.fulfill({ json: { status: 'not_connected', email: null, csrf_token: 'fixture-csrf' } })
    return route.fulfill({ json: [{
      id: 'fixture-draft', coin_id: 'solana', model: 'fixture-web', status: 'failed', created_at: '2026-11-23T00:10:00Z',
      error: 'incomplete_response', provider_http_status: 200, provider_terminal_state: 'response.incomplete / incomplete',
      provider_request_id: 'req_fixture', post: null, sources: null,
      draft: { headline: 'Черновой обзор Solana', summary: 'Сводка модели.', opinion: 'Мнение модели.', watch: 'Наблюдение.',
        sections: [{ block: 'project', status: 'reported', text: 'Не подтверждённое приложение.', source_urls: [source] }] },
    }] })
  })
  await page.goto(pagePath)
  await expect(page.getByText('Черновой обзор Solana')).toBeVisible()
  await expect(page.getByText('Черновик', { exact: true })).toBeVisible()
  await expect(page.getByText('Пост не опубликован', { exact: true })).toBeVisible()
  await page.getByText('Аргументы и источники', { exact: true }).click()
  await expect(page.getByText('Утверждения и ссылки не проверены.', { exact: false })).toBeVisible()
  await expect(page.getByText('По источникам', { exact: true })).toHaveCount(0)
  await expect(page.locator(`a[href="${source}"]`)).toHaveCount(0)
  await page.getByText('Диагностика', { exact: true }).click()
  await expect(page.getByText('response.incomplete / incomplete', { exact: false })).toBeVisible()
})

test('portfolio screening sends only resolved coin IDs and puts the adverse verdict first', async ({ page }) => {
  await page.addInitScript(() => {
    window.localStorage.setItem('portfel.csv-portfolio-book.v1', JSON.stringify({
      version: 1, portfolios: [{
        id: 'fixture', name: 'Локальное название', sourceName: 'fixture.csv', exportedAt: null,
        holdings: [
          { name: 'Bitcoin', assetId: 'coingecko:bitcoin', quantity: '211.000001' },
          { name: 'Ethereum', assetId: 'coingecko:ethereum', quantity: '47' },
          { name: 'Solana', assetId: null, quantity: '31' },
          { name: 'Solana from another wallet label', assetId: 'coingecko:solana', quantity: '1' },
          { name: 'Ambiguous', assetId: null, quantity: '7' },
          { name: 'Empty', assetId: 'coingecko:empty', quantity: '0' },
        ],
      }],
    }))
  })
  await page.route('**/api/v1/public/market/coins', route => {
    expect(route.request().postData()).toBeNull()
    return route.fulfill({ json: [
      { id: 'bitcoin', name: 'Bitcoin', symbol: 'btc' }, { id: 'ethereum', name: 'Ethereum', symbol: 'eth' },
      { id: 'solana', name: 'Solana', symbol: 'sol' },
      { id: 'ambi-1', name: 'Ambiguous', symbol: 'a' }, { id: 'ambi-2', name: 'Ambiguous', symbol: 'a2' },
      { id: 'empty', name: 'Empty', symbol: 'e' },
    ] })
  })
  const verdict = (classification: string) => ({
    classification, confidence: 'medium', thesis: 'Основной тезис.', counterpoint: 'Сильный контраргумент.',
    invalidation: 'Будет пересмотрено после релиза.', basis_blocks: ['project'],
  })
  let submitted: { id: string; coin_ids: string[]; model: string } | null = null
  function rows(status: 'queued' | 'succeeded') {
    return (submitted?.coin_ids ?? []).map((id, index) => ({
      id: `job-${index}`, batch_id: submitted!.id, coin_id: id, model: 'fixture-web', status, error: null, sources: null,
      created_at: '2026-11-23T12:00:00Z',
      decision: status === 'succeeded' ? { action: id === 'solana' ? 'sell' : id === 'ethereum' ? 'add' : 'hold',
        reason: `Короткая причина для ${id}.` } : null,
      post: status === 'succeeded' ? { headline: `Обзор: ${id}`, summary: 'Сводка.', opinion: 'Мнение.', watch: 'Что наблюдать.',
        sections: [{ block: 'project', status: 'reported', text: 'Дата метрики.', source_urls: [], data_as_of: '2026-11-20' }],
        verdict: verdict(id === 'solana' ? 'exit_review' : 'hold_case') } : null,
    }))
  }
  await page.route(`**${api}/**`, async route => {
    const part = new URL(route.request().url()).pathname.slice(api.length)
    if (part === '/session') return route.fulfill({ json: { status: 'connected', email: null, csrf_token: 'fixture-csrf' } })
    if (part === '/models') return route.fulfill({ json: [{ id: 'fixture-web', name: 'Web Fixture' }] })
    if (part === '/batches') {
      submitted = route.request().postDataJSON()
      expect(submitted).toEqual({ id: expect.any(String), coin_ids: ['bitcoin', 'ethereum', 'solana'], model: 'fixture-web' })
      return route.fulfill({ status: 202, json: rows('queued') })
    }
    expect(part).toBe('/jobs')
    return route.fulfill({ json: rows('succeeded') })
  })
  await page.goto(pagePath)
  await page.getByRole('combobox', { name: 'Что анализировать' }).selectOption('portfolio')
  await expect(page.getByText('Без сопоставления: 1.', { exact: false })).toBeVisible()
  await page.getByRole('button', { name: 'Запустить обзор портфеля (3)' }).click()
  await expect(page.getByText('Готово: 3 из 3', { exact: false })).toBeVisible()
  const summaries = page.getByRole('region', { name: 'Последние обзоры' }).locator('summary').filter({ hasText: ' · ' })
  await expect(page.getByRole('list', { name: 'Сигналы по пакету' })).toBeVisible()
  await expect(summaries).toHaveText([/^solana · Продаём.*Короткая причина для solana\./, /^ethereum · Докупаем/, /^bitcoin · Удерживаем/])
  await expect(page.getByText('Сильный контраргумент.', { exact: false }).first()).toBeHidden()
  await summaries.first().click()
  const open = summaries.first().locator('..')
  await open.getByText('Аргументы и источники', { exact: true }).click()
  await expect(open.getByText('Сильный контраргумент.', { exact: false })).toBeVisible()
  await expect(open.getByText('Дата данных:', { exact: false })).toBeVisible()
})

test('a portfolio batch reports partially verified results separately and does not show a verdict for them', async ({ page }) => {
  const batch_id = 'fixture-partial'
  const baseline = { batch_id, created_at: '2026-11-23T13:18:00Z', model: 'fixture-web', error: null, sources: null }
  const querySource = `${source}?type=spot`
  await page.route(`**${api}/**`, route => {
    if (new URL(route.request().url()).pathname.endsWith('/session'))
      return route.fulfill({ json: { status: 'not_connected', email: null, csrf_token: 'fixture-csrf' } })
    return route.fulfill({ json: [
      { ...baseline, id: 'fixture-ok', coin_id: 'solana', status: 'succeeded', post: null },
      { ...baseline, id: 'fixture-part', coin_id: 'bitcoin', status: 'partial', unverified_blocks: ['economics'],
        sources: [{ url: querySource, title: 'Данные спота' }],
        post: { headline: 'Частичный обзор Bitcoin', summary: 'Остался блок со ссылкой.', opinion: 'Нет итоговой оценки.', watch: 'Проверить остальное.',
          sections: [{ block: 'markets', status: 'reported', text: 'Видны торги.', source_urls: [querySource] }],
          verdict: { classification: 'insufficient', confidence: 'low', thesis: 'Скрытый вердикт.',
            counterpoint: 'Не отображается.', invalidation: 'Не отображается.', basis_blocks: [] } } },
    ] })
  })
  await page.goto(pagePath)
  await expect(page.getByText('Готово: 1 из 2 · осталось: 0 · частично: 1')).toBeVisible()
  await page.getByText('bitcoin · Частичный обзор', { exact: true }).click()
  await expect(page.getByText('Скрытые блоки: Экономика.', { exact: false })).toBeVisible()
  await page.locator('details[open]').getByText('Аргументы и источники', { exact: true }).click()
  await expect(page.getByRole('link', { name: 'Данные спота' })).toHaveAttribute('href', querySource)
  await expect(page.getByText('Скрытый вердикт.', { exact: true })).toHaveCount(0)
})

test('a refuted recommendation exposes the countercheck but keeps the published signal neutral', async ({ page }) => {
  await page.route(`**${api}/**`, route => {
    if (new URL(route.request().url()).pathname.endsWith('/session'))
      return route.fulfill({ json: { status: 'not_connected', email: null, csrf_token: 'fixture-csrf' } })
    return route.fulfill({ json: [{
      id: 'refuted', coin_id: 'solana', model: 'fixture-web', status: 'succeeded', created_at: '2026-11-23T12:00:00Z', error: null,
      decision: { action: 'no_signal', reason: 'Повторный поиск не подтвердил основания.' },
      review: { candidate: 'sell', status: 'not_supported', sources: [{ url: source, title: 'Статус площадки' }],
        checks: [{ topic: 'markets', result: 'contradicted', finding: 'Биржа возобновила спот.', data_as_of: '2026-11-22', source_url: source }] },
      post: { headline: 'Обзор Solana', summary: 'Первый отчёт.', opinion: 'Мнение.', watch: 'Что отслеживать.', sections: [],
        verdict: { classification: 'exit_review', confidence: 'medium', thesis: 'Торги остановлены.',
          counterpoint: 'Их могут возобновить.', invalidation: 'Биржа откроет спот.', basis_blocks: ['markets'] } },
    }] })
  })
  await page.goto(pagePath)
  await expect(page.getByText('Нет сигнала', { exact: true })).toBeVisible()
  await expect(page.getByText('Торги остановлены.', { exact: true })).toBeHidden()
  await page.getByText('Аргументы и источники', { exact: true }).click()
  await expect(page.getByText('Первичный вывод, до повторного поиска.')).toBeVisible()
  await expect(page.getByText('Биржа возобновила спот.')).toBeVisible()
  await expect(page.getByRole('link', { name: 'Статус площадки' })).toHaveAttribute('href', source)
})

test('journal survives a restart, measures outcomes after fees, and does not backfill a missed window', async ({ page }) => {
  const day = 86_400_000
  const start = Date.parse('2026-11-23T12:00:00Z')
  let now = start
  let quoteAt = start
  let price = '0.000000000000000001'
  let jobsVisible = true
  const requests: string[] = []
  await page.clock.setFixedTime(new Date(now))
  await page.route(`**${api}/**`, route => {
    if (new URL(route.request().url()).pathname.endsWith('/session'))
      return route.fulfill({ json: { status: 'not_connected', email: null, csrf_token: 'fixture-csrf' } })
    return route.fulfill({ json: jobsVisible ? [
      { id: 'journal-add', coin_id: 'solana', model: 'fixture-web', status: 'succeeded', created_at: new Date(start).toISOString(),
        completed_at: new Date(start).toISOString(), decision: { action: 'add', reason: 'Тезис.' }, post: null, sources: null, error: null },
      { id: 'journal-hold', coin_id: 'bitcoin', model: 'fixture-web', status: 'succeeded', created_at: new Date(start).toISOString(),
        completed_at: new Date(start).toISOString(), decision: { action: 'hold', reason: 'Тезис.' }, post: null, sources: null, error: null },
    ] : [] })
  })
  await page.route('**/api/v1/public/market/quotes?*', route => {
    const ids = new URL(route.request().url()).searchParams.get('ids')
    expect(route.request().postData()).toBeNull()
    expect(ids).toBe(now === start ? 'bitcoin,solana' : 'solana')
    requests.push(ids!)
    return route.fulfill({ json: [
      { id: 'solana', usd: price, as_of: new Date(quoteAt).toISOString() },
      { id: 'bitcoin', usd: price, as_of: new Date(quoteAt - 16 * 60_000).toISOString() },
    ] })
  })
  await page.goto(pagePath)
  const journal = page.locator('details').filter({ has: page.locator('summary', { hasText: 'Журнал сигналов за 30/90 дней' }) })
  await journal.locator('summary').click()
  const sol = journal.locator('div.rounded-md').filter({ hasText: 'solana · Докупаем' })
  const btc = journal.locator('div.rounded-md').filter({ hasText: 'bitcoin · Удерживаем' })
  await expect(sol.getByText('Старт: 0.000000000000000001 USD', { exact: false })).toBeVisible()
  await expect(btc.getByText('ожидается стартовая котировка', { exact: false })).toHaveCount(2)
  jobsVisible = false
  await page.reload()
  await journal.locator('summary').click()
  await expect(sol.getByText('Старт: 0.000000000000000001 USD', { exact: false })).toBeVisible()
  expect(requests).toHaveLength(1)

  async function capture(days: number, nextPrice: string) {
    now = quoteAt = start + days * day
    price = nextPrice
    await page.clock.setFixedTime(new Date(now))
    await journal.getByRole('button', { name: 'Записать свежие котировки' }).click()
    await expect(journal.getByRole('button', { name: 'Записать свежие котировки' })).toBeVisible()
  }

  await capture(12, '0.0000000000000000005')
  await capture(30, '0.000000000000000002')
  await expect(sol.getByText('99,9 % по сигналу / 50 % при удержании', { exact: false })).toBeVisible()
  await expect(sol.getByText('Разница: 49,9 п.п.', { exact: false })).toBeVisible()
  await expect(sol.getByText('Наблюдаемая просадка: 50,02 % по сигналу / 25 % при удержании', { exact: false })).toBeVisible()
  await capture(93, '0.000000000000000003')
  await expect(sol.getByText('нет снимка в окне контрольной даты', { exact: false })).toBeVisible()
  await expect(btc.getByText('нет стартовой котировки', { exact: false })).toHaveCount(2)
})
