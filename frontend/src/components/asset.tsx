import { ArrowLeftIcon, PlayIcon } from 'lucide-react'
import { useState } from 'react'
import { blockNames, date, decimal, dollars, percent, useLoad, type AssetDetail, type FlowSeries, type Markets, type ProjectEvent, type Source, type Supply } from '@/lib/api'
import { Badge, Button, Card, CardContent, CardDescription, CardHeader, CardTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow, Tabs, TabsContent, TabsList, TabsTrigger } from './ui/primitives'
import { Empty, ExternalLink, Loaded, RunJob, Status } from './shared'
import { FeedCard } from './feed'
import { AssetIcon, EventList } from './portfolio'

const metricNames: Record<string, string> = { chain_revenue: 'Выручка сети', app_revenue: 'Выручка приложений', revenue: 'Выручка протокола', fees: 'Сборы', holders_revenue: 'Поток держателям' }
const scopeNames: Record<string, string> = { chain: 'Сеть', ecosystem_apps: 'Приложения экосистемы', protocol_global: 'Протокол', protocol_chain: 'Протокол в сети' }

function chartUnits(raw: string | null) {
  if (!raw) return 0n
  const [whole, fraction = ''] = raw.replace(/^-/, '').split('.')
  return BigInt(whole + fraction.slice(0, 8).padEnd(8, '0'))
}

function Economy({ id, revision }: { id: string; revision: number }) {
  const state = useLoad<FlowSeries[]>(`/assets/${id}/metrics?block=economy&granularity=month`, revision)
  return <Loaded state={state}>{series => series.length ? <div className="grid gap-4">{series.map((item, index) => {
    const range = item.months.filter(month => !month.mtd)
    const mtd = item.months.find(month => month.mtd)
    const max = range.reduce((peak, month) => chartUnits(month.total) > peak ? chartUnits(month.total) : peak, 1n)
    return <Card key={`${item.scope}-${item.metric_key}-${index}`}>
      <CardHeader><div className="flex flex-wrap items-center justify-between gap-2"><CardTitle>{metricNames[item.metric_key] ?? item.metric_key}</CardTitle><Badge variant="outline">{scopeNames[item.scope] ?? item.scope}</Badge></div>
        <CardDescription>12 завершённых месяцев · календарь UTC · {item.unit}</CardDescription></CardHeader>
      <CardContent>
        <div role="img" aria-label={`Месячные значения показателя «${metricNames[item.metric_key] ?? item.metric_key}». Точные значения приведены в таблице.`} className="grid h-36 grid-cols-12 items-end gap-1.5 border-b sm:gap-3">
          {range.map(month => <div key={month.month} className="flex h-full min-w-0 flex-col justify-end gap-1 text-center" title={`${month.month.slice(0, 7)} · ${dollars(month.total)}`}>
            <div className={month.quality_status === 'ok' ? 'rounded-t bg-primary' : 'rounded-t border border-dashed bg-muted'} style={{ height: `${(chartUnits(month.total) * 83n / max).toString()}%` }} />
            <span className="pb-1 text-[10px] text-muted-foreground">{month.month.slice(5, 7)}</span>
          </div>)}
        </div>
        {mtd && <div className="mt-4 flex flex-wrap items-center justify-between gap-2 text-sm"><span>Текущий месяц (MTD)</span><strong>{dollars(mtd.total)}</strong><Status value={mtd.quality_status} /></div>}
        <details className="mt-4 rounded-md border p-3"><summary className="cursor-pointer text-sm font-medium">Значения и покрытие</summary>
          <Table><TableHeader><TableRow><TableHead>Месяц</TableHead><TableHead>Сумма</TableHead><TableHead>Дни</TableHead><TableHead>Качество</TableHead></TableRow></TableHeader>
            <TableBody>{item.months.map(month => <TableRow key={month.month}><TableCell>{month.month.slice(0, 7)}{month.mtd ? ' · MTD' : ''}</TableCell>
              <TableCell title={month.total ?? undefined}>{dollars(month.total)}</TableCell><TableCell>{month.observed_intervals} / {month.expected_intervals}</TableCell>
              <TableCell><Status value={month.quality_status} /></TableCell></TableRow>)}</TableBody></Table>
        </details>
      </CardContent>
    </Card>
  })}</div> : <Empty>Для актива отсутствует сопоставимый ряд экономики.</Empty>}</Loaded>
}

function SupplyTab({ id, revision, timezone }: { id: string; revision: number; timezone: string }) {
  const state = useLoad<Supply>(`/assets/${id}/supply`, revision)
  const labels: Record<string, string> = { issuance: 'Новый выпуск', unlock: 'Разблокировка', burn: 'Сжигание', treasury: 'Из казначейства' }
  return <Loaded state={state}>{data => <div className="grid gap-4">
    <Card><CardHeader><CardTitle>Предложение токена</CardTitle><CardDescription>Данные на {date(data.data_as_of, timezone)}</CardDescription></CardHeader>
      <CardContent className="grid gap-3 sm:grid-cols-3">{[
        ['В обращении', data.circulating_supply], ['Всего выпущено', data.total_supply], ['Максимум', data.maximum_supply],
      ].map(([label, value]) => <div key={label}><p className="text-sm text-muted-foreground">{label}</p><p className="mt-1 font-semibold" title={value ?? undefined}>{value ? decimal(value) : label === 'Максимум' && data.maximum_supply_kind === 'unbounded' ? 'Не ограничен' : 'Неизвестно'}</p></div>)}</CardContent>
    </Card>
    <Card><CardHeader><CardTitle>Давление предложения</CardTitle><CardDescription>Доля от объёма в обращении на дату оценки</CardDescription></CardHeader>
      <CardContent className="p-0"><Table><TableHeader><TableRow><TableHead className="pl-5">Горизонт</TableHead><TableHead>Разблокировки</TableHead><TableHead>Новый выпуск</TableHead><TableHead>В обращение</TableHead><TableHead className="pr-5">Покрытие</TableHead></TableRow></TableHeader>
        <TableBody>{data.windows.map(item => <TableRow key={item.days}><TableCell className="pl-5">{item.days} дней</TableCell><TableCell>{percent(item.unlocks_pct)}</TableCell>
          <TableCell>{percent(item.new_issuance_pct)}</TableCell><TableCell>{percent(item.gross_supply_pressure_pct)}</TableCell><TableCell className="pr-5"><Status value={item.quality_status} /></TableCell></TableRow>)}</TableBody></Table></CardContent>
    </Card>
    <Card><CardHeader><CardTitle>Расписание выпуска</CardTitle></CardHeader><CardContent>
      {data.schedule.length ? <ul className="divide-y">{data.schedule.map(item => <li key={item.id} className="flex flex-wrap items-center justify-between gap-2 py-3 first:pt-0 last:pb-0">
        <div><p className="text-sm font-medium">{labels[item.kind] ?? item.kind} · {item.recipient}</p><p className="mt-1 text-xs text-muted-foreground">{date(item.starts_at, timezone)}{item.ends_at && ` — ${date(item.ends_at, timezone)}`}</p></div>
        <div className="flex items-center gap-2"><span className="text-sm" title={item.quantity_total}>{decimal(item.quantity_total)}</span>{item.projected && <Badge variant="outline">Прогноз</Badge>}</div>
      </li>)}</ul> : <p className="text-sm text-muted-foreground">Расписание отсутствует.</p>}
    </CardContent></Card>
  </div>}</Loaded>
}

function MarketTab({ id, revision, timezone }: { id: string; revision: number; timezone: string }) {
  const state = useLoad<Markets>(`/assets/${id}/markets`, revision)
  return <Loaded state={state}>{data => <Card><CardHeader><div className="flex flex-wrap items-center justify-between gap-2"><CardTitle>Активные спотовые пары</CardTitle><Status value={data.quality_status} /></div>
    <CardDescription>Площадки: {data.venues} · Наблюдаемый объём: {dollars(data.observed_volume)} · Доля крупнейшей: {percent(data.top_venue_share)}</CardDescription></CardHeader>
    <CardContent><div className="mb-4 flex items-center gap-2 text-sm text-muted-foreground">Крупные биржи<Status value={data.major_exchange_status} /></div>
      {data.active_pairs.length ? <Table><TableHeader><TableRow><TableHead>Пара</TableHead><TableHead>Площадка</TableHead><TableHead>Объём</TableHead><TableHead>Последняя сделка</TableHead><TableHead>Глубина</TableHead></TableRow></TableHeader>
        <TableBody>{data.active_pairs.map(item => <TableRow key={item.id}><TableCell>{item.pair}</TableCell><TableCell>{item.exchange_id} · {item.venue_type}</TableCell>
          <TableCell>{dollars(item.reported_volume_usd)}</TableCell><TableCell>{date(item.last_trade_at, timezone)}</TableCell><TableCell>{item.depth_usd ? dollars(item.depth_usd) : 'Не измерена'}</TableCell></TableRow>)}</TableBody></Table>
        : <p className="text-sm text-muted-foreground">Свежих спотовых наблюдений нет.</p>}
    </CardContent></Card>}</Loaded>
}

function EventsTab({ id, timezone, revision }: { id: string; timezone: string; revision: number }) {
  const state = useLoad<ProjectEvent[]>(`/assets/${id}/events`, revision)
  return <Loaded state={state}>{events => <Card><CardHeader><CardTitle>Объявления и даты</CardTitle></CardHeader><CardContent><EventList events={events} timezone={timezone} /></CardContent></Card>}</Loaded>
}

function SourcesTab({ detail }: { detail: AssetDetail }) {
  const state = useLoad<Source[]>(`/assets/${detail.asset.id}/sources`)
  return <div className="grid gap-4">
    <Card><CardHeader><CardTitle>Идентификаторы и связи</CardTitle></CardHeader><CardContent className="grid gap-3 text-sm">
      {detail.asset.identifiers.map((item, index) => <div className="grid grid-cols-2 gap-3" key={index}><span className="text-muted-foreground">{item.provider}</span><span>{item.external_id}{item.contract && ` · ${item.contract}`}</span></div>)}
      {detail.links.map(link => <div key={link.entity_id} className="flex items-center justify-between gap-2"><span>{link.entity_id}</span><Status value={link.status} /></div>)}
    </CardContent></Card>
    <Loaded state={state}>{sources => <Card><CardHeader><CardTitle>Источники наблюдений</CardTitle></CardHeader><CardContent className="grid gap-3">
      {sources.map(source => <div className="text-sm" key={source.id}><ExternalLink href={source.url}>{source.name}</ExternalLink><p className="mt-1 text-muted-foreground">{source.methodology}</p></div>)}
    </CardContent></Card>}</Loaded>
  </div>
}

export function AssetPage({ id, timezone, back }: { id: string; timezone: string; back: () => void }) {
  const [revision, setRevision] = useState(0)
  const result = useLoad<AssetDetail>(`/assets/${id}`, revision)
  const refresh = () => setRevision(value => value + 1)
  return <div className="grid gap-5"><Button className="justify-self-start" size="sm" variant="ghost" onClick={back}><ArrowLeftIcon />К портфелю</Button>
    <Loaded state={result}>{data => <>
      <div className="flex flex-wrap items-start justify-between gap-4"><div className="flex items-center gap-3"><AssetIcon symbol={data.asset.symbol} /><div>
        <h1 className="text-3xl font-semibold tracking-tight">{data.asset.name}</h1><p className="mt-1 text-sm text-muted-foreground">{data.asset.symbol} · CMC ID {data.asset.id}</p></div></div>
        <RunJob path={`/assets/${id}/analysis-runs`} label="Запустить анализ" icon={<PlayIcon />} onDone={refresh} />
      </div>
      <Card><CardHeader><div className="flex flex-wrap items-center justify-between gap-2"><CardTitle>Позиция и вывод</CardTitle>{data.position && <Status value={data.position.quality_status} />}</div>
        <CardDescription>Последние наблюдения {date(data.data_as_of, timezone)}</CardDescription></CardHeader>
        <CardContent className="grid gap-2 text-sm">{data.position && <div className="flex flex-wrap gap-x-6 gap-y-1"><span title={data.position.quantity}>{decimal(data.position.quantity)} {data.asset.symbol}</span><span>{dollars(data.position.value_usd)}</span><span>{percent(data.position.share_pct)} портфеля</span></div>}
          <p className="text-muted-foreground">{data.last_conclusion ?? 'Аналитики для этого актива пока нет.'}</p></CardContent></Card>
      <Tabs defaultValue="feed" key={id} className="min-w-0"><div className="overflow-x-auto"><TabsList aria-label="Разделы актива">
        <TabsTrigger value="feed">Лента</TabsTrigger>{Object.entries(blockNames).map(([key, title]) => <TabsTrigger key={key} value={key}>{title}</TabsTrigger>)}<TabsTrigger value="sources">Источники</TabsTrigger>
      </TabsList></div>
        <TabsContent value="feed" className="grid gap-4">{data.latest.length ? data.latest.map(item => <FeedCard key={item.id} item={item} timezone={timezone} refresh={refresh} />) : <Empty>Публикаций пока нет.</Empty>}</TabsContent>
        <TabsContent value="economy"><Economy id={id} revision={revision} /></TabsContent>
        <TabsContent value="supply"><SupplyTab id={id} timezone={timezone} revision={revision} /></TabsContent>
        <TabsContent value="markets"><MarketTab id={id} timezone={timezone} revision={revision} /></TabsContent>
        <TabsContent value="development"><EventsTab id={id} timezone={timezone} revision={revision} /></TabsContent>
        <TabsContent value="sources"><SourcesTab detail={data} /></TabsContent>
      </Tabs>
    </>}</Loaded>
  </div>
}
