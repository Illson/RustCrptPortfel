import { FilterIcon } from 'lucide-react'
import { type FormEvent, useState } from 'react'
import { blockNames, date, decimal, request, useLoad, useRefreshWhileUpdating, type Evidence, type FeedItem, type FeedPage, type Portfolio } from '@/lib/api'
import { Badge, Button, Card, CardContent, CardDescription, CardHeader, CardTitle, Dialog, DialogContent, DialogDescription, DialogTitle, DialogTrigger, Input } from './ui/primitives'
import { Empty, ExternalLink, Loaded, Picker, Status, Warning } from './shared'

function References({ ids, evidence }: { ids: string[]; evidence: Evidence[] }) {
  return <div className="mt-1 flex flex-wrap gap-2 text-xs text-muted-foreground">{ids.map(id => {
    const source = evidence.find(item => item.id === id)
    return source ? <ExternalLink key={id} href={source.url}>{source.title}</ExternalLink> : null
  })}</div>
}

export function FeedCard({ item, timezone, refresh }: { item: FeedItem; timezone: string; refresh: () => void }) {
  const [error, setError] = useState('')
  const [readPending, setReadPending] = useState(false)
  async function toggleRead() {
    setReadPending(true)
    setError('')
    try { await request(`/feed/${item.id}/read`, { method: 'PATCH', body: JSON.stringify({ read: !item.read }) }); refresh() }
    catch (failure) { setError((failure as Error).message) }
    finally { setReadPending(false) }
  }

  return <Card>
    <CardHeader><div className="mb-2 flex flex-wrap items-center gap-2">{item.asset_symbol && <Badge variant="outline">{item.asset_symbol}</Badge>}<Status value={item.type} /><Status value={item.analysis.severity} />
      {item.demo ? <Badge variant="outline">Учебная карточка</Badge> : <Badge variant="outline">Создано AI</Badge>}
      {!item.demo && item.personalization !== 'current' && <Badge variant="outline">{item.personalization === 'historical' ? 'Историческая оценка' : 'Без персонализации'}</Badge>}
      {!item.read && <Badge variant="outline">Новая</Badge>}{item.version > 1 && <Badge variant="outline">Версия {item.version}</Badge>}
    </div><CardTitle className="text-base">{item.analysis.headline}</CardTitle><CardDescription>Опубликовано {date(item.published_at, timezone)} · данные на {date(item.data_as_of, timezone)}</CardDescription></CardHeader>
    <CardContent className="grid gap-4"><p className="text-sm">{item.analysis.summary}</p><div className="flex flex-wrap gap-2">
      <Dialog><DialogTrigger asChild><Button size="sm" variant="outline">Открыть анализ</Button></DialogTrigger><DialogContent className="max-h-[85dvh] overflow-y-auto sm:max-w-lg">
        <DialogTitle className="pr-8 text-lg font-semibold">{item.analysis.headline}</DialogTitle><DialogDescription className="text-sm text-muted-foreground">{item.analysis.summary}</DialogDescription>
        <div className="grid gap-3">{item.analysis.sections.map(section => <div className="rounded-md border p-3" key={section.block}>
          <div className="flex flex-wrap items-center gap-1"><h3 className="mr-auto text-sm font-semibold">{blockNames[section.block]}</h3><Status value={section.data_status} /><Status value={section.assessment} /></div>
          <p className="mt-2 text-sm text-muted-foreground">{section.rationale}</p><References ids={section.evidence_ids} evidence={item.evidence} />
        </div>)}</div>
        <div className="grid gap-3 text-sm"><div><h3 className="font-semibold">Влияние на позицию</h3><p>{item.personalization === 'historical' ? 'Оценка не относится к актуальному составу.' : item.analysis.portfolio_impact}</p></div>
          {!!item.analysis.facts.length && <div><h3 className="font-semibold">Факты и источники</h3>{item.analysis.facts.map(fact => <div key={fact.id} className="mt-2">
            <p>{fact.text} <Status value={fact.certainty} /></p>
            {!!fact.numeric_claims.length && <ul className="mt-1 list-inside list-disc">{fact.numeric_claims.map((claim, index) => <li key={index}>
              <span title={claim.value_decimal}>{decimal(claim.value_decimal)} {claim.unit}</span> · {claim.period} · {claim.scope}
              {item.evidence.find(evidence => evidence.id === claim.metric_ref)?.formula_version && ` · ${item.evidence.find(evidence => evidence.id === claim.metric_ref)?.formula_version}`}
            </li>)}</ul>}
            <References ids={fact.evidence_ids} evidence={item.evidence} />
          </div>)}</div>}
          {!!item.analysis.changes.length && <div><h3 className="font-semibold">Что изменилось</h3><p>{item.analysis.changes.join(' ')}</p></div>}
          {!!item.analysis.actions.length && <div><h3 className="font-semibold">Условные действия</h3>{item.analysis.actions.filter(action => item.personalization !== 'historical' || action.type !== 'review_allocation').map((action, index) => <div key={index}><p>{action.rationale} {action.condition}</p><References ids={action.evidence_ids} evidence={item.evidence} /></div>)}</div>}
          {!!item.analysis.unknowns.length && <div><h3 className="font-semibold">Что неизвестно</h3><p>{item.analysis.unknowns.join(' ')}</p></div>}
          <div><h3 className="font-semibold">Следующая проверка</h3><p>{item.analysis.next_review}</p></div>
        </div>
      </DialogContent></Dialog>
      <Button size="sm" variant="ghost" disabled={readPending} onClick={() => void toggleRead()}>{item.read ? 'Отметить как новую' : 'Отметить прочитанной'}</Button>
      <ReportDialog item={item} />
    </div>{error && <Warning title="Не удалось сохранить">{error}</Warning>}</CardContent>
  </Card>
}

function ReportDialog({ item }: { item: FeedItem }) {
  const [reason, setReason] = useState('')
  const [pending, setPending] = useState(false)
  const [saved, setSaved] = useState(false)
  const [error, setError] = useState('')

  async function send(event: FormEvent) {
    event.preventDefault()
    setPending(true)
    setError('')
    try {
      await request(`/feed/${item.id}/reports`, { method: 'POST', body: JSON.stringify({ reason }) })
      setSaved(true)
    } catch (failure) { setError((failure as Error).message) }
    finally { setPending(false) }
  }

  return <Dialog><DialogTrigger asChild><Button size="sm" variant="ghost">Сообщить об ошибке</Button></DialogTrigger>
    <DialogContent>
      <DialogTitle className="pr-8">Замечание к карточке</DialogTitle>
      <DialogDescription>Замечание сохранится в приложении. Не указывайте пароли или другие секреты.</DialogDescription>
      {saved ? <p role="status">Замечание сохранено.</p> : <form className="grid gap-3" onSubmit={event => void send(event)}>
        <label className="grid gap-1.5">Что нужно исправить?
          <textarea required maxLength={800} className="min-h-24 rounded-md border border-input px-2.5 py-2"
            value={reason} onChange={event => setReason(event.target.value)} />
        </label>
        {error && <Warning title="Не удалось сохранить">{error}</Warning>}
        <Button disabled={pending || !reason.trim()} type="submit">Сохранить замечание</Button>
      </form>}
    </DialogContent>
  </Dialog>
}

export function Feed({ timezone }: { timezone: string }) {
  const [asset, setAsset] = useState('any')
  const [read, setRead] = useState('any')
  const [type, setType] = useState('any')
  const [importance, setImportance] = useState('any')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [revision, setRevision] = useState(0)
  const [page, setPage] = useState<{ filter: string; cursor: string | null }>({ filter: '', cursor: null })
  const positions = useLoad<Portfolio>('/portfolio')
  const filter = new URLSearchParams(Object.entries({ asset, read, type, importance, from, to }).filter(([, value]) => value && value !== 'any')).toString()
  const cursor = page.filter === filter ? page.cursor : null
  const items = useLoad<FeedPage>(`/feed?${filter}${cursor ? `&cursor=${cursor}` : ''}`, revision, 'v2')
  useRefreshWhileUpdating(cursor ? undefined : items.data, setRevision)
  const refresh = () => setRevision(value => value + 1)
  return <div className="grid gap-6"><div><h1 className="text-3xl font-semibold tracking-tight">Лента</h1><p className="mt-1 text-sm text-muted-foreground">Публикации по активам портфеля.</p></div>
    <Card><CardHeader><CardTitle className="flex items-center gap-2"><FilterIcon className="size-4" />Фильтры</CardTitle></CardHeader><CardContent className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
      <Picker label="Актив" value={asset} setValue={setAsset} options={[["any", "Все активы"], ...(positions.data?.positions.map(position => [position.asset_id, position.symbol] as [string, string]) ?? [])]} />
      <Picker label="Категория" value={type} setValue={setType} options={[["any", "Все категории"], ["daily_analysis", "Ежедневный анализ"], ["event_analysis", "Анализ события"]]} />
      <Picker label="Важность" value={importance} setValue={setImportance} options={[["any", "Любая"], ["info", "Информация"], ["watch", "Наблюдение"], ["important", "Важная"], ["critical", "Критическая"]]} />
      <Picker label="Прочитанность" value={read} setValue={setRead} options={[["any", "Все"], ["false", "Новые"], ["true", "Прочитанные"]]} />
      <label className="grid gap-1.5 text-sm font-medium">Дата с<Input type="date" value={from} onChange={event => setFrom(event.target.value)} /></label>
      <label className="grid gap-1.5 text-sm font-medium">Дата по<Input type="date" value={to} onChange={event => setTo(event.target.value)} /></label>
    </CardContent></Card>
    <Loaded state={items}>{result => result.items.length ? <div className="grid gap-4">{result.items.map(item => <FeedCard key={item.id} item={item} timezone={timezone} refresh={refresh} />)}
      <div className="flex gap-2">{cursor && <Button variant="outline" onClick={() => setPage({ filter, cursor: null })}>К началу</Button>}{result.next_cursor && <Button variant="outline" onClick={() => setPage({ filter, cursor: result.next_cursor })}>Следующая страница</Button>}</div>
    </div> : <Empty>{result.updating ? 'Анализ выполняется. Новые карточки появятся здесь.' : 'Публикаций с такими параметрами нет.'}</Empty>}</Loaded>
  </div>
}
