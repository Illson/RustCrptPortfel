import { ActivityIcon, CircleHelpIcon, MoonIcon, SunIcon, UserRoundIcon, WalletIcon } from 'lucide-react'
import { type MouseEvent, useEffect, useState } from 'react'
import { createRoot } from 'react-dom/client'
import { Button, Card, CardContent, CardDescription, CardHeader, Sidebar,
  SidebarContent, SidebarHeader, SidebarMenu, SidebarMenuButton, SidebarMenuItem, SidebarProvider, SidebarTrigger,
  useSidebar } from '@/components/ui/primitives'
import { CsvPortfolios, csvPortfoliosPath, pathForPortfolio } from '@/components/csv-portfolios'
import { CmcMonitorPage, cmcMonitorPath, cmcRequestsPath, marketRequestsPath } from '@/components/cmc-monitor'
import { PortfolioHelp, portfolioHelpPath } from '@/components/portfolio-tools'
import { ProfileHome, readProfileName } from '@/components/profile'
import { Loading, Warning } from '@/components/shared'
import { request, session, type Session } from '@/lib/api'
import { readBook } from '@/lib/csv-portfolios'
import './index.css'

const navigation = [
  { path: '/', name: 'Профиль', icon: UserRoundIcon },
  { path: csvPortfoliosPath, name: 'Мои портфели', icon: WalletIcon },
  { path: marketRequestsPath, name: 'Мониторинг запросов', icon: ActivityIcon },
  { path: portfolioHelpPath, name: 'Подсказки', icon: CircleHelpIcon },
]
const browserZone = Intl.DateTimeFormat().resolvedOptions().timeZone

function Brand() {
  return <div className="flex items-center gap-2.5"><span className="flex size-8 items-center justify-center rounded-lg bg-primary text-primary-foreground"><WalletIcon aria-hidden className="size-4" /></span><span className="font-semibold tracking-tight">dotnetCrptPortfel</span></div>
}

function Workspace() {
  const [address, setAddress] = useState(() => location.pathname + location.search)
  const [sessionState, setSession] = useState<Session | null>(null)
  const [checking, setChecking] = useState(true)
  const [connectionError, setConnectionError] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [profile, setProfile] = useState(readProfileName)
  const [{ book, bookError }, setBook] = useState(() => {
    try { return { book: readBook(), bookError: '' } }
    catch (failure) { return { book: null, bookError: (failure as Error).message } }
  })
  const { isMobile, open, openMobile, setOpenMobile } = useSidebar()
  const [dark, setDark] = useState(() => window.matchMedia('(prefers-color-scheme: dark)').matches)
  const url = new URL(address, location.origin)
  const path = url.pathname
  const isMonitor = [cmcMonitorPath, cmcRequestsPath, marketRequestsPath].includes(path)
  const selectedId = path === csvPortfoliosPath ? url.searchParams.get('portfolio') : null
  const portfolios = book?.portfolios ?? []
  const selected = portfolios.find(item => item.id === selectedId)

  async function refreshSession() {
    setChecking(true)
    setConnectionError('')
    try { setSession(await session()) }
    catch (failure) { setConnectionError((failure as Error).message) }
    finally { setChecking(false) }
  }
  useEffect(() => { if (isMonitor) void refreshSession() }, [isMonitor])
  useEffect(() => { document.documentElement.classList.toggle('dark', dark) }, [dark])
  useEffect(() => {
    const pop = () => { setAddress(location.pathname + location.search); setError('') }
    window.addEventListener('popstate', pop)
    return () => window.removeEventListener('popstate', pop)
  }, [])

  function navigate(next: string) {
    setOpenMobile(false)
    if (next === address) return
    window.history.pushState({}, '', next)
    setAddress(next)
    setError('')
    window.scrollTo(0, 0)
  }
  function follow(event: MouseEvent<HTMLAnchorElement>, next: string) {
    if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return
    event.preventDefault()
    navigate(next)
  }
  async function enableMarketTools() {
    if (connectionError) { await refreshSession(); return }
    setBusy(true)
    setError('')
    try {
      await request('/auth/market-tools', { method: 'POST' })
      await refreshSession()
    } catch (failure) { setError((failure as Error).message) }
    finally { setBusy(false) }
  }

  const active = isMonitor ? marketRequestsPath : path
  const title = selected?.name ?? navigation.find(item => item.path === active)?.name ?? 'Профиль'
  const selectedPath = path === csvPortfoliosPath ? (selected ? pathForPortfolio(selected.id) : csvPortfoliosPath) : ''
  return <>
    <Sidebar>
      <SidebarHeader className="border-b px-4 py-4"><Brand /><span className="break-words text-xs text-muted-foreground">{profile}</span></SidebarHeader>
      <SidebarContent><nav aria-label="Основная навигация"><SidebarMenu>{navigation.map(({ path: target, name, icon: Icon }) =>
        <SidebarMenuItem key={target}><SidebarMenuButton asChild isActive={active === target}>
          <a href={target} aria-current={active === target ? 'page' : undefined} onClick={event => follow(event, target)}>
            <Icon aria-hidden /><span>{name}</span>
          </a>
        </SidebarMenuButton></SidebarMenuItem>)}</SidebarMenu></nav></SidebarContent>
    </Sidebar>
    <div className="min-w-0 flex-1">
      <header className="sticky top-0 z-20 flex min-h-14 flex-wrap items-center justify-between gap-2 border-b bg-background px-3 py-2 sm:px-6">
        <div className="flex min-w-0 items-center gap-2.5">
          <SidebarTrigger aria-label="Меню" aria-expanded={isMobile ? openMobile : open} className="shrink-0" />
          <span className="truncate text-sm font-medium">{title}</span>
        </div>
        <div className="flex min-w-0 max-w-full items-center gap-2">
          <select aria-label="Выбрать портфель" value={selectedPath} onChange={event => navigate(event.target.value || '/')}
            className="h-8 w-40 min-w-0 truncate rounded-md border bg-background px-2 text-sm sm:w-52">
            <option value="">Выбрать портфель</option>
            <option value={csvPortfoliosPath}>Все мои портфели</option>
            {portfolios.map(item => <option key={item.id} value={pathForPortfolio(item.id)}>{item.name}</option>)}
          </select>
          <Button size="icon" variant="ghost" aria-label={dark ? 'Светлая тема' : 'Тёмная тема'} onClick={() => setDark(value => !value)}>{dark ? <SunIcon /> : <MoonIcon />}</Button>
        </div>
      </header>
      <main className="mx-auto grid min-w-0 max-w-6xl gap-5 px-4 py-6 sm:px-6 lg:px-8">
        {error && <Warning title="Не удалось открыть инструмент">{error}</Warning>}
        {path === csvPortfoliosPath ? <CsvPortfolios book={book} bookError={bookError} portfolioId={selectedId}
          onSave={saved => setBook({ book: saved, bookError: '' })} onOverview={() => navigate(csvPortfoliosPath)} /> :
          path === portfolioHelpPath ? <PortfolioHelp /> :
          isMonitor ? (sessionState?.market_tools_authenticated && !checking && !connectionError ?
            <CmcMonitorPage journal={path !== cmcMonitorPath} timezone={browserZone} follow={follow} /> :
            <Card><CardHeader><h1 className="text-lg font-semibold tracking-tight">Мониторинг запросов</h1>
              <CardDescription>{connectionError || (sessionState?.market_tools_available
                ? 'Журнал запросов и кэш истории CoinMarketCap. Сохранённые позиции не покидают браузер.'
                : 'Рыночные инструменты доступны в локальном режиме разработки.')}</CardDescription>
            </CardHeader>{(checking || connectionError || sessionState?.market_tools_available) && <CardContent>{checking ?
              <Loading /> : <Button disabled={busy} onClick={() => void enableMarketTools()}>
                {connectionError ? 'Повторить подключение' : 'Включить рыночные инструменты'}</Button>}</CardContent>}</Card>) :
          <ProfileHome name={profile} onName={setProfile} portfolios={portfolios} bookError={bookError} open={navigate} />}
      </main>
    </div>
  </>
}

createRoot(document.getElementById('root')!).render(<SidebarProvider><Workspace /></SidebarProvider>)
