import { BotIcon, CalendarDaysIcon, CableIcon, LayoutListIcon, LogOutIcon, MenuIcon, MoonIcon, SettingsIcon, SunIcon, WalletIcon } from 'lucide-react'
import { type MouseEvent, useEffect, useState } from 'react'
import { createRoot } from 'react-dom/client'
import { Alert, AlertDescription, AlertTitle, Badge, Button, Card, CardContent, CardDescription, CardHeader, CardTitle, Sheet, SheetContent, SheetDescription, SheetTitle, SheetTrigger, Sidebar } from '@/components/ui/primitives'
import { AssetPage } from '@/components/asset'
import { Feed } from '@/components/feed'
import { Calendar, Dashboard } from '@/components/portfolio'
import { AiPage, SettingsPage, Sources } from '@/components/settings'
import { Loading, Status, Warning } from '@/components/shared'
import { request, session, useLoad, type Session, type Settings } from '@/lib/api'
import './index.css'

const navigation = [
  { path: '/', name: 'Портфель', icon: WalletIcon },
  { path: '/feed', name: 'Лента', icon: LayoutListIcon },
  { path: '/calendar', name: 'Календарь', icon: CalendarDaysIcon },
  { path: '/sources', name: 'Источники', icon: CableIcon },
  { path: '/ai', name: 'AI-подключения', icon: BotIcon },
  { path: '/settings', name: 'Настройки', icon: SettingsIcon },
]

function Brand() {
  return <div className="flex items-center gap-2.5"><span className="flex size-8 items-center justify-center rounded-lg bg-primary text-primary-foreground"><WalletIcon aria-hidden className="size-4" /></span><span className="font-semibold tracking-tight">dotnetCrptPortfel</span></div>
}

function Login({ state, onLogin }: { state: Session; onLogin: () => Promise<void> }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  async function demo() {
    setBusy(true)
    setError('')
    try { await request('/auth/demo', { method: 'POST' }); await onLogin() }
    catch (failure) { setError((failure as Error).message) }
    finally { setBusy(false) }
  }
  return <main className="grid min-h-svh place-items-center bg-muted/35 p-4">
    <div className="grid w-full max-w-md gap-5"><Brand />
      <Card><CardHeader><CardTitle className="text-2xl tracking-tight">Вход в приложение</CardTitle><CardDescription>Анализ активов вашего портфеля CoinMarketCap.</CardDescription></CardHeader>
        <CardContent className="grid gap-4">
          <Button disabled className="w-full">Войти через CoinMarketCap</Button>
          <Alert><AlertTitle>Вход через CoinMarketCap пока недоступен</AlertTitle><AlertDescription><span className="font-mono text-xs">{state.authentication}</span></AlertDescription></Alert>
          <Alert><AlertTitle>Чтение личного портфеля пока недоступно</AlertTitle><AlertDescription><span className="font-mono text-xs">{state.portfolio_access}</span></AlertDescription></Alert>
          {state.demo_available && <Button variant="outline" className="w-full" onClick={() => void demo()} disabled={busy}>Открыть демо-портфель</Button>}
          {error && <Warning title="Не удалось войти">{error}</Warning>}
        </CardContent>
      </Card>
    </div>
  </main>
}

function Workspace({ onLogout }: { onLogout: () => Promise<void> }) {
  const [path, setPath] = useState(location.pathname)
  const [open, setOpen] = useState(false)
  const [dark, setDark] = useState(() => window.matchMedia('(prefers-color-scheme: dark)').matches)
  const [error, setError] = useState('')
  const [override, setOverride] = useState<string | null>(null)
  const settings = useLoad<Settings>('/settings')
  const timezone = override ?? settings.data?.timezone ?? 'Africa/Nairobi'
  useEffect(() => { document.documentElement.classList.toggle('dark', dark) }, [dark])
  useEffect(() => {
    const pop = () => setPath(location.pathname)
    window.addEventListener('popstate', pop)
    return () => window.removeEventListener('popstate', pop)
  }, [])
  function navigate(next: string) {
    if (next !== path) window.history.pushState({}, '', next)
    setPath(next)
    setOpen(false)
    window.scrollTo(0, 0)
  }
  function follow(event: MouseEvent<HTMLAnchorElement>, next: string) {
    if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return
    event.preventDefault()
    navigate(next)
  }
  async function logout() {
    try { await request('/auth/logout', { method: 'POST' }); await onLogout() }
    catch (failure) { setError((failure as Error).message) }
  }

  const matched = path.match(/^\/assets\/(\d+)$/)
  const active = path === '/' || matched ? '/' : path
  const title = navigation.find(item => item.path === active)?.name ?? 'Портфель'
  const sidebar = <>
    <div className="border-b px-4 py-4"><Brand /></div>
    <nav aria-label="Основная навигация" className="grid gap-1 p-3">{navigation.map(({ path: target, name, icon: Icon }) =>
      <a key={target} href={target} aria-current={active === target ? 'page' : undefined} onClick={event => follow(event, target)}
        className="flex items-center gap-2.5 rounded-md px-3 py-2 text-sm outline-none hover:bg-sidebar-accent focus-visible:ring-2 focus-visible:ring-ring aria-[current=page]:bg-sidebar-accent aria-[current=page]:font-medium">
        <Icon aria-hidden className="size-4" />{name}</a>)}</nav>
    <div className="mt-auto border-t p-3"><Button className="w-full justify-start" variant="ghost" onClick={() => void logout()}><LogOutIcon />Выйти</Button></div>
  </>

  return <div className="grid min-h-svh md:grid-cols-[15rem_minmax(0,1fr)]">
    <Sidebar className="sticky top-0 hidden h-svh border-r md:flex">{sidebar}</Sidebar>
    <div className="min-w-0">
      <header className="sticky top-0 z-20 flex h-14 items-center justify-between gap-2 border-b bg-background px-4 sm:px-6">
        <div className="flex items-center gap-2.5">
          <Sheet open={open} onOpenChange={setOpen}><SheetTrigger asChild><Button variant="ghost" size="icon" className="md:hidden" aria-label="Меню"><MenuIcon /></Button></SheetTrigger>
            <SheetContent><SheetTitle className="sr-only">Меню</SheetTitle><SheetDescription className="sr-only">Разделы приложения</SheetDescription><Sidebar className="w-full">{sidebar}</Sidebar></SheetContent></Sheet>
          <span className="text-sm font-medium">{title}</span>
        </div>
        <div className="flex items-center gap-2"><Badge variant="outline">Демо</Badge><Button size="icon" variant="ghost" aria-label={dark ? 'Светлая тема' : 'Тёмная тема'} onClick={() => setDark(value => !value)}>{dark ? <SunIcon /> : <MoonIcon />}</Button></div>
      </header>
      <main className="mx-auto grid max-w-6xl gap-5 px-4 py-6 sm:px-6 lg:px-8">
        {error && <Warning title="Ошибка сессии">{error}</Warning>}
        {matched ? <AssetPage key={matched[1]} id={matched[1]} timezone={timezone} back={() => navigate('/')} /> :
          path === '/feed' ? <Feed timezone={timezone} /> : path === '/calendar' ? <Calendar timezone={timezone} /> :
          path === '/sources' ? <Sources timezone={timezone} /> : path === '/settings' ? <SettingsPage saved={value => setOverride(value.timezone)} /> :
          path === '/ai' ? <AiPage /> : <Dashboard timezone={timezone} navigate={navigate} />}
      </main>
    </div>
  </div>
}

function App() {
  const [state, setState] = useState<Session | null>(null)
  const [error, setError] = useState('')
  const refresh = async () => setState(await session())
  useEffect(() => { void refresh().catch((failure: Error) => setError(failure.message)) }, [])
  if (error) return <main className="mx-auto grid max-w-md gap-4 p-6"><Brand /><Warning title="Сервер приложения недоступен">{error}</Warning><Button onClick={() => location.reload()}>Повторить</Button></main>
  if (!state) return <main className="mx-auto grid max-w-md gap-4 p-6"><Brand /><Loading /></main>
  return state.authenticated ? <Workspace onLogout={refresh} /> : <Login state={state} onLogin={refresh} />
}

createRoot(document.getElementById('root')!).render(<App />)
