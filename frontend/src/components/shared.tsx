import { AlertCircleIcon, ArrowUpRightIcon } from 'lucide-react'
import { type ReactNode } from 'react'
import { Alert, AlertDescription, AlertTitle, Skeleton } from './ui/primitives'

export function Warning({ title = 'Данные недоступны', children }: { title?: string; children: ReactNode }) {
  return <Alert><AlertCircleIcon aria-hidden /><AlertTitle>{title}</AlertTitle><AlertDescription>{children}</AlertDescription></Alert>
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
