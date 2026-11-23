import { type FormEvent, useEffect, useState } from 'react'
import { blockNames, date, request, type Block } from '@/lib/api'
import { Badge, Button, Card, CardContent, CardDescription, CardHeader, CardTitle, Dialog, DialogContent,
  DialogDescription, DialogHeader, DialogTitle, Input } from './ui/primitives'
import { ExternalLink, Loading, Status, Warning } from './shared'
import { LabPortfolioPicker } from './chatgpt-lab-portfolio'
import { actionNames, SignalJournal } from './chatgpt-signal-journal'
import type { Action, Signal } from '@/lib/chatgpt-signal-journal'

export const chatGptLabPath = '/chatgpt-lab'
const api = '/chatgpt-lab'
const usage = 'https://chatgpt.com/settings/usage'
const coins = [['bitcoin', 'Bitcoin (BTC)'], ['ethereum', 'Ethereum (ETH)'], ['solana', 'Solana (SOL)'],
  ['arbitrum', 'Arbitrum (ARB)'], ['dash', 'Dash (DASH)'], ['iota', 'IOTA'], ['terra-luna', 'Terra Classic (LUNC)']]
const localSeen = 'portfel.chatgpt-lab.welcome'
const balanceKey = (id: string) => `portfel.chatgpt-lab.balance.${id}`
const inputClass = 'h-9 w-full min-w-0 rounded-md border bg-background px-2'

type LabSession = { status: 'not_connected' | 'connected' | 'plan_disabled'; email: string | null; csrf_token: string }
type Model = { id: string; name: string }
type Decision = { action: Action; reason: string; revisit?: string | null }
type Topic = Block | 'token_thesis' | 'valuation'
type OpinionReview = { candidate: 'sell' | 'add'; status: 'supported' | 'not_supported' | 'unavailable'; error?: string | null;
  request_id?: string | null; upstream_status?: number | null; terminal_state?: string | null;
  checks: { topic: Topic; result: 'supported' | 'contradicted' | 'unclear'; finding: string; data_as_of: string | null; source_url: string | null }[];
  sources: { url: string; title: string }[] }
type Verdict = { classification: string; confidence: string; thesis: string; counterpoint: string; invalidation: string; basis_blocks: Block[];
  price_case?: { comparison: string; data_as_of: string; source_urls: string[] } | null;
  token_thesis?: { mechanism: 'direct' | 'indirect' | 'none'; explanation: string; source_urls: string[] } | null }
type OpinionPost = { headline: string; summary: string; sections: { block: Block; status: string; text: string; source_urls: string[];
  data_as_of?: string | null; signal?: string }[]; opinion: string; watch: string; verdict?: Verdict }
type OpinionJob = { id: string; coin_id: string; model: string; created_at: string; status: string; error: string | null;
  batch_id?: string | null;
  provider_request_id?: string | null; provider_http_status?: number | null; provider_terminal_state?: string | null;
  unverified_blocks?: Block[] | null;
  decision?: Decision | null;
  review?: OpinionReview | null; completed_at?: string | null;
  raw_output?: { text: string; partial: boolean; truncated: boolean } | null;
  post: OpinionPost | null; draft?: OpinionPost | null; sources: { url: string; title: string }[] | null }

const verdictNames: Record<string, string> = {
  add_case: 'Есть аргументы увеличить позицию', hold_case: 'Есть аргументы удерживать', watch: 'Спорный тезис', exit_review: 'Тезис под угрозой',
  likely_inactive: 'Вероятно неактивен', insufficient: 'Недостаточно данных для вывода',
}
const confidenceNames: Record<string, string> = { low: 'Низкая', medium: 'Средняя', high: 'Высокая' }
const signalNames: Record<string, string> = { positive: 'Положительный сигнал', mixed: 'Смешанный сигнал',
  negative: 'Отрицательный сигнал', critical: 'Критический сигнал' }
const triageOrder = ['likely_inactive', 'exit_review', 'watch', 'insufficient', 'hold_case']
const actionOrder: Action[] = ['sell', 'add', 'hold', 'no_signal']
const topicNames: Record<Topic, string> = { ...blockNames, token_thesis: 'Польза для токена', valuation: 'Оценка цены' }
const isActive = (job: OpinionJob) => job.status === 'queued' || job.status === 'running'
const decisionFor = (job: OpinionJob) => job.status === 'succeeded' ? job.decision : null

function ActionBadge({ action }: { action: Action }) {
  return <Badge variant={action === 'sell' ? 'destructive' : action === 'add' ? 'default' : action === 'hold' ? 'secondary' : 'outline'}>
    {actionNames[action]}
  </Badge>
}

const errors: Record<string, string> = {
  chatgpt_reconnect: 'Сессия ChatGPT истекла или была отозвана. Войдите заново.',
  authentication_required: 'ChatGPT не принял сессию. Попробуйте войти заново.',
  plan_disabled: 'Аккаунт не разрешил использовать подписку ChatGPT. Для обзора нужно дать это разрешение.',
  chatgpt_plan_disabled: 'Использование подписки ChatGPT не разрешено.',
  denied: 'Вход отменён или доступ не предоставлен.',
  chatgpt_permission_denied: 'ChatGPT не разрешил доступ для этого аккаунта или размещения.',
  ineligible_account: 'Выбранному аккаунту или рабочему пространству недоступно использование подписки в этом приложении.',
  permission_denied: 'ChatGPT не разрешил этот запрос.',
  invalid_state: 'Попытка входа истекла или открыта в другом браузере. Начните заново.',
  invalid_callback: 'ChatGPT не вернул данные для регистрации приложения.',
  chatgpt_identity_invalid: 'Не удалось проверить личность аккаунта ChatGPT.',
  identity_invalid: 'Ответ не соответствует ранее подключённому аккаунту.',
  quota_exhausted: 'Достигнут лимит использования. Проверьте настройки ChatGPT.',
  rate_limited: 'ChatGPT временно ограничил запросы.',
  unsupported_capability: 'Модель или политика аккаунта не поддерживает запрос с веб-поиском. Попробуйте другую доступную модель.',
  limited_research: 'Веб-поиск не вернул пригодных источников. Пост не опубликован.',
  invalid_output: 'Не удалось прочитать ответ ChatGPT. Пост не опубликован.',
  invalid_post_format: 'Модель не вернула пост из четырёх блоков в нужном формате. Пост не опубликован.',
  unverified_post_sources: 'Ссылки в посте не совпали с источниками веб-поиска. Пост не опубликован.',
  plan_unexpected_content_type: 'OpenAI вернул ответ вместо потока ChatGPT. Пост не опубликован.',
  plan_invalid_sse: 'В потоке ChatGPT повреждено событие ответа. Пост не опубликован.',
  plan_stream_too_large: 'Ответ веб-поиска превысил допустимый размер. Пост не опубликован.',
  plan_completed_without_text: 'ChatGPT завершил поток, но не вернул финальный текст поста.',
  provider_rejected_schema: 'OpenAI отклонил схему поста для этого запроса. Пост не опубликован.',
  incomplete_response: 'Ответ не завершился. Пост не опубликован.',
  incomplete_output_limit: 'OpenAI остановил генерацию из-за лимита ответа. Пост не опубликован.',
  incomplete_content_filter: 'OpenAI остановил генерацию фильтром содержимого. Пост не опубликован.',
  incomplete_context_limit: 'Ответ остановлен из-за лимита контекста. Пост не опубликован.',
  outcome_unknown: 'Соединение прервалось. Неизвестно, учёл ли ChatGPT запрос. Автоповтора нет.',
  unverified_review_sources: 'Не все ссылки второго поиска совпали с метаданными открытых страниц.',
  invalid_review_format: 'Ответ второго поиска не прошёл проверку формата или свежести данных.',
}

function localBalance(id: string) {
  try { return sessionStorage.getItem(balanceKey(id)) } catch { return null }
}

function DecisionText({ decision }: { decision: Decision }) {
  return <span className="block">
    {decision.reason}
    {decision.revisit && <span className="block text-muted-foreground"><strong>Пересмотр:</strong> {decision.revisit}</span>}
  </span>
}

function ReviewDetails({ review }: { review: OpinionReview }) {
  const links = new Map(review.sources.map(source => [source.url, source.title]))
  return <div className="grid gap-2 rounded-md border p-3">
    <h3 className="font-semibold">Повторный поиск · {actionNames[review.candidate]}</h3>
    <p>{review.status === 'supported' ? 'GPT нашёл поддержку ключевых утверждений в открытых источниках.'
      : review.status === 'not_supported' ? 'Кандидат не прошёл: нужны два подтверждения от разных издателей.'
        : review.error === 'unverified_review_sources' || review.error === 'invalid_review_format' ? errors[review.error]
          : 'Повторный поиск не завершился. Первый отчёт сохранён.'}</p>
    {review.error && <code className="break-all">{[review.error, review.upstream_status && `HTTP ${review.upstream_status}`,
      review.terminal_state, review.request_id].filter(Boolean).join(' · ')}</code>}
    {review.checks.map(check => <div key={check.topic}>
      <strong>{topicNames[check.topic]}:</strong> {check.result === 'supported' ? 'Поддержано' : check.result === 'contradicted' ? 'Опровергнуто' : 'Неясно'}
      {check.data_as_of && ` · ${date(check.data_as_of, 'UTC')}`}
      <p>{check.finding}</p>
      {check.source_url && <ExternalLink href={check.source_url}>{links.get(check.source_url) ?? check.source_url}</ExternalLink>}
    </div>)}
  </div>
}

function OpinionCard({ job, inlineDecision = false }: { job: OpinionJob; inlineDecision?: boolean }) {
  const post = job.post ?? job.draft
  const isDraft = !job.post && !!job.draft
  const isPartial = job.status === 'partial'
  const decision = decisionFor(job)
  const balance = localBalance(job.id)
  const links = new Map(job.sources?.map(source => [source.url, source.title]))
  return <Card className="min-w-0 break-words">
    <CardHeader>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <CardTitle>{post?.headline ?? `Обзор: ${job.coin_id}`}</CardTitle>
        {isDraft ? <Badge variant="outline">Черновик</Badge> : isPartial ? <Badge variant="outline">Частично</Badge>
          : job.status === 'skipped' ? <Badge variant="outline">Пропущено</Badge>
          : <Status value={job.status} />}
      </div>
      <CardDescription>{job.coin_id} · {job.model} · {date(job.created_at, 'UTC')} UTC</CardDescription>
    </CardHeader>
    <CardContent className="grid gap-4 text-sm">
      {balance !== null && <p>На балансе: <strong>{balance}</strong> <span className="text-muted-foreground">· только в этой вкладке</span></p>}
      {decision && !inlineDecision && <div className="grid gap-2 rounded-md border p-3">
        <div className="flex flex-wrap items-center gap-2">
          <ActionBadge action={decision.action} />
          {decision.action !== 'no_signal' && post?.verdict && <span className="text-xs text-muted-foreground">
            Уверенность: {confidenceNames[post.verdict.confidence]?.toLowerCase() ?? 'не указана'}
          </span>}
        </div>
        <DecisionText decision={decision} />
        <p className="text-xs text-muted-foreground">Сигнал по активу: без учёта вашей доли, цены входа и налогов.</p>
      </div>}
      {job.error && <Warning title={isDraft ? 'Пост не опубликован' : 'Обзор не сформирован'}>
        {job.status === 'skipped' && 'Этот запрос не отправлен. '}
        {errors[job.error] ?? 'Не удалось завершить запрос к ChatGPT. Автоповтора нет.'}
        {job.unverified_blocks?.length ? <p className="mt-1">Не прошли сверку: {job.unverified_blocks.map(block => blockNames[block]).join(', ')}.</p> : null}
        <details className="mt-2">
          <summary className="cursor-pointer">Диагностика</summary>
          <code className="break-all">{[job.error, job.provider_http_status && `HTTP ${job.provider_http_status}`, job.provider_terminal_state, job.provider_request_id]
            .filter(Boolean).join(' · ')}</code>
        </details>
      </Warning>}
      {isPartial && <Warning title="Часть источников не подтверждена">
        Блоки без совпавших ссылок и общий вердикт GPT скрыты. Скрытые блоки: {job.unverified_blocks?.map(block => blockNames[block]).join(', ')}.
      </Warning>}
      {post && <details className="rounded-md border p-3">
        <summary className="cursor-pointer font-medium">Аргументы и источники</summary>
        <div className="mt-3 grid gap-3">
        <p>{post.summary}</p>
        <p className="text-muted-foreground">{isDraft
          ? 'Это черновик GPT из неподтверждённого ответа. Утверждения и ссылки не проверены.'
          : 'Обзор ChatGPT по веб-источникам. Приложение проверяет ссылки, но не сверяет содержание источников. Это оценка актива, не указание совершить сделку.'}</p>
        {!isDraft && !isPartial && post.verdict && <div className="grid gap-2 rounded-md border p-3">
          <div className="flex flex-wrap items-center gap-2">
            <h3 className="font-semibold">{verdictNames[post.verdict.classification]}</h3>
            <Badge variant="outline">Уверенность: {confidenceNames[post.verdict.confidence]?.toLowerCase() ?? 'не указана'}</Badge>
          </div>
           {job.review && <p className="text-muted-foreground">Первичный вывод, до повторного поиска.</p>}
           <p>{post.verdict.thesis}</p>
          <p><strong>Контраргумент:</strong> {post.verdict.counterpoint}</p>
           <p><strong>Что изменит оценку:</strong> {post.verdict.invalidation}</p>
           {post.verdict.token_thesis && <div>
             <p><strong>Польза для держателя:</strong> {post.verdict.token_thesis.explanation}</p>
             <ul className="mt-1 grid gap-1">{post.verdict.token_thesis.source_urls.map(url =>
               <li key={url}><ExternalLink href={url}>{links.get(url) ?? url}</ExternalLink></li>)}</ul>
           </div>}
          {post.verdict.price_case && <div>
            <p><strong>Аргумент по цене ({date(post.verdict.price_case.data_as_of, 'UTC')}):</strong> {post.verdict.price_case.comparison}</p>
            <ul className="mt-1 grid gap-1">{post.verdict.price_case.source_urls.map(url =>
              <li key={url}><ExternalLink href={url}>{links.get(url) ?? url}</ExternalLink></li>)}</ul>
         </div>}
         {job.review && <ReviewDetails review={job.review} />}
          {post.verdict.basis_blocks.length > 0 && <p className="text-muted-foreground">
            Основания: {post.verdict.basis_blocks.map(block => blockNames[block]).join(', ')}
          </p>}
        </div>}
        <div className="grid gap-3 sm:grid-cols-2">{post.sections.map(section => <section key={section.block} className="grid min-w-0 gap-2 rounded-md border p-3">
          <div className="flex flex-wrap items-center gap-2">
            <h3 className="font-medium">{blockNames[section.block]}</h3>
            {!isDraft && <Badge variant="outline">{section.status === 'reported' ? 'По источникам' : 'Недостаточно данных'}</Badge>}
            {!isDraft && section.signal && signalNames[section.signal] && <Badge variant="outline">{signalNames[section.signal]}</Badge>}
          </div>
          <p>{section.text}</p>
          {!isDraft && section.status === 'reported' && <p className="text-xs text-muted-foreground">
            Дата данных: {section.data_as_of ? date(section.data_as_of, 'UTC') : 'не установлена'}
          </p>}
          {!isDraft && section.source_urls.length > 0 && <ul className="grid gap-1">{section.source_urls.map(url => <li key={url}>
            <ExternalLink href={url}>{links.get(url) === 'Источник веб-поиска' ? new URL(url).hostname : links.get(url) ?? url}</ExternalLink>
          </li>)}</ul>}
        </section>)}</div>
        <div><h3 className="font-semibold">Мнение и риски</h3><p>{post.opinion}</p></div>
        <div><h3 className="font-semibold">Что отслеживать дальше</h3><p>{post.watch}</p></div>
        </div>
      </details>}
      {(job.raw_output || job.status === 'failed') && <details className="rounded-md border p-3 text-sm">
        <summary className="cursor-pointer font-medium">Ответ GPT</summary>
        {job.raw_output ? <>
          {(job.raw_output.partial || job.raw_output.truncated || job.error || isPartial) && <p className="mt-2 text-muted-foreground">
            {job.raw_output.partial ? 'Полученный фрагмент. ' : ''}
            {job.raw_output.truncated ? 'Показано начало ответа. ' : ''}
            {isPartial ? 'Этот текст не опубликован в таком виде.' : job.error ? 'Этот текст не опубликован как пост.' : ''}
          </p>}
          <pre className="mt-2 max-h-96 overflow-auto whitespace-pre-wrap break-words rounded-md bg-muted p-2 text-xs">{job.raw_output.text}</pre>
        </> : <p className="mt-2 text-muted-foreground">Текст модели не получен.</p>}
      </details>}
    </CardContent>
  </Card>
}

function BatchPanel({ jobs, cancel, busy }: { jobs: OpinionJob[]; cancel: () => void; busy: boolean }) {
  const remaining = jobs.filter(isActive).length
  const done = jobs.filter(job => job.status === 'succeeded').length
  const partial = jobs.filter(job => job.status === 'partial').length
  const skipped = jobs.filter(job => job.status === 'skipped').length
  const cancelled = jobs.filter(job => job.status === 'cancelled').length
  const failed = jobs.length - done - partial - remaining - skipped - cancelled
  const rank = (job: OpinionJob) => {
    const decision = decisionFor(job)
    if (decision) return actionOrder.indexOf(decision.action)
    const value = triageOrder.indexOf(job.post?.verdict?.classification ?? '')
    return actionOrder.length + (value >= 0 ? value : job.status === 'failed' ? 3 : 5)
  }
  return <Card>
    <CardHeader><CardTitle>Обзор портфеля</CardTitle>
      <CardDescription>{date(jobs[0].created_at, 'UTC')} UTC · {jobs[0].model}</CardDescription></CardHeader>
    <CardContent className="grid gap-2 text-sm">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p role="status">Готово: {done} из {jobs.length} · осталось: {remaining}
          {partial > 0 && ` · частично: ${partial}`}{failed > 0 && ` · сбои: ${failed}`}{skipped > 0 && ` · пропущено: ${skipped}`}
          {cancelled > 0 && ` · отменено: ${cancelled}`}</p>
        {remaining > 0 && <Button size="sm" variant="outline" disabled={busy} onClick={cancel}>Остановить пакет</Button>}
      </div>
      {jobs.some(decisionFor) && <>
        <ul aria-label="Сигналы по пакету" className="flex flex-wrap gap-2">
          {actionOrder.map(action => <li key={action} className="flex items-center gap-1 rounded-md border px-2 py-1">
            <ActionBadge action={action} />{jobs.filter(job => decisionFor(job)?.action === action).length}
          </li>)}
        </ul>
        <p className="text-xs text-muted-foreground">Сигналы GPT по активам, без учёта ваших долей и налогов. Откройте строку для источников.</p>
      </>}
      {skipped > 0 && <p className="text-muted-foreground">Запросы по оставшимся монетам не отправлены после ошибки провайдера.</p>}
      {[...jobs].sort((a, b) => rank(a) - rank(b) || a.coin_id.localeCompare(b.coin_id)).map(job => {
        const decision = decisionFor(job)
        return <details key={job.id} className="rounded-md border p-2">
          <summary className="cursor-pointer break-words font-medium">
            {job.coin_id} · {decision ? <ActionBadge action={decision.action} />
              : job.status === 'partial' ? 'Частичный обзор' : job.post?.verdict ? verdictNames[job.post.verdict.classification] : job.draft ? 'Черновик'
                : job.status === 'skipped' ? 'Пропущено' : job.error === 'limited_research' ? 'Недостаточно источников' : <Status value={job.status} />}
            {decision && <span className="mt-1 block text-sm font-normal"><DecisionText decision={decision} /></span>}
          </summary>
          <div className="mt-2"><OpinionCard job={job} inlineDecision /></div>
        </details>
      })}
    </CardContent>
  </Card>
}

export function ChatGptLab() {
  const [session, setSession] = useState<LabSession | null>(null)
  const [models, setModels] = useState<Model[] | null>(null)
  const [model, setModel] = useState('')
  const [coin, setCoin] = useState('bitcoin')
  const [other, setOther] = useState('')
  const [balance, setBalance] = useState('')
  const [scope, setScope] = useState('coin')
  const [jobs, setJobs] = useState<OpinionJob[]>([])
  const [jobsLoaded, setJobsLoaded] = useState(false)
  const [busy, setBusy] = useState(false)
  const [loadError, setLoadError] = useState('')
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const [welcome, setWelcome] = useState(false)
  const [connection] = useState(() => new URLSearchParams(window.location.search).get('connection'))
  const coinId = coin === 'other' ? other.trim() : coin
  const active = jobs.some(isActive)
  const groups = new Map<string, OpinionJob[]>()
  for (const job of jobs) {
    const key = job.batch_id ?? job.id
    const group = groups.get(key)
    if (group) group.push(job)
    else groups.set(key, [job])
  }
  const connected = session?.status === 'connected'
  const signals = jobs.flatMap((job): Signal[] => {
    const action = decisionFor(job)?.action
    return job.completed_at && action && action !== 'no_signal'
      ? [{ id: job.id, coin_id: job.coin_id, model: job.model, completed_at: job.completed_at, action }] : []
  })

  useEffect(() => {
    const controller = new AbortController()
    if (connection) window.history.replaceState(window.history.state, '', chatGptLabPath)
    request<LabSession>(`${api}/session`, { signal: controller.signal }).then(result => {
      if (controller.signal.aborted) return
      setSession(result)
      if (result.status === 'connected' && connection === 'connected') {
        try {
          if (!localStorage.getItem(localSeen)) {
            localStorage.setItem(localSeen, '1')
            setWelcome(true)
          }
        } catch { setWelcome(true) }
      } else if (connection) setError(errors[connection] ?? 'Не удалось завершить подключение ChatGPT.')
    }, (failure: Error) => { if (!controller.signal.aborted) setLoadError(failure.message) })
    return () => controller.abort()
  }, [connection])

  useEffect(() => {
    if (!session) return
    const controller = new AbortController()
    request<OpinionJob[]>(`${api}/jobs`, { signal: controller.signal }).then(
      result => { if (!controller.signal.aborted) { setJobs(result); setJobsLoaded(true) } },
      (failure: Error) => { if (!controller.signal.aborted) { setError(failure.message); setJobsLoaded(true) } },
    )
    return () => controller.abort()
  }, [session])

  useEffect(() => {
    if (!connected) return
    const controller = new AbortController()
    request<Model[]>(`${api}/models`, { signal: controller.signal }).then(result => {
      if (controller.signal.aborted) return
      setModels(result)
      setModel(current => result.some(item => item.id === current) ? current : result[0]?.id ?? '')
    }, (failure: Error) => { if (!controller.signal.aborted) { setModels([]); setError(failure.message) } })
    return () => controller.abort()
  }, [connected])

  useEffect(() => {
    if (!active) return
    const controller = new AbortController()
    let timer = window.setTimeout(poll, 1500)
    async function poll() {
      try {
        const result = await request<OpinionJob[]>(`${api}/jobs`, { signal: controller.signal })
        if (controller.signal.aborted) return
        setJobs(result)
        if (!result.some(isActive)) return
      } catch (failure) { if (!controller.signal.aborted) setError((failure as Error).message) }
      if (!controller.signal.aborted) timer = window.setTimeout(poll, 1500)
    }
    return () => { controller.abort(); window.clearTimeout(timer) }
  }, [active])

  async function update(action: 'connect' | 'disconnect' | 'jobs' | { coins: string[] } | { cancel: string }) {
    if (!session || busy) return
    setBusy(true)
    setError('')
    setNotice('')
    const headers = { 'X-CSRF-Token': session.csrf_token }
    try {
      if (action === 'connect') {
        const result = await request<{ url: string }>(`${api}/connect`, { method: 'POST', headers })
        window.location.assign(result.url)
      } else if (action === 'disconnect') {
        const result = await request<{ revoked: boolean }>(`${api}/disconnect`, { method: 'POST', headers })
        setSession(await request<LabSession>(`${api}/session`))
        setModels(null)
        setNotice(result.revoked ? 'Сессия ChatGPT отключена.' : 'Сессия удалена локально. Отзыв не подтверждён — проверьте настройки ChatGPT.')
      } else if (typeof action === 'object' && 'coins' in action) {
        const id = crypto.randomUUID()
        const batch = await request<OpinionJob[]>(`${api}/batches`, { method: 'POST', headers,
          body: JSON.stringify({ id, coin_ids: action.coins, model }) })
        setJobs(previous => [...batch, ...previous.filter(item => item.batch_id !== id)])
      } else if (typeof action === 'object') {
        await request<void>(`${api}/batches/${action.cancel}/cancel`, { method: 'POST', headers })
        setJobs(await request<OpinionJob[]>(`${api}/jobs`))
      } else {
        if (balance.trim() && !/^\d{1,160}(?:[.,]\d{1,96})?$/.test(balance.trim())) throw new Error('Введите неотрицательное количество монет.')
        const id = crypto.randomUUID()
        const job = await request<OpinionJob>(`${api}/jobs`, { method: 'POST', headers, body: JSON.stringify({ id, coin_id: coinId, model }) })
        if (balance.trim()) {
          try { sessionStorage.setItem(balanceKey(job.id), balance.trim().replace('.', ',')) } catch { /* Optional local display. */ }
        }
        setJobs(previous => [job, ...previous.filter(item => item.id !== job.id)])
      }
    } catch (failure) {
      setError((failure as Error).message)
      if (action === 'jobs' || typeof action === 'object') await request<OpinionJob[]>(`${api}/jobs`).then(setJobs, () => {})
    } finally { setBusy(false) }
  }

  function submit(event: FormEvent) { event.preventDefault(); void update('jobs') }

  return <main className="mx-auto grid max-w-4xl gap-5 p-4 sm:p-6">
    <header className="flex flex-wrap items-start justify-between gap-3">
      <div><h1 className="text-3xl font-semibold tracking-tight">Тест ChatGPT: мнение о монете</h1>
        <p className="mt-1 text-sm text-muted-foreground">Разбор тезиса удержания одной монеты или выбранных позиций портфеля. Веб-поиск по подписке ChatGPT.</p></div>
      <Button asChild variant="outline"><a href="/portfolios">Мои портфели</a></Button>
    </header>
    {loadError ? <Warning title="Тестовая страница недоступна">
      Запустите локальное приложение в Windows командой <code>.\local.cmd -ChatGptLab</code> и откройте адрес на <code>127.0.0.1</code>.
      <span className="mt-1 block">{loadError}</span>
    </Warning> : !session ? <Loading /> : <>
      <Card>
        <CardHeader><CardTitle>Подписка ChatGPT</CardTitle>
          <CardDescription>{connected ? 'Запросы используют подписку ChatGPT. На платный API приложение не переключается.'
            : 'Подключите аккаунт по официальному OAuth. Ключи и токены вводить не нужно.'}</CardDescription></CardHeader>
        <CardContent className="grid gap-3 text-sm">
          {session.email && <p className="break-all">{session.email}</p>}
          <div className="flex flex-wrap items-center gap-3">
            {connected && <Badge>Используется подписка ChatGPT</Badge>}
            <Button disabled={busy || active} onClick={() => void update('connect')} variant={connected ? 'outline' : 'default'}>
              {connected ? 'Повторить вход' : 'Continue with ChatGPT'}
            </Button>
            {session.status !== 'not_connected' && <Button variant="outline" disabled={busy} onClick={() => void update('disconnect')}>Отключить</Button>}
            <ExternalLink href={usage}>Управлять использованием в ChatGPT</ExternalLink>
          </div>
          {notice && <p role="status">{notice}</p>}
        </CardContent>
      </Card>
      {error && <Warning title="Не удалось выполнить действие">{error}</Warning>}
      {connected && <Card>
        <CardHeader><CardTitle>Новый обзор</CardTitle>
          <CardDescription>ChatGPT получает публичный CoinGecko ID и время обзора. Для продажи и докупки повторно проверяет только публичные утверждения. Баланс не отправляется.</CardDescription></CardHeader>
        <CardContent className="grid gap-4">
          <div className="grid gap-3 sm:grid-cols-2">
            <label className="grid gap-1 text-sm">Что анализировать<select className={inputClass} value={scope} onChange={e => setScope(e.target.value)}>
              <option value="coin">Одну монету</option><option value="portfolio">Локальный портфель</option>
            </select></label>
            <label className="grid gap-1 text-sm">Модель<select className={inputClass} value={model} onChange={e => setModel(e.target.value)} disabled={!models?.length}>
              {!models?.length && <option value="">{models === null ? 'Загрузка…' : 'Нет доступных моделей'}</option>}
              {models?.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
            </select></label>
          </div>
          {scope === 'portfolio' ? <LabPortfolioPicker disabled={busy || !jobsLoaded || active || !model}
            submit={ids => void update({ coins: ids })} /> : <form onSubmit={submit} className="grid gap-4">
            <label className="grid gap-1 text-sm">Монета<select className={inputClass} value={coin} onChange={e => setCoin(e.target.value)}>
              {coins.map(([id, name]) => <option key={id} value={id}>{name}</option>)}<option value="other">Другая (CoinGecko ID)</option>
            </select></label>
            {coin === 'other' && <label className="grid gap-1 text-sm">CoinGecko ID
              <Input maxLength={120} value={other} onChange={e => setOther(e.target.value)} autoComplete="off" required />
            </label>}
            <label className="grid gap-1 text-sm">Количество на балансе (необязательно)
              <Input value={balance} onChange={e => setBalance(e.target.value)} inputMode="decimal" maxLength={257} autoComplete="off" />
            </label>
            <div><Button type="submit" disabled={busy || !jobsLoaded || active || !model || !/^[a-z0-9_.-]{1,120}$/.test(coinId)}>Сформировать мнение</Button></div>
          </form>}
          {active && <p role="status" className="text-sm text-muted-foreground">Задание отправлено. Глубокий веб-поиск может занять несколько минут на монету.</p>}
        </CardContent>
      </Card>}
      {jobs.length > 0 && <section aria-label="Последние обзоры" className="grid gap-4">
        {[...groups].map(([id, group]) => group[0].batch_id
          ? <BatchPanel key={id} jobs={group} cancel={() => void update({ cancel: id })} busy={busy} />
          : <OpinionCard key={id} job={group[0]} />)}
      </section>}
      <SignalJournal signals={signals} />
    </>}
    <Dialog open={welcome} onOpenChange={setWelcome}><DialogContent>
      <DialogHeader><DialogTitle>Используется подписка ChatGPT</DialogTitle>
        <DialogDescription>Подходящие запросы будут учтены в использовании вашего плана ChatGPT.</DialogDescription></DialogHeader>
      <ExternalLink href={usage}>Управлять использованием в ChatGPT</ExternalLink>
      <Button onClick={() => setWelcome(false)}>Понятно</Button>
    </DialogContent></Dialog>
  </main>
}
