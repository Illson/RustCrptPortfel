import { AlertCircleIcon, ArrowUpRightIcon, CheckIcon, Clock3Icon, LoaderCircleIcon } from 'lucide-react'
import { type ReactNode, useState } from 'react'
import { Alert, AlertDescription, AlertTitle, Badge, Button, Card, CardContent, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, Skeleton } from './ui/primitives'
import { request, statusText, type Job } from '@/lib/api'

export function Status({ value }: { value: string }) {
  const Icon = ['ok', 'succeeded', 'confirmed'].includes(value) ? CheckIcon : ['stale', 'queued', 'running', 'postponed'].includes(value) ? Clock3Icon : null
  return <Badge variant={Icon ? 'secondary' : 'outline'}>{Icon && <Icon aria-hidden />}{statusText[value] ?? value}</Badge>
}

export function Warning({ title = 'Данные недоступны', children }: { title?: string; children: ReactNode }) {
  return <Alert><AlertCircleIcon aria-hidden /><AlertTitle>{title}</AlertTitle><AlertDescription>{children}</AlertDescription></Alert>
}

export function Empty({ children }: { children: ReactNode }) {
  return <Card><CardContent className="text-sm text-muted-foreground">{children}</CardContent></Card>
}

export function Loading() {
  return <div role="status" aria-label="Загрузка" className="grid gap-3"><Skeleton className="h-12" /><Skeleton className="h-32" /></div>
}

export function Loaded<T>({ state, children }: { state: { data?: T; error?: string }; children: (data: T) => ReactNode }) {
  if (state.error) return <Warning>{state.error}</Warning>
  if (state.data === undefined) return <Loading />
  return children(state.data)
}

export function ExternalLink({ href, children }: { href: string; children: ReactNode }) {
  return <a href={href} target="_blank" rel="noreferrer" className="inline-flex items-center gap-1 font-medium underline-offset-4 hover:underline">{children}<ArrowUpRightIcon aria-hidden className="size-3 shrink-0" /></a>
}

export function Picker({ label, value, options, setValue }: { label: string; value: string; options: [string, string][]; setValue: (value: string) => void }) {
  return <label className="grid gap-1.5 text-sm font-medium">{label}<Select value={value} onValueChange={setValue}>
    <SelectTrigger aria-label={label}><SelectValue /></SelectTrigger>
    <SelectContent>{options.map(([key, title]) => <SelectItem key={key} value={key}>{title}</SelectItem>)}</SelectContent>
  </Select></label>
}

export function RunJob({ path, label, icon, onDone }: { path: string; label: string; icon: ReactNode; onDone: () => void }) {
  const [job, setJob] = useState<Job | null>(null)
  const [error, setError] = useState('')
  const [pending, setPending] = useState(false)
  const [key, setKey] = useState<string | null>(null)

  async function run() {
    const requestKey = key ?? crypto.randomUUID()
    setError('')
    setPending(true)
    setKey(requestKey)
    try {
      let current = await request<Job>(path, { method: 'POST', headers: { 'Idempotency-Key': requestKey } })
      setJob(current)
      for (let attempt = 0; attempt < 30 && ['queued', 'running', 'retry_wait'].includes(current.status); attempt++) {
        await new Promise(resolve => setTimeout(resolve, 1000))
        current = await request<Job>(`/jobs/${current.job_id}`)
        setJob(current)
      }
      if (!['queued', 'running', 'retry_wait'].includes(current.status)) setKey(null)
      onDone()
    } catch (failure) {
      setError((failure as Error).message)
    } finally { setPending(false) }
  }

  async function cancel() {
    if (!job) return
    try { setJob(await request<Job>(`/jobs/${job.job_id}/cancel`, { method: 'POST' })); onDone() }
    catch (failure) { setError((failure as Error).message) }
  }

  return <div className="flex flex-wrap items-center gap-2">
    <Button onClick={() => void run()} disabled={pending}>{pending ? <LoaderCircleIcon className="animate-spin" /> : icon}{label}</Button>
    {job && <span aria-live="polite"><Status value={job.status} /></span>}
    {job && ['queued', 'running', 'retry_wait'].includes(job.status) && <Button variant="ghost" size="sm" onClick={() => void cancel()}>Отменить</Button>}
    {error && <span role="alert" className="text-sm text-destructive">{error}</span>}
  </div>
}
