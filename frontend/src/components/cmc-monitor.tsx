import { RefreshCwIcon } from 'lucide-react'
import { useEffect, useState, type FormEvent, type MouseEvent } from 'react'
import { useLoad } from '@/lib/api'
import { Loaded, Warning } from './shared'
import { Badge, Button, Card, CardContent, CardDescription, CardHeader, CardTitle, Input,
  Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from './ui/primitives'

export const cmcMonitorPath = '/monitoring/coinmarketcap'
export const cmcRequestsPath = `${cmcMonitorPath}/requests`
export const marketRequestsPath = '/monitoring/requests'
const api = '/internal/market/coinmarketcap'

type MarketRequest = {
  id: string; slug: string | null; kind: string; outcome: string; http_status: number | null;
  started_at: string; finished_at: string | null; provider: string; response: Record<string, unknown> | null
}

type Monitor = {
  as_of: string
  global_next_attempt_at: string | null
  coins: { slug: string; has_platform: boolean; fetched_at: string | null; next_attempt_at: string | null }[]
  requests: MarketRequest[]
}

const outcomes: Record<string, string> = {
  running: 'Выполняется', succeeded: 'Успешно', failed: 'Ошибка', rate_limited: 'Лимит источника',
  cancelled: 'Отменён', interrupted: 'Прерван',
}
const kinds: Record<string, string> = { platform: 'Поиск ID', chart: 'График', catalog: 'Каталог монет', quotes: 'Котировки' }
const errors: Record<string, string> = {
  http_error: 'Источник вернул ошибку HTTP', transport_error: 'Не удалось соединиться с источником',
  timeout: 'Истекло время ожидания', invalid_response: 'Неожиданный формат ответа', invalid_json: 'Некорректный JSON',
  schema_error: 'Неожиданный формат ответа', too_large: 'Ответ превышает допустимый размер',
  provider_error: 'Источник сообщил об ошибке в ответе', provider_unavailable: 'Источник недоступен',
  rate_limited: 'Источник ограничил запросы', cancelled: 'Запрос отменён',
}

function ResponseSummary({ entry }: { entry: MarketRequest }) {
  const r = entry.response
  if (!r) return <span className="text-muted-foreground">—</span>
  const summary = typeof r.error === 'string' ? errors[r.error] ?? 'Ошибка источника' :
    typeof r.points === 'number' ? `Точек: ${r.points}` :
    typeof r.cryptocurrency_id === 'number' ? `ID монеты: ${r.cryptocurrency_id}` :
    typeof r.coins === 'number' ? `Монет: ${r.coins}` :
    typeof r.available === 'number' && typeof r.requested === 'number' ? `Котировок: ${r.available} из ${r.requested}` : 'Данные получены'
  return <details className="min-w-36 whitespace-normal">
    <summary className="cursor-pointer">{summary}</summary>
    <pre className="mt-2 max-w-64 overflow-x-auto whitespace-pre-wrap break-all rounded-md bg-muted p-2 text-xs">{JSON.stringify(r, null, 2)}</pre>
  </details>
}

export function CmcMonitorPage({ journal, timezone, follow }: {
  journal: boolean; timezone: string; follow: (event: MouseEvent<HTMLAnchorElement>, path: string) => void
}) {
  const [revision, setRevision] = useState(0)
  const state = useLoad<Monitor>('/internal/market/requests', revision)
  const [provider, setProvider] = useState('')
  const [slug, setSlug] = useState('')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  const [error, setError] = useState('')
  const formatter = new Intl.DateTimeFormat('ru-RU', { dateStyle: 'medium', timeStyle: 'medium', timeZone: timezone })
  const time = (value: string | null) => value ? formatter.format(new Date(value)) : '—'
  const refresh = () => setRevision(value => value + 1)

  useEffect(() => {
    const timer = window.setInterval(() => setRevision(value => value + 1), 10_000)
    return () => window.clearInterval(timer)
  }, [])

  async function loadChart(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setMessage('')
    setError('')
    try {
      const response = await fetch(`/api/v1${api}/charts/${encodeURIComponent(slug.trim())}`, { credentials: 'same-origin' })
      const next = response.headers.get('X-Chart-Next-Attempt-At')
      const retry = next ? ` Следующая возможная попытка: ${time(next)}.` : ''
      if (!response.ok) {
        const fault = await response.json().catch(() => null) as { message?: string } | null
        throw new Error(`HTTP ${response.status}: ${fault?.message ?? 'Ошибка API.'}${retry}`)
      }
      await response.body?.cancel()
      const cache = response.headers.get('X-Chart-Cache')
      setMessage(`HTTP ${response.status}: ` + (response.status === 202 ? 'ID монеты сохранён. Повторите запрос после паузы.' :
        cache === 'stale' ? 'Доступна предыдущая версия истории.' :
        cache === 'hit' ? 'История взята из кэша.' : 'История загружена.') + retry)
    } catch (failure) {
      setError((failure as Error).message)
    } finally {
      setBusy(false)
      refresh()
    }
  }

  return <div className="grid min-w-0 gap-5">
    <div className="flex flex-wrap items-start justify-between gap-3">
      <div><h1 className="text-3xl font-semibold tracking-tight">{journal ? 'Журнал рыночных запросов' : 'Кэш истории CoinMarketCap'}</h1>
        <p className="mt-1 text-sm text-muted-foreground">Коды и краткие ответы CoinGecko и CoinMarketCap. Просмотр журнала не обращается к источникам.</p></div>
      <Button variant="outline" onClick={refresh}><RefreshCwIcon aria-hidden />Обновить мониторинг</Button>
    </div>
    <nav className="flex flex-wrap gap-2" aria-label="Мониторинг CoinMarketCap">
      <Button asChild variant={journal ? 'default' : 'outline'}><a href={marketRequestsPath} onClick={event => follow(event, marketRequestsPath)}
        aria-current={journal ? 'page' : undefined}>Журнал запросов</a></Button>
      <Button asChild variant={journal ? 'outline' : 'default'}><a href={cmcMonitorPath} onClick={event => follow(event, cmcMonitorPath)}
        aria-current={journal ? undefined : 'page'}>Кэш монет</a></Button>
    </nav>
    {!journal && <Card><CardHeader><CardTitle>Запросить историю</CardTitle>
      <CardDescription>Недельные данные в USD. Для новой монеты сначала определяется ID, затем после общей паузы можно запросить график.</CardDescription></CardHeader>
      <CardContent><form className="flex flex-wrap items-end gap-3" onSubmit={event => void loadChart(event)}>
        <label className="grid gap-1.5 text-sm font-medium">Slug монеты
          <Input placeholder="dash" value={slug} required maxLength={120} pattern="[a-z0-9]+(?:\x2d+[a-z0-9]+)*" onChange={event => setSlug(event.target.value)} />
        </label>
        <Button type="submit" disabled={busy}>{busy ? 'Запрос выполняется…' : 'Запросить историю'}</Button>
      </form>{message && <p className="mt-3 text-sm" role="status">{message}</p>}
        {error && <div className="mt-3"><Warning title="Не удалось обновить историю">{error}</Warning></div>}
      </CardContent></Card>}
    <Loaded state={state}>{data => <>
      <Card><CardHeader><CardTitle>Лимиты CoinMarketCap</CardTitle>
        <CardDescription>30 секунд между внешними GET · один график на монету в час.</CardDescription></CardHeader>
        <CardContent className="grid gap-1 text-sm">
          <p>Общая пауза: {data.global_next_attempt_at ? `до ${time(data.global_next_attempt_at)}` : 'нет'}</p>
          <p className="text-muted-foreground">Состояние на {time(data.as_of)} · время: {timezone}</p>
        </CardContent></Card>
      {journal ? <Card className="min-w-0"><CardHeader><CardTitle>Последние 100 внешних запросов</CardTitle>
        <CardDescription>Попадания в кэш и локальный лимит не создают внешний запрос. «—» означает, что HTTP-ответ не получен или код не был сохранён.</CardDescription>
      </CardHeader>
        <CardContent className="grid min-w-0 gap-3">
          <label className="flex flex-wrap items-center gap-2 text-sm">Источник
            <select value={provider} onChange={event => setProvider(event.target.value)} className="h-8 rounded-md border bg-background px-2">
              <option value="">Все источники</option><option>CoinGecko</option><option>CoinMarketCap</option>
            </select>
          </label>
          {!data.requests.some(row => !provider || row.provider === provider) ?
            <p className="text-sm text-muted-foreground">Нет внешних запросов. Откройте свой портфель для котировок или запросите историю в «Кэш монет».</p> :
          <Table aria-label="Внешние запросы"><TableHeader><TableRow>
            <TableHead>Источник</TableHead><TableHead>Запрос</TableHead><TableHead>Результат</TableHead>
            <TableHead>HTTP источника</TableHead><TableHead>Ответ</TableHead><TableHead>Начало</TableHead><TableHead>Окончание</TableHead>
          </TableRow></TableHeader><TableBody>{data.requests.filter(row => !provider || row.provider === provider).map(row => <TableRow key={row.id}>
            <TableCell>{row.provider}</TableCell><TableCell>{kinds[row.kind] ?? row.kind}
              {row.slug && <div className="max-w-40 whitespace-normal break-words text-xs text-muted-foreground">{row.slug}</div>}</TableCell>
            <TableCell><Badge variant="outline">{outcomes[row.outcome] ?? row.outcome}</Badge></TableCell>
            <TableCell>{row.http_status ?? '—'}</TableCell><TableCell><ResponseSummary entry={row} /></TableCell>
            <TableCell>{time(row.started_at)}</TableCell><TableCell>{time(row.finished_at)}</TableCell>
          </TableRow>)}</TableBody></Table>}
        </CardContent></Card> : <Card className="min-w-0"><CardHeader><CardTitle>Состояние монет</CardTitle></CardHeader>
        <CardContent>{data.coins.length === 0 ? <p className="text-sm text-muted-foreground">В кэше пока нет монет.</p> :
          <Table aria-label="Состояние кэша"><TableHeader><TableRow><TableHead>Монета</TableHead><TableHead>История</TableHead>
             <TableHead>Загружена</TableHead><TableHead>Пауза по монете до</TableHead><TableHead>Ответ</TableHead></TableRow></TableHeader>
            <TableBody>{data.coins.map(row => <TableRow key={row.slug}>
              <TableCell>{row.slug}</TableCell><TableCell><Badge variant="outline">{row.fetched_at ?
                Date.parse(data.as_of) - Date.parse(row.fetched_at) >= 3_600_000 ? 'Устарела' : 'В кэше' :
                row.has_platform ? 'ID найден' : 'Нет данных'}</Badge></TableCell>
              <TableCell>{time(row.fetched_at)}</TableCell><TableCell>{time(row.next_attempt_at)}</TableCell>
              <TableCell>{row.fetched_at ? <a className="underline underline-offset-2" target="_blank" rel="noreferrer"
                href={`/api/v1${api}/cached/${encodeURIComponent(row.slug)}`}>Открыть JSON</a> : '—'}</TableCell>
            </TableRow>)}</TableBody></Table>}
        </CardContent></Card>}
    </>}</Loaded>
  </div>
}
