import { useEffect, useState } from 'react'
import { date, percent } from '@/lib/api'
import { journalKey, readSignals, recordSignals, sampleSignals, scoreSignal, wantsQuote, type Action, type Signal, type SignalEntry } from '@/lib/chatgpt-signal-journal'
import { Button } from './ui/primitives'
import { ExternalLink } from './shared'

export const actionNames: Record<Action, string> = { sell: 'Продаём', hold: 'Удерживаем', add: 'Докупаем', no_signal: 'Нет сигнала' }

function Horizon({ entry, days, now }: { entry: SignalEntry; days: 30 | 90; now: number }) {
  const outcome = scoreSignal(entry, days, now)
  return <div className="min-w-0">
    <strong>{days} дней:</strong>{' '}
    {outcome.status === 'ready' ? <>
      {percent(outcome.strategy)} по сигналу / {percent(outcome.hold)} при удержании
      <p>Разница: {percent(outcome.difference).replace(' %', ' п.п.')} · {date(outcome.end.as_of, 'UTC')} UTC</p>
      <p>Наблюдаемая просадка: {outcome.drawdown === null ? 'нет промежуточных снимков'
        : `${percent(outcome.drawdown)} по сигналу / ${percent(outcome.holdDrawdown)} при удержании`}</p>
    </> : outcome.status === 'pending' ? entry.start ? 'ожидается котировка на контрольную дату' : 'ожидается стартовая котировка'
      : entry.start ? 'нет снимка в окне контрольной даты' : 'нет стартовой котировки'}
  </div>
}

export function SignalJournal({ signals }: { signals: Signal[] }) {
  const [entries, setEntries] = useState<SignalEntry[]>([])
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [now, setNow] = useState(Date.now)
  // Ignore unrelated updates to queued or running jobs while a snapshot is in flight.
  const payload = JSON.stringify(signals)

  useEffect(() => {
    const controller = new AbortController()
    const update = async () => {
      try {
        const incoming = JSON.parse(payload) as Signal[]
        const incomingIds = new Set(incoming.map(signal => signal.id))
        const saved = await recordSignals(incoming)
        if (controller.signal.aborted) return
        setEntries(saved)
        setNow(Date.now())
        const pending = saved.filter(entry => incomingIds.has(entry.id) && !entry.start && wantsQuote(entry, Date.now()))
        if (pending.length === 0) return
        await sampleSignals(pending, controller.signal)
        if (!controller.signal.aborted) { setEntries(readSignals()); setNow(Date.now()); setError('') }
      } catch (failure) {
        if (!controller.signal.aborted) setError(`Журнал: ${(failure as Error).message}`)
      }
    }
    void update()
    function changed(event: StorageEvent) {
      if (event.key !== journalKey && event.key !== null) return
      try { setEntries(readSignals()); setNow(Date.now()) } catch (failure) { setError((failure as Error).message) }
    }
    window.addEventListener('storage', changed)
    return () => { controller.abort(); window.removeEventListener('storage', changed) }
  }, [payload])

  async function refresh() {
    setBusy(true)
    setError('')
    try { setEntries(await sampleSignals(readSignals(), AbortSignal.timeout(30_000))) }
    catch (failure) {
      try { setEntries(readSignals()) } catch { /* Preserve the original error. */ }
      setError(`Не все снимки удалось обновить: ${(failure as Error).message}`)
    } finally { setNow(Date.now()); setBusy(false) }
  }

  return <details className="rounded-md border p-3 text-sm">
    <summary className="cursor-pointer font-medium">Журнал сигналов за 30/90 дней{entries.length > 0 && ` · ${entries.length}`}</summary>
    <div className="mt-3 grid gap-3">
      <p className="text-muted-foreground">Хранится в этом браузере. Стартовый снимок берётся при получении сигнала. Обновляйте снимки по ходу наблюдения
        и в течение двух суток после 30-го и 90-го дня: пропущенные котировки не восстанавливаются.</p>
      <p className="text-muted-foreground">Условный старт: одна монета и столько же в USD. Продажа переводит монету в USD; докупка тратит запас USD.
        Комиссия 0,10% за одно действие; без проскальзывания, налогов и закрывающей сделки. Просадка только по сохранённым точкам.</p>
      <div className="flex flex-wrap items-center gap-3">
        <Button size="sm" variant="outline" disabled={busy || !entries.some(entry => wantsQuote(entry, now))} onClick={() => void refresh()}>
          {busy ? 'Получаем снимки…' : 'Записать свежие котировки'}
        </Button>
        <ExternalLink href="https://www.coingecko.com/en/api">Котировки CoinGecko, USD</ExternalLink>
      </div>
      {error && <p role="alert">{error}</p>}
      {entries.length === 0 && <p>Пока нет сигналов для наблюдения.</p>}
      {entries.map(entry => <div key={entry.id} className="grid gap-2 rounded-md border p-2">
        <p><strong>{entry.coin_id} · {actionNames[entry.action]}</strong> · {date(entry.completed_at, 'UTC')} UTC · {entry.model}
          {entry.start && <span className="block text-muted-foreground">Старт: {entry.start.usd} USD · {date(entry.start.as_of, 'UTC')} UTC</span>}</p>
        <div className="grid gap-2 sm:grid-cols-2">
          <Horizon entry={entry} days={30} now={now} />
          <Horizon entry={entry} days={90} now={now} />
        </div>
      </div>)}
    </div>
  </details>
}
