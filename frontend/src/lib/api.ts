import { useEffect, useState } from 'react'

export type Quality = 'ok' | 'partial' | 'stale' | 'missing' | 'not_applicable' | 'conflict' | 'error'
export type Session = { authenticated: boolean; demo: boolean; demo_available: boolean; authentication: string; portfolio_access: string; csrf_token: string | null }
export type Integration = { id: string; name: string; status: string; details: string; url: string; last_success_at: string | null }
export type IntegrationSet = { sources: Integration[] }
export type Job = { job_id: string; status: string; created_at: string; block_reason: string | null }
export type Position = { asset_id: string; name: string; symbol: string; quantity: string; value_usd: string | null; share_pct: string | null; quality_status: Quality; spot: boolean }
export type ProjectEvent = { id: string; asset_id: string; title: string; announced_at: string; local_date: string | null; event_at: string | null; deadline_at: string | null; status: string; version: number; requires_user_action: boolean }
export type Portfolio = { currency: string; total_usd: string | null; quality_status: Quality; source_portfolio_id: string | null; last_sync_at: string | null; sync_status: string; positions: Position[]; latest_job: Job | null; upcoming_events: ProjectEvent[] }
export type Evidence = { id: string; url: string; title: string; checked_at: string }
export type Block = 'economy' | 'supply' | 'markets' | 'development'
export type FeedItem = { id: string; asset_id: string; type: string; published_at: string; data_as_of: string; personalization: string; read: boolean; demo: boolean; version: number; supersedes_id: string | null; evidence: Evidence[]; analysis: {
  title: string; summary: string; importance: string; portfolio_impact: string; next_review: string; unknowns: string[];
  sections: { block: Block; data_status: Quality; assessment: string; rationale: string; evidence_ids: string[] }[];
  facts: { id: string; text: string; certainty: string; evidence_ids: string[] }[];
  actions: { type: string; rationale: string; condition: string }[];
} }
export type FeedPage = { items: FeedItem[]; next_cursor: string | null }
export type AssetDetail = { asset: { id: string; name: string; symbol: string; identifiers: { provider: string; external_id: string; chain: string | null; contract: string | null }[] }; position: Position | null; links: { entity_id: string; status: string }[]; data_as_of: string | null; last_conclusion: string | null; latest: FeedItem[] }
export type FlowSeries = { metric_key: string; scope: string; unit: string; months: { month: string; total: string | null; observed_intervals: number; expected_intervals: number; quality_status: Quality; mtd: boolean }[] }
export type Supply = { maximum_supply_kind: string; maximum_supply: string | null; total_supply: string | null; circulating_supply: string | null; data_as_of: string | null;
  windows: { days: number; quality_status: Quality; unlocks_pct: string | null; new_issuance_pct: string | null; gross_supply_pressure_pct: string | null }[];
  schedule: { id: string; kind: string; starts_at: string; ends_at: string | null; quantity_total: string; recipient: string; projected: boolean }[] }
export type Markets = { venues: number; observed_volume: string | null; top_venue_share: string | null; major_exchange_status: string; quality_status: Quality;
  active_pairs: { id: string; exchange_id: string; pair: string; venue_type: string; reported_volume_usd: string | null; last_trade_at: string; depth_usd: string | null }[] }
export type Source = { id: string; name: string; url: string; methodology: string }
export type Settings = { timezone: string; daily_time: string; major_exchange_ids: string[]; schedule_enabled: boolean; investment_profile: string | null }

let csrfToken: string | null = null

export async function request<T>(path: string, options: RequestInit = {}): Promise<T> {
  const headers = new Headers(options.headers)
  if (options.body !== undefined) headers.set('Content-Type', 'application/json')
  if (options.method && csrfToken) headers.set('X-CSRF-Token', csrfToken)
  const result = await fetch(`/api/v1${path}`, { ...options, headers, credentials: 'same-origin' })
  if (!result.ok) {
    const error = (await result.json().catch(() => null)) as { message?: string } | null
    throw new Error(error?.message ?? `Ошибка API (${result.status})`)
  }
  return result.status === 204 ? undefined as T : await result.json() as T
}

export async function session() {
  const result = await request<Session>('/auth/session')
  csrfToken = result.csrf_token
  return result
}

export function useLoad<T>(path: string, revision = 0) {
  const [state, setState] = useState<{ path: string; data?: T; error?: string }>({ path })
  useEffect(() => {
    const controller = new AbortController()
    request<T>(path, { signal: controller.signal }).then(
      data => setState({ path, data }),
      (error: Error) => { if (!controller.signal.aborted) setState({ path, error: error.message }) },
    )
    return () => controller.abort()
  }, [path, revision])
  return state.path === path ? state : { path }
}

export const statusText: Record<string, string> = {
  ok: 'Актуально', partial: 'Неполное покрытие', stale: 'Устарело', missing: 'Нет данных', not_applicable: 'Неприменимо',
  conflict: 'Конфликт данных', error: 'Ошибка', demo_fixture: 'Учебный источник', BLOCKED_CMC_AUTH: 'Вход недоступен',
  BLOCKED_CMC_ACCESS: 'Чтение портфеля недоступно', authentication_required: 'Требуется ключ', not_connected: 'Не подключён',
  unsupported_by_plan: 'Требуется другой тариф', queued: 'В очереди', running: 'Выполняется', succeeded: 'Готово',
  retry_wait: 'Ожидается повтор', failed: 'Сбой', blocked: 'Заблокировано', cancelled: 'Отменено',
  scheduled: 'Запланировано', postponed: 'Перенесено', announced: 'Объявлено', completed: 'Завершено', disputed: 'Оспаривается',
  confirmed: 'Подтверждено', unmapped: 'Связь не найдена', ambiguous: 'Связь неоднозначна', reported: 'Сообщается',
  undetermined: 'Недостаточно данных', improving: 'Улучшается', stable: 'Стабильно', deteriorating: 'Ухудшается', mixed: 'Смешанная оценка',
  normal: 'Обычная', important: 'Важная', critical: 'Критическая', confirmed_spot: 'Подтверждённый спот',
  unconfirmed: 'Не подтверждено', not_configured: 'Критерий не настроен', daily_analysis: 'Ежедневный анализ', event_analysis: 'Анализ события',
}

export const blockNames: Record<Block, string> = { economy: 'Экономика', supply: 'Предложение', markets: 'Рынки', development: 'Проект и события' }

export function decimal(value: string | null | undefined, digits = 8, minimum = 0) {
  if (value == null) return '—'
  const [whole, fraction = ''] = value.replace(/^-/, '').split('.')
  const scale = BigInt(10) ** BigInt(digits)
  let coefficient = BigInt(whole + fraction.slice(0, digits).padEnd(digits, '0'))
  const guard = fraction[digits] ?? '0'
  if (guard > '5' || (guard === '5' && (/[1-9]/.test(fraction.slice(digits + 1)) || coefficient % 2n === 1n))) coefficient++
  const rest = (coefficient % scale).toString().padStart(digits, '0').replace(/0+$/, '').padEnd(minimum, '0')
  const sign = value.startsWith('-') && coefficient !== 0n ? '−' : ''
  if (coefficient === 0n && /[1-9]/.test(value)) return `${sign}< ${digits === 0 ? '1' : '0,' + '0'.repeat(digits - 1) + '1'}`
  return `${sign}${(coefficient / scale).toLocaleString('ru-RU')}${rest ? ',' + rest : ''}`
}

export function dollars(value: string | null | undefined) {
  return value == null ? '—' : `${decimal(value, 2, 2)} $`
}

export function percent(value: string | null | undefined) {
  return value == null ? '—' : `${decimal(value, 2)} %`
}

export function date(value: string | null | undefined, timezone = 'Africa/Nairobi') {
  if (!value) return '—'
  if (/^\d{4}-\d{2}-\d{2}$/.test(value)) return new Intl.DateTimeFormat('ru-RU', { dateStyle: 'medium', timeZone: 'UTC' }).format(new Date(`${value}T00:00:00Z`))
  return new Intl.DateTimeFormat('ru-RU', { dateStyle: 'medium', timeStyle: 'short', timeZone: timezone }).format(new Date(value))
}
