import { useEffect, useState } from 'react'

export type Session = { csrf_token: string | null; market_tools_available: boolean; market_tools_authenticated: boolean }

let csrfToken: string | null = null

export async function request<T>(path: string, options: RequestInit = {}): Promise<T> {
  const headers = new Headers(options.headers)
  if (options.body !== undefined) headers.set('Content-Type', 'application/json')
  if (options.method && csrfToken) headers.set('X-CSRF-Token', csrfToken)
  const result = await fetch(`/api/v1${path}`, { ...options, headers, credentials: 'same-origin' })
  if (!result.ok) {
    const error = (await result.json().catch(() => null)) as { message?: string } | null
    throw new Error(error?.message ?? `Ошибка API (${result.status})`)
  }
  return result.status === 204 ? undefined as T : await result.json() as T
}

export async function session() {
  const result = await request<Session>('/auth/session')
  csrfToken = result.csrf_token
  return result
}

export function useLoad<T>(path: string, revision = 0) {
  const [state, setState] = useState<{ path: string; data?: T; error?: string }>({ path })
  useEffect(() => {
    const controller = new AbortController()
    request<T>(path, { signal: controller.signal }).then(
      data => { if (!controller.signal.aborted) setState({ path, data }) },
      (error: Error) => { if (!controller.signal.aborted) setState({ path, error: error.message }) },
    )
    return () => controller.abort()
  }, [path, revision])
  return state.path === path ? state : { path }
}

export function decimal(value: string | null | undefined, digits = 8, minimum = 0) {
  if (value == null) return '—'
  const [whole, fraction = ''] = value.replace(/^-/, '').split('.')
  const scale = BigInt(10) ** BigInt(digits)
  let coefficient = BigInt(whole + fraction.slice(0, digits).padEnd(digits, '0'))
  const guard = fraction[digits] ?? '0'
  if (guard > '5' || (guard === '5' && (/[1-9]/.test(fraction.slice(digits + 1)) || coefficient % 2n === 1n))) coefficient++
  const rest = digits === 0 ? '' : (coefficient % scale).toString().padStart(digits, '0').replace(/0+$/, '').padEnd(minimum, '0')
  const sign = value.startsWith('-') && coefficient !== 0n ? '−' : ''
  if (coefficient === 0n && /[1-9]/.test(value)) return `${value.startsWith('-') ? '−' : ''}< ${digits === 0 ? '1' : '0,' + '0'.repeat(digits - 1) + '1'}`
  return `${sign}${(coefficient / scale).toLocaleString('ru-RU')}${rest ? ',' + rest : ''}`
}

export function dollars(value: string | null | undefined) {
  return value == null ? '—' : `${decimal(value, 2, 2)} $`
}

export function percent(value: string | null | undefined) {
  return value == null ? '—' : `${decimal(value, 2)} %`
}

export function date(value: string | null | undefined, timezone: string) {
  if (!value) return '—'
  return new Intl.DateTimeFormat('ru-RU', { dateStyle: 'medium', timeStyle: 'short', timeZone: timezone }).format(new Date(value))
}
