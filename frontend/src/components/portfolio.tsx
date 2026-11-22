import { ArrowRightIcon, CalendarDaysIcon, ChartNoAxesCombinedIcon, RefreshCwIcon, WalletIcon } from 'lucide-react'
import { useState } from 'react'
import { date, decimal, dollars, percent, useLoad, useRefreshWhileUpdating, type FeedPage, type Portfolio, type Position, type ProjectEvent } from '@/lib/api'
import { Badge, Button, Card, CardContent, CardDescription, CardHeader, CardTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from './ui/primitives'
import { Empty, Loaded, RunJob, Status } from './shared'

export function AssetIcon({ symbol }: { symbol: string }) {
  return <span aria-hidden className="flex size-8 shrink-0 items-center justify-center rounded-full bg-muted text-xs font-semibold">{symbol.slice(0, 3)}</span>
}

export function PositionTable({ positions, navigate }: { positions: Position[]; navigate: (path: string) => void }) {
  if (!positions.length) return <p className="px-5 text-sm text-muted-foreground">Позиции появятся после синхронизации.</p>
  return <Table>
    <TableHeader><TableRow>
      <TableHead className="pl-5">Актив</TableHead><TableHead className="text-right">Количество</TableHead><TableHead className="text-right">Стоимость</TableHead>
      <TableHead className="text-right">Доля</TableHead><TableHead className="pr-5">Данные</TableHead>
    </TableRow></TableHeader>
    <TableBody>{positions.map(position => <TableRow key={position.asset_id}>
      <TableCell className="pl-5"><a href={`/assets/${encodeURIComponent(position.asset_id)}`} onClick={event => {
        if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return
        event.preventDefault(); navigate(`/assets/${encodeURIComponent(position.asset_id)}`)
      }} className="flex items-center gap-2.5 font-medium hover:underline">
        <AssetIcon symbol={position.symbol} /><span>{position.name}<span className="block text-xs font-normal text-muted-foreground">{position.symbol}</span></span>
      </a></TableCell>
      <TableCell className="text-right tabular-nums" title={position.quantity}>{decimal(position.quantity)}</TableCell>
      <TableCell className="text-right tabular-nums" title={position.value_usd ?? undefined}>{dollars(position.value_usd)}</TableCell>
      <TableCell className="text-right tabular-nums">{percent(position.share_pct)}</TableCell>
      <TableCell className="pr-5"><Status value={position.quality_status} /></TableCell>
    </TableRow>)}</TableBody>
  </Table>
}

export function EventList({ events, timezone }: { events: ProjectEvent[]; timezone: string }) {
  if (!events.length) return <p className="text-sm text-muted-foreground">Сведения о событиях отсутствуют.</p>
  const current = new Map<string, number>()
  events.forEach(event => current.set(event.id, Math.max(current.get(event.id) ?? 0, event.version)))
  return <ul className="divide-y">{events.map(event => <li key={`${event.id}:${event.version}`} className="flex items-start justify-between gap-2 py-3 first:pt-0 last:pb-0">
    <div className="min-w-0"><div className="text-sm font-medium">{event.title}</div>
      <div className="mt-1 text-xs text-muted-foreground">{date(event.deadline_at ?? event.event_at ?? event.local_date, timezone)}{event.local_date ? ' · дата без времени' : ''}</div>
    </div>
    <div className="flex shrink-0 flex-col items-end gap-1"><Status value={event.status} />{event.version < (current.get(event.id) ?? 0) && <Badge variant="outline">Предыдущая версия</Badge>}</div>
  </li>)}</ul>
}

export function Dashboard({ timezone, navigate }: { timezone: string; navigate: (path: string) => void }) {
  const [revision, setRevision] = useState(0)
  const portfolio = useLoad<Portfolio>('/portfolio', revision)
  const feed = useLoad<FeedPage>('/feed', revision, 'v2')
  useRefreshWhileUpdating(feed.data, setRevision)
  const refresh = () => setRevision(value => value + 1)

  return <div className="grid gap-6">
    <div className="flex flex-wrap items-start justify-between gap-4">
      <div><h1 className="text-3xl font-semibold tracking-tight">Портфель</h1><p className="mt-1 text-sm text-muted-foreground">Состав, оценка позиций и последние публикации.</p></div>
      <RunJob path="/portfolio/sync" label="Синхронизировать" icon={<RefreshCwIcon />} onDone={refresh} />
    </div>
    <Loaded state={portfolio}>{data => <>
      <div className="grid gap-4 sm:grid-cols-3">
        <Card><CardHeader><CardDescription className="flex items-center justify-between">Оценка портфеля<WalletIcon className="size-4" /></CardDescription>
          <CardTitle className="mt-1 text-2xl tabular-nums" title={data.total_usd ?? undefined}>{dollars(data.total_usd)}</CardTitle>
        </CardHeader><CardContent><Status value={data.quality_status} /></CardContent></Card>
        <Card><CardHeader><CardDescription className="flex items-center justify-between">Активы<ChartNoAxesCombinedIcon className="size-4" /></CardDescription>
          <CardTitle className="mt-1 text-2xl tabular-nums">{data.positions.length}</CardTitle>
        </CardHeader><CardContent className="text-sm text-muted-foreground">{data.positions.filter(position => position.quality_status === 'ok').length} со свежей оценкой</CardContent></Card>
        <Card><CardHeader><CardDescription className="flex items-center justify-between">Последняя синхронизация<RefreshCwIcon className="size-4" /></CardDescription>
          <CardTitle className="mt-1 text-lg">{date(data.last_sync_at, timezone)}</CardTitle>
        </CardHeader><CardContent><Status value={data.latest_job?.status ?? data.sync_status} /></CardContent></Card>
      </div>
      <Card className="min-w-0">
        <CardHeader><CardTitle>Позиции</CardTitle><CardDescription>Стоимость рассчитана в USD по доступным котировкам.</CardDescription></CardHeader>
        <PositionTable positions={data.positions} navigate={navigate} />
      </Card>
      <div className="grid gap-4 lg:grid-cols-2">
        <Card><CardHeader className="flex items-start justify-between gap-2"><div className="grid gap-1.5"><CardTitle>Последние публикации</CardTitle><CardDescription>Анализ активов портфеля</CardDescription></div>
          <Button variant="ghost" size="sm" onClick={() => navigate('/feed')}>Лента<ArrowRightIcon /></Button></CardHeader>
        <CardContent><Loaded state={feed}>{page => page.items.length ? <ul className="divide-y">{page.items.slice(0, 3).map(item => <li className="py-3 first:pt-0 last:pb-0" key={item.id}>
          <button type="button" onClick={() => navigate(`/assets/${encodeURIComponent(item.asset_id)}`)} className="text-left text-sm font-medium underline-offset-4 hover:underline">{item.analysis.headline}</button>
          <p className="mt-1 text-xs text-muted-foreground">{date(item.published_at, timezone)}</p>
        </li>)}</ul> : <p className="text-sm text-muted-foreground">Публикации появятся после выполнения заданий.</p>}</Loaded></CardContent></Card>
        <Card><CardHeader className="flex items-start justify-between gap-2"><div className="grid gap-1.5"><CardTitle>Ближайшие события</CardTitle><CardDescription>Объявления и даты из источников</CardDescription></div>
          <Button variant="ghost" size="icon" aria-label="Календарь" onClick={() => navigate('/calendar')}><CalendarDaysIcon /></Button></CardHeader>
        <CardContent><EventList events={data.upcoming_events} timezone={timezone} /></CardContent></Card>
      </div>
    </>}</Loaded>
  </div>
}

export function Calendar({ timezone }: { timezone: string }) {
  const events = useLoad<ProjectEvent[]>('/events')
  return <div className="grid gap-6"><div><h1 className="text-3xl font-semibold tracking-tight">Календарь</h1><p className="mt-1 text-sm text-muted-foreground">Запланированные события и история переноса дат.</p></div>
    <Loaded state={events}>{items => items.length ? <Card><CardHeader><CardTitle>События проектов</CardTitle></CardHeader><CardContent><EventList events={items} timezone={timezone} /></CardContent></Card>
      : <Empty>Нет подтверждённых дат в текущих данных.</Empty>}</Loaded>
  </div>
}
