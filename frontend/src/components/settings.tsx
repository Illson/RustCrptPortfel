import { BotIcon, CableIcon, InfoIcon } from 'lucide-react'
import { type FormEvent, useState } from 'react'
import { date, request, useLoad, type IntegrationSet, type Settings } from '@/lib/api'
import { Alert, AlertDescription, AlertTitle, Badge, Button, Card, CardContent, CardDescription, CardHeader, CardTitle, Input } from './ui/primitives'
import { ExternalLink, Loaded, Status, Warning } from './shared'

export function Sources({ timezone }: { timezone: string }) {
  const state = useLoad<IntegrationSet>('/integrations/status')
  return <div className="grid gap-6"><div><h1 className="text-3xl font-semibold tracking-tight">Источники</h1><p className="mt-1 text-sm text-muted-foreground">Доступ, покрытие и состояние подключений.</p></div>
    <Loaded state={state}>{data => <div className="grid gap-4 sm:grid-cols-2">{data.sources.map(integration => <Card key={integration.id}>
      <CardHeader><CardTitle className="flex items-center gap-2"><CableIcon className="size-4" />{integration.name}</CardTitle><CardDescription>{integration.details}</CardDescription></CardHeader>
      <CardContent className="grid justify-items-start gap-2 text-sm"><Status value={integration.status} /><p className="text-xs text-muted-foreground">Последний успех: {date(integration.last_success_at, timezone)}</p>
        <ExternalLink href={integration.url}>Документация</ExternalLink></CardContent>
    </Card>)}</div>}</Loaded>
  </div>
}

function SettingsForm({ settings, saved }: { settings: Settings; saved: (value: Settings) => void }) {
  const [timezone, setTimezone] = useState(settings.timezone)
  const [daily, setDaily] = useState(settings.daily_time)
  const [schedule, setSchedule] = useState(settings.schedule_enabled)
  const [exchanges, setExchanges] = useState(settings.major_exchange_ids.join(', '))
  const [pending, setPending] = useState(false)
  const [status, setStatus] = useState('')
  const [error, setError] = useState('')

  async function submit(event: FormEvent) {
    event.preventDefault()
    setError('')
    setStatus('')
    setPending(true)
    try {
      const result = await request<Settings>('/settings', { method: 'PATCH', body: JSON.stringify({ ...settings,
        timezone, daily_time: daily, schedule_enabled: schedule, major_exchange_ids: exchanges.split(',').map(value => value.trim()).filter(Boolean),
      }) })
      saved(result)
      setStatus('Настройки сохранены.')
    } catch (failure) { setError((failure as Error).message) }
    finally { setPending(false) }
  }

  return <Card><CardHeader><CardTitle>Расписание и рынки</CardTitle><CardDescription>В расписании используется местное время выбранного часового пояса.</CardDescription></CardHeader>
    <CardContent><form onSubmit={event => void submit(event)} className="grid gap-4">
      <div className="grid gap-3 sm:grid-cols-2"><label className="grid gap-1.5 text-sm font-medium">Часовой пояс IANA<Input required value={timezone} onChange={event => setTimezone(event.target.value)} /></label>
        <label className="grid gap-1.5 text-sm font-medium">Время ежедневного анализа<Input type="time" value={daily} required onChange={event => setDaily(event.target.value)} /></label></div>
      <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={schedule} onChange={event => setSchedule(event.target.checked)} />Включить расписание учебного анализа</label>
      <label className="grid gap-1.5 text-sm font-medium">Идентификаторы крупных бирж<Input placeholder="Через запятую" value={exchanges} onChange={event => setExchanges(event.target.value)} /></label>
      <div className="flex flex-wrap items-center gap-3"><Button disabled={pending} type="submit">Сохранить настройки</Button><span role="status" className="text-sm text-muted-foreground">{status}</span></div>
      {error && <Warning title="Не удалось сохранить настройки">{error}</Warning>}
    </form></CardContent>
  </Card>
}

export function SettingsPage({ saved }: { saved: (value: Settings) => void }) {
  const state = useLoad<Settings>('/settings')
  return <div className="grid gap-6"><div><h1 className="text-3xl font-semibold tracking-tight">Настройки</h1><p className="mt-1 text-sm text-muted-foreground">Расписание анализа и параметры портфеля.</p></div>
    <Alert><InfoIcon aria-hidden /><AlertTitle>Выбор портфеля CMC недоступен</AlertTitle><AlertDescription>Для выбора нужен подтверждённый доступ к личным портфелям.</AlertDescription></Alert>
    <Loaded state={state}>{settings => <SettingsForm settings={settings} saved={saved} />}</Loaded>
  </div>
}

const providers = [
  { name: 'OpenAI', mode: 'Responses API · API key', link: 'https://developers.openai.com/api/docs/guides/tools-web-search' },
  { name: 'Anthropic', mode: 'Messages API · API key', link: 'https://platform.claude.com/docs/en/build-with-claude/tool-use/web-search-tool' },
  { name: 'OpenAI-compatible', mode: 'HTTP · API key', link: 'https://developers.openai.com/api/docs/api-reference/chat' },
  { name: 'ChatGPT', mode: 'План ChatGPT · OAuth', link: 'https://developers.openai.com/siwc/token-sharing-open-source' },
]

export function AiPage() {
  return <div className="grid gap-6"><div><h1 className="text-3xl font-semibold tracking-tight">AI-подключения</h1><p className="mt-1 text-sm text-muted-foreground">Маршруты исследования и синтеза.</p></div>
    <Alert><InfoIcon aria-hidden /><AlertTitle>Учебный режим</AlertTitle><AlertDescription>Для реальных запусков нужно подтверждённое подключение. Учебные карточки формируются из фикстур.</AlertDescription></Alert>
    <div className="grid gap-4 sm:grid-cols-2">{providers.map(provider => <Card key={provider.name}>
      <CardHeader><CardTitle className="flex items-center gap-2"><BotIcon className="size-4" />{provider.name}</CardTitle><CardDescription>{provider.mode}</CardDescription></CardHeader>
      <CardContent className="grid justify-items-start gap-3 text-sm"><Status value="not_connected" />
        {provider.name === 'ChatGPT' && <Button variant="outline" disabled>Подключить ChatGPT</Button>}
        <ExternalLink href={provider.link}>Официальный контракт</ExternalLink></CardContent>
    </Card>)}</div>
    <Card><CardHeader><CardTitle>Маршруты и лимиты</CardTitle></CardHeader><CardContent className="grid gap-3 text-sm">
      {['Исследование', 'Синтез'].map(route => <div className="flex justify-between gap-2" key={route}>{route}<Badge variant="outline">Не выбран</Badge></div>)}
      <div className="flex justify-between gap-2">Квота и стоимость<Badge variant="outline">Неизвестны</Badge></div>
      <div className="flex justify-between gap-2">Переход на платный API<Badge variant="outline">Выключен</Badge></div>
    </CardContent></Card>
  </div>
}
