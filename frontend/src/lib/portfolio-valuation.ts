import type { SummaryRow } from './csv-portfolios'

const maxAgeMs = 15 * 60 * 1000
const clockSkewMs = 60 * 1000
const storageKey = 'portfel.coingecko-mappings.v1'
const idPattern = /^[a-z0-9._-]{1,120}$/
const pricePattern = /^(?:0|[1-9]\d{0,256})(?:\.\d{1,96})?$/
// Verified public listings for full labels with duplicate or differing catalog names.
const canonicalCoins = new Map<string, MarketCoin>([
  ['dash', { id: 'dash', name: 'Dash', symbol: 'dash' }],
  ['iota', { id: 'iota', name: 'IOTA', symbol: 'iota' }],
  ['terra classic', { id: 'terra-luna', name: 'Terra Luna Classic', symbol: 'lunc' }],
])

export type MarketCoin = { id: string; name: string; symbol: string }
export type UsdQuote = { id: string; usd: string; as_of: string }
export type Mapping = Record<string, string>

function nameKey(value: string) {
  return value.normalize().trim().replace(/\s+/g, ' ').toLowerCase()
}

export function makeCoinIndex(coins: MarketCoin[]) {
  const byId = new Map(coins.map(coin => [coin.id, coin]))
  const byName = new Map<string, MarketCoin[]>()
  for (const coin of coins) {
    const key = nameKey(coin.name)
    const matches = byName.get(key)
    if (matches) matches.push(coin)
    else byName.set(key, [coin])
  }
  return { byId, byName }
}

export function resolveCoin(row: SummaryRow, catalog: ReturnType<typeof makeCoinIndex>, mappings: Mapping) {
  const explicit = row.assetId?.startsWith('coingecko:') ? row.assetId.slice(10) : undefined
  const key = nameKey(row.name)
  const matches = !row.assetId ? catalog.byName.get(key) : undefined
  const known = !row.assetId ? canonicalCoins.get(key) : undefined
  const current = known ? catalog.byId.get(known.id) : undefined
  const specific = known && current?.name === known.name && current.symbol === known.symbol ? known.id : undefined
  const automatic = explicit ? catalog.byId.get(explicit)?.id
    : known ? specific : matches?.length === 1 ? matches[0].id : undefined
  const ids = new Set(row.mappingKeys.map(key => Object.hasOwn(mappings, key) ? mappings[key] : automatic))
  const [id] = ids
  return ids.size === 1 && id ? catalog.byId.get(id) : undefined
}

export function coinSuggestions(coins: MarketCoin[], text: string) {
  const query = nameKey(text)
  if (!query) return []
  const exact = [], others = []
  for (const coin of coins) {
    const values = [coin.id, nameKey(coin.name), nameKey(coin.symbol)]
    if (values.includes(query)) exact.push(coin)
    else if (others.length < 12 && values.some(value => value.includes(query))) others.push(coin)
  }
  return [...exact, ...others].slice(0, 12)
}

export function readMappings(): Mapping {
  const raw = localStorage.getItem(storageKey)
  if (raw === null) return {}
  try {
    const parsed: unknown = JSON.parse(raw)
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) throw new Error()
    if (Object.values(parsed).some(id => typeof id !== 'string' || !idPattern.test(id))) throw new Error()
    return parsed as Mapping
  } catch { throw new Error('Не удалось прочитать сохранённые сопоставления CoinGecko.') }
}

export async function saveMapping(keys: string[], id: string): Promise<Mapping> {
  function write() {
    const next = { ...readMappings(), ...Object.fromEntries(keys.map(key => [key, id])) }
    localStorage.setItem(storageKey, JSON.stringify(next))
    return next
  }
  return navigator.locks ? navigator.locks.request(storageKey, write) : write()
}

export function quoteBatches(ids: string[]) {
  const batches: string[][] = []
  let batch: string[] = []
  let size = 0
  for (const id of [...new Set(ids)].sort()) {
    if (batch.length === 100 || size + id.length + 1 > 5000) {
      batches.push(batch)
      batch = []
      size = 0
    }
    if (batch.length) size++
    batch.push(id)
    size += id.length
  }
  if (batch.length) batches.push(batch)
  return batches
}

type Amount = { units: bigint; scale: number }
const zero: Amount = { units: 0n, scale: 0 }

function amount(decimal: string): Amount {
  const [whole, frac = ''] = decimal.split('.')
  return { units: BigInt(whole + frac), scale: frac.length }
}

function scaleBy(power: number) { return 10n ** BigInt(power) }

function add(a: Amount, b: Amount): Amount {
  const scale = Math.max(a.scale, b.scale)
  return { units: a.units * scaleBy(scale - a.scale) + b.units * scaleBy(scale - b.scale), scale }
}

function text({ units, scale }: Amount) {
  if (!scale) return units.toString()
  const digits = units.toString().padStart(scale + 1, '0')
  const frac = digits.slice(-scale).replace(/0+$/, '')
  return digits.slice(0, -scale) + (frac ? `.${frac}` : '')
}

function share(value: Amount, total: Amount) {
  const dividend = value.units * scaleBy(total.scale + 4)
  const divisor = total.units * scaleBy(value.scale)
  const quotient = dividend / divisor
  const twiceRemainder = dividend % divisor * 2n
  return text({ units: quotient + (twiceRemainder > divisor || twiceRemainder === divisor && quotient % 2n === 1n ? 1n : 0n), scale: 2 })
}

export function valuePortfolio(rows: { row: SummaryRow; coin?: MarketCoin }[], quotes: ReadonlyMap<string, UsdQuote>, now: number) {
  let total = zero
  let covered = 0
  const valuations = rows.map(({ row, coin }) => {
    const entry = coin ? quotes.get(coin.id) : undefined
    const age = entry ? now - Date.parse(entry.as_of) : NaN
    const status = row.quantity === '0' ? 'empty' : !coin ? 'unmapped' : !entry || !Number.isFinite(age)
      || !pricePattern.test(entry.usd) || amount(entry.usd).units === 0n ? 'missing' : age < -clockSkewMs || age > maxAgeMs ? 'stale' : 'ok'
    const price = status === 'ok' && entry ? amount(entry.usd) : null
    const quantity = amount(row.quantity)
    const value = status === 'empty' ? zero : price ? { units: quantity.units * price.units, scale: quantity.scale + price.scale } : null
    if (value !== null) { total = add(total, value); covered++ }
    return { row, coin, status, quote: entry, value }
  })
  const complete = covered === rows.length
  return {
    total: total.units > 0n || (complete && rows.length > 0) ? text(total) : null,
    covered,
    complete,
    rows: valuations.map(row => ({ ...row, value: row.value === null ? null : text(row.value),
      share: complete && row.value !== null && total.units > 0n ? share(row.value, total) : null })),
  }
}
