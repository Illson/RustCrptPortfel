import { RefreshCwIcon } from 'lucide-react'
import { useEffect, useState, type FormEvent } from 'react'
import { useLoad } from '@/lib/api'
import { Loaded, Warning } from './shared'
import { Badge, Button, Card, CardContent, CardDescription, CardHeader, CardTitle, Input,
  Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from './ui/primitives'

export const cmcMonitorPath = '/monitoring/coinmarketcap'
export const cmcRequestsPath = `${cmcMonitorPath}/requests`
const api = '/internal/market/coinmarketcap'

type Monitor = {
  as_of: string
  global_next_attempt_at: string | null
  coins: { slug: string; has_platform: boolean; fetched_at: string | null; next_attempt_at: string | null }[]
  requests: { id: string; slug: string; kind: string; outcome: string; http_status: number | null;
    started_at: string; finished_at: string | null }[]
}

const outcomes: Record<string, string> = {
  running: 'Выполняется', succeeded: 'Успешно', failed: 'Ошибка', rate_limited: 'Лимит источника',
  cancelled: 'Отменён', interrupted: 'Прерван',
}

export function CmcMonitorPage({ journal, timezone }: { journal: boolean; timezone: string }) {
  const [revision, setRevision] = useState(0)
  const state = useLoad<Monitor>(`${api}/monitor`, revision)
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
        throw new Error((fault?.message ?? `Ошибка API (${response.status}).`) + retry)
      }
      await response.body?.cancel()
      const cache = response.headers.get('X-Chart-Cache')
      setMessage((response.status === 202 ? 'ID монеты сохранён. Повторите запрос после паузы.' :
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
      <div><h1 className="text-3xl font-semibold tracking-tight">{journal ? 'Журнал запросов CoinMarketCap' : 'Кэш истории CoinMarketCap'}</h1>
        <p className="mt-1 text-sm text-muted-foreground">Просмотр мониторинга не обращается к CoinMarketCap.</p></div>
      <Button variant="outline" onClick={refresh}><RefreshCwIcon aria-hidden />Обновить мониторинг</Button>
    </div>
    <nav className="flex flex-wrap gap-2" aria-label="Мониторинг CoinMarketCap">
      <Button asChild variant={journal ? 'outline' : 'default'}><a href={cmcMonitorPath} aria-current={journal ? undefined : 'page'}>Кэш монет</a></Button>
      <Button asChild variant={journal ? 'default' : 'outline'}><a href={cmcRequestsPath} aria-current={journal ? 'page' : undefined}>Журнал запросов</a></Button>
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
      <Card><CardHeader><CardTitle>Внешние лимиты</CardTitle>
        <CardDescription>30 секунд между внешними GET · один график на монету в час.</CardDescription></CardHeader>
        <CardContent className="grid gap-1 text-sm">
          <p>Общая пауза: {data.global_next_attempt_at ? `до ${time(data.global_next_attempt_at)}` : 'нет'}</p>
          <p className="text-muted-foreground">Состояние на {time(data.as_of)} · время: {timezone}</p>
        </CardContent></Card>
      {journal ? <Card className="min-w-0"><CardHeader><CardTitle>Последние 100 попыток</CardTitle></CardHeader>
        <CardContent>{data.requests.length === 0 ? <p className="text-sm text-muted-foreground">Внешних запросов ещё не было.</p> :
          <Table aria-label="Внешние запросы"><TableHeader><TableRow>
            <TableHead>Монета</TableHead><TableHead>Запрос</TableHead><TableHead>Результат</TableHead>
            <TableHead>Код HTTP</TableHead><TableHead>Начало</TableHead><TableHead>Окончание</TableHead>
          </TableRow></TableHeader><TableBody>{data.requests.map(row => <TableRow key={row.id}>
            <TableCell>{row.slug}</TableCell><TableCell>{row.kind === 'platform' ? 'Поиск ID' : 'График'}</TableCell>
            <TableCell><Badge variant="outline">{outcomes[row.outcome] ?? row.outcome}</Badge></TableCell>
            <TableCell>{row.http_status ?? '—'}</TableCell><TableCell>{time(row.started_at)}</TableCell><TableCell>{time(row.finished_at)}</TableCell>
          </TableRow>)}</TableBody></Table>}
        </CardContent></Card> : <Card className="min-w-0"><CardHeader><CardTitle>Состояние монет</CardTitle></CardHeader>
        <CardContent>{data.coins.length === 0 ? <p className="text-sm text-muted-foreground">В кэше пока нет монет.</p> :
          <Table aria-label="Состояние кэша"><TableHeader><TableRow><TableHead>Монета</TableHead><TableHead>История</TableHead>
            <TableHead>Загружена</TableHead><TableHead>Пауза по монете до</TableHead></TableRow></TableHeader>
            <TableBody>{data.coins.map(row => <TableRow key={row.slug}>
              <TableCell>{row.slug}</TableCell><TableCell><Badge variant="outline">{row.fetched_at ?
                Date.parse(data.as_of) - Date.parse(row.fetched_at) >= 3_600_000 ? 'Устарела' : 'В кэше' :
                row.has_platform ? 'ID найден' : 'Нет данных'}</Badge></TableCell>
              <TableCell>{time(row.fetched_at)}</TableCell><TableCell>{time(row.next_attempt_at)}</TableCell>
            </TableRow>)}</TableBody></Table>}
        </CardContent></Card>}
    </>}</Loaded>
  </div>
}
