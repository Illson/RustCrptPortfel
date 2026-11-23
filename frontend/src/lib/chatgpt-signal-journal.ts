import { quoteBatches, type UsdQuote } from './portfolio-valuation'
import { request } from './api'

export type Action = 'sell' | 'hold' | 'add' | 'no_signal'
export type Signal = { id: string; coin_id: string; model: string; completed_at: string; action: Exclude<Action, 'no_signal'> }
type Sample = { usd: string; as_of: string }
export type SignalEntry = Signal & { start?: Sample; daily: Sample[]; at30?: Sample; at90?: Sample }

export const journalKey = 'portfel.chatgpt-lab.signals.v1'
const maxEntries = 300
const day = 86_400_000
const minute = 60_000
const entryWindow = 60 * minute
const entrySkew = 5 * minute
const closeWindow = 2 * day
const maxQuoteAge = 15 * minute
const idPattern = /^[a-z0-9_.-]{1,120}$/
const pricePattern = /^(?:0|[1-9]\d{0,256})(?:\.\d{1,96})?$/

function validSample(sample: Sample | undefined): sample is Sample {
  return !!sample && typeof sample.usd === 'string' && pricePattern.test(sample.usd) &&
    /[1-9]/.test(sample.usd) && typeof sample.as_of === 'string' && Number.isFinite(Date.parse(sample.as_of))
}

export function readSignals(): SignalEntry[] {
  const entries = JSON.parse(localStorage.getItem(journalKey) ?? '[]') as SignalEntry[]
  if (!Array.isArray(entries) || entries.length > maxEntries || entries.some(entry => !entry ||
    typeof entry.id !== 'string' || typeof entry.model !== 'string' || typeof entry.coin_id !== 'string' || !idPattern.test(entry.coin_id) ||
    typeof entry.completed_at !== 'string' || !Number.isFinite(Date.parse(entry.completed_at)) || !['sell', 'add', 'hold'].includes(entry.action) ||
    !Array.isArray(entry.daily) || entry.daily.length > 94 || !entry.daily.every(validSample) ||
    [entry.start, entry.at30, entry.at90].some(sample => sample !== undefined && !validSample(sample))))
    throw new Error('Не удалось прочитать локальный журнал сигналов.')
  return entries
}

async function changeSignals(change: (entries: SignalEntry[]) => SignalEntry[]) {
  function write() {
    const previous = readSignals()
    const next = change(previous)
    if (next !== previous) localStorage.setItem(journalKey, JSON.stringify(next))
    return next
  }
  return navigator.locks ? navigator.locks.request(journalKey, write) : write()
}

export function recordSignals(signals: Signal[]) {
  return changeSignals(entries => {
    const knownIds = new Set(entries.map(entry => entry.id))
    const additions = signals.filter(signal => {
      if (knownIds.has(signal.id)) return false
      knownIds.add(signal.id)
      return true
    })
    return additions.length === 0 ? entries : [...entries, ...additions.map(signal => ({ ...signal, daily: [] }))]
      .sort((a, b) => Date.parse(b.completed_at) - Date.parse(a.completed_at)).slice(0, maxEntries)
  })
}

function closeAt(entry: SignalEntry, days: 30 | 90) { return Date.parse(entry.completed_at) + days * day }

export function wantsQuote(entry: SignalEntry, now: number) {
  if (!entry.start) {
    const age = now - Date.parse(entry.completed_at)
    return age >= -entrySkew && age <= entryWindow
  }
  return !entry.at90 && now <= closeAt(entry, 90) + closeWindow
}

export function observeQuote(entry: SignalEntry, quote: UsdQuote, now: number): SignalEntry {
  if (quote?.id !== entry.coin_id || !validSample(quote)) return entry
  const at = Date.parse(quote.as_of)
  if (at > now || now - at > maxQuoteAge) return entry
  const sample = { usd: quote.usd, as_of: quote.as_of }
  const age = at - Date.parse(entry.completed_at)
  if (!entry.start) return age >= -entrySkew && age <= entryWindow && wantsQuote(entry, now) ? { ...entry, start: sample } : entry
  if (entry.at90 || at <= Date.parse(entry.start.as_of) || age > 90 * day + closeWindow) return entry
  const at30 = entry.at30 ?? (age >= 30 * day && age <= 30 * day + closeWindow ? sample : undefined)
  const at90 = age >= 90 * day && age <= 90 * day + closeWindow ? sample : undefined
  const daily = entry.daily.some(point => Math.floor(Date.parse(point.as_of) / day) === Math.floor(at / day))
    ? entry.daily : [...entry.daily, sample]
  return at30 !== entry.at30 || at90 || daily !== entry.daily ? { ...entry, daily, at30, at90 } : entry
}

export async function sampleSignals(entries: SignalEntry[], signal: AbortSignal) {
  const ids = entries.filter(entry => wantsQuote(entry, Date.now())).map(entry => entry.coin_id)
  const batches = quoteBatches(ids)
  for (const batch of batches) {
    const quotes = await request<UsdQuote[]>(`/public/market/quotes?ids=${batch.join(',')}`, { signal })
    if (signal.aborted) return readSignals()
    const now = Date.now()
    const byId = new Map(quotes.map(quote => [quote.id, quote]))
    await changeSignals(previous => {
      if (signal.aborted) return previous
      const updated = previous.map(entry => {
        const quote = byId.get(entry.coin_id)
        return quote ? observeQuote(entry, quote, now) : entry
      })
      return updated.some((entry, index) => entry !== previous[index]) ? updated : previous
    })
  }
  return readSignals()
}

// Exact USD ratios. Both alternatives start at one unit of asset plus an equal amount of cash.
// One simulated rebalance at 0.10% of the traded amount, no interest or closing trade.
const basis = 1000n
function units({ usd }: Sample) {
  const [whole, fraction = ''] = usd.split('.')
  return BigInt(whole + fraction.padEnd(96, '0'))
}

function percentRatio(numerator: bigint, denominator: bigint) {
  const negative = numerator < 0n
  const magnitude = (negative ? -numerator : numerator) * 10_000n
  let value = magnitude / denominator
  const remainder = magnitude % denominator * 2n
  if (remainder > denominator || remainder === denominator && value % 2n !== 0n) value++
  return `${negative && value !== 0n ? '-' : ''}${value / 100n}.${(value % 100n).toString().padStart(2, '0')}`
}

export function scoreSignal(entry: SignalEntry, days: 30 | 90, now: number) {
  const target = closeAt(entry, days)
  const end = days === 30 ? entry.at30 : entry.at90
  if (!entry.start || !end) return { status: now > (entry.start ? target + closeWindow : Date.parse(entry.completed_at) + entryWindow)
    ? 'missing' as const : 'pending' as const }
  const start = units(entry.start)
  const base = 2n * start * basis
  const hold = (price: bigint) => (price + start) * basis
  const simulated = (price: bigint) => entry.action === 'sell' ? (2n * basis - 1n) * start
    : entry.action === 'add' ? (2n * basis - 1n) * price : hold(price)
  const last = units(end)
  const stamps = new Map<number, Sample>()
  const lastTime = Date.parse(end.as_of)
  for (const sample of [...entry.daily, entry.at30, end]) {
    if (!sample) continue
    const stamp = Date.parse(sample.as_of)
    if (stamp <= lastTime) stamps.set(stamp, sample)
  }
  const points = [...stamps.keys()].sort((a, b) => a - b).map(stamp => units(stamps.get(stamp)!))
  function drawdown(value: (price: bigint) => bigint) {
    let peak = base
    let drop = 0n
    let from = base
    for (const price of [start, ...points]) {
      const current = value(price)
      if (current > peak) peak = current
      if ((peak - current) * from > drop * peak) { drop = peak - current; from = peak }
    }
    return percentRatio(drop, from)
  }
  return {
    status: 'ready' as const, end,
    hold: percentRatio(hold(last) - base, base), strategy: percentRatio(simulated(last) - base, base),
    difference: percentRatio(simulated(last) - hold(last), base),
    drawdown: points.length > 1 ? drawdown(simulated) : null, holdDrawdown: points.length > 1 ? drawdown(hold) : null,
  }
}
