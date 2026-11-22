import { FolderOpenIcon } from 'lucide-react'
import { useState, type MouseEvent } from 'react'
import type { Portfolio } from '@/lib/csv-portfolios'
import { csvPortfoliosPath, pathForPortfolio } from './csv-portfolios'
import { PortfolioHeading, portfolioHelpPath } from './portfolio-tools'
import { Warning } from './shared'
import { Badge, Button, Card, CardContent, CardDescription, CardHeader, CardTitle, Input } from './ui/primitives'

const profileKey = 'portfel.local-profile.name.v1'

export function readProfileName() {
  try { return localStorage.getItem(profileKey)?.trim().slice(0, 60) || 'Мой профиль' }
  catch { return 'Мой профиль' }
}

export function ProfileHome({ name, onName, portfolios, bookError, open }: {
  name: string; onName: (name: string) => void; portfolios: Portfolio[]; bookError: string; open: (path: string) => void;
}) {
  const [draft, setDraft] = useState(name)
  const [note, setNote] = useState('')
  function follow(event: MouseEvent<HTMLAnchorElement>) {
    if (event.button || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return
    event.preventDefault()
    open(event.currentTarget.pathname + event.currentTarget.search)
  }
  return <div className="grid min-w-0 gap-5">
    <PortfolioHeading title="Портфели" description="Один профиль для своих портфелей и рыночных инструментов." source="Локальный профиль">
      <Button variant="outline" onClick={() => open(portfolioHelpPath)}>Подсказки</Button>
    </PortfolioHeading>
    <Card><CardHeader>
      <CardTitle>Профиль</CardTitle>
      <CardDescription>Имя и портфели хранятся в этом браузере. Вход в аккаунт не нужен.</CardDescription>
    </CardHeader><CardContent>
      <form className="flex flex-wrap items-end gap-3" onSubmit={event => {
        event.preventDefault()
        const value = draft.trim()
        if (!value) { setNote('Введите имя профиля.'); return }
        try { localStorage.setItem(profileKey, value); onName(value); setDraft(value); setNote('Имя профиля сохранено.') }
        catch { setNote('Браузер не позволил сохранить имя профиля.') }
      }}>
        <label className="grid min-w-0 gap-1.5 text-sm font-medium">Имя профиля<Input maxLength={60} required
          value={draft} onChange={event => { setDraft(event.target.value); setNote('') }} /></label>
        <Button type="submit" variant="outline">Сохранить имя</Button>
        {note && <span role="status" className="text-sm text-muted-foreground">{note}</span>}
      </form>
    </CardContent></Card>
    {bookError && <Warning title="Портфели недоступны">{bookError}</Warning>}
    <Card><CardHeader><CardTitle className="flex items-center gap-2"><FolderOpenIcon className="size-4" aria-hidden />Свои портфели</CardTitle>
      <CardDescription>Откройте один сохранённый портфель или перейдите к импорту и общей сводке.</CardDescription>
    </CardHeader><CardContent className="grid min-w-0 gap-2">
      {portfolios.map(portfolio => <Button key={portfolio.id} asChild variant="outline" className="h-auto justify-between whitespace-normal py-2">
        <a href={pathForPortfolio(portfolio.id)} onClick={follow} className="gap-3"><span className="min-w-0 break-words text-left">{portfolio.name}</span>
          <Badge variant="outline">Позиций: {portfolio.holdings.length}</Badge></a>
      </Button>)}
      {!portfolios.length && !bookError && <p className="text-sm text-muted-foreground">Пока нет сохранённых портфелей. Добавьте CSV или ZIP.</p>}
      <Button asChild className="mt-2 w-fit"><a href={csvPortfoliosPath} onClick={follow}>Открыть свои портфели</a></Button>
    </CardContent></Card>
  </div>
}
