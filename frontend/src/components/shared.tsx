import { AlertCircleIcon, ArrowUpRightIcon, CheckIcon, Clock3Icon, LoaderCircleIcon } from 'lucide-react'
import { type ReactNode, useEffect, useRef, useState } from 'react'
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
  if (!URL.canParse(href) || new URL(href).protocol !== 'https:') return <span>{children}</span>
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
  const [cancelling, setCancelling] = useState(false)
  const key = useRef<string | null>(null)
  const mutation = useRef<AbortController | null>(null)
  const done = useRef(onDone)
  const active = job !== null && ['queued', 'running', 'retry_wait'].includes(job.status)

  useEffect(() => { done.current = onDone }, [onDone])
  useEffect(() => () => { mutation.current?.abort() }, [path])
  useEffect(() => {
    if (!job || !active || cancelling) return
    const controller = new AbortController()
    const timer = window.setTimeout(async () => {
      try {
        const current = await request<Job>(`/jobs/${job.job_id}`, { signal: controller.signal })
        if (!controller.signal.aborted) {
          setError('')
          setJob(current)
        }
      } catch (failure) {
        if (!controller.signal.aborted) {
          setError((failure as Error).message)
          setJob(previous => previous ? { ...previous } : null)
        }
      }
    }, 1200)
    return () => { controller.abort(); window.clearTimeout(timer) }
  }, [job, active, cancelling])
  useEffect(() => {
    if (job && !active) {
      key.current = null
      done.current()
    }
  }, [job, active])

  async function run() {
    if (mutation.current || active) return
    const requestKey = key.current ?? crypto.randomUUID()
    key.current = requestKey
    const controller = new AbortController()
    mutation.current = controller
    setError('')
    setPending(true)
    try {
      const current = await request<Job>(path, { method: 'POST', headers: { 'Idempotency-Key': requestKey }, signal: controller.signal })
      if (!controller.signal.aborted) setJob(current)
    } catch (failure) {
      if (!controller.signal.aborted) setError((failure as Error).message)
    } finally {
      mutation.current = null
      if (!controller.signal.aborted) setPending(false)
    }
  }

  async function cancel() {
    if (!job || mutation.current) return
    const controller = new AbortController()
    mutation.current = controller
    setCancelling(true)
    setError('')
    try {
      const current = await request<Job>(`/jobs/${job.job_id}/cancel`, { method: 'POST', signal: controller.signal })
      if (!controller.signal.aborted) setJob(current)
    } catch (failure) {
      if (!controller.signal.aborted) setError((failure as Error).message)
    } finally {
      mutation.current = null
      if (!controller.signal.aborted) setCancelling(false)
    }
  }

  return <div className="flex flex-wrap items-center gap-2">
    <Button onClick={() => void run()} disabled={pending || active}>{pending ? <LoaderCircleIcon className="animate-spin" /> : icon}{label}</Button>
    {job && <span aria-live="polite"><Status value={job.status} />{job.block_reason && <span className="ml-2 text-xs">{statusText[job.block_reason] ?? job.block_reason}</span>}</span>}
    {active && <Button disabled={cancelling} variant="ghost" size="sm" onClick={() => void cancel()}>Отменить</Button>}
    {error && <span role="alert" className="text-sm text-destructive">{error}</span>}
  </div>
}
