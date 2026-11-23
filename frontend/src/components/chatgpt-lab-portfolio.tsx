import { useEffect, useMemo, useState } from 'react'
import { request } from '@/lib/api'
import { readBook, summarizePortfolios } from '@/lib/csv-portfolios'
import { makeCoinIndex, readMappings, resolveCoin, type MarketCoin } from '@/lib/portfolio-valuation'
import { Button } from './ui/primitives'
import { Warning } from './shared'

const maxCoins = 40
const validId = /^[a-z0-9._-]{1,120}$/

export function LabPortfolioPicker({ disabled, submit }: { disabled: boolean; submit: (ids: string[]) => void }) {
  const [local] = useState(() => {
    try { return { portfolios: readBook().portfolios, mappings: readMappings(), error: '' } }
    catch (failure) { return { portfolios: [], mappings: {}, error: (failure as Error).message } }
  })
  const [portfolioId, setPortfolioId] = useState(local.portfolios[0]?.id ?? '')
  const [catalog, setCatalog] = useState<MarketCoin[] | null>(null)
  const [catalogError, setCatalogError] = useState('')
  const [overrides, setOverrides] = useState(new Map<string, boolean>())
  const index = useMemo(() => makeCoinIndex(catalog ?? []), [catalog])
  const rows = useMemo(() => summarizePortfolios(local.portfolios.filter(portfolio => portfolio.id === portfolioId), false)
    .filter(row => row.quantity !== '0'), [local.portfolios, portfolioId])
  const candidates = new Map<string, string>()
  const missing: string[] = []
  for (const row of rows) {
    const coin = resolveCoin(row, index, local.mappings)
    // If CoinGecko is unavailable, explicit and previously confirmed IDs still work. Do not guess names or tickers.
    const saved = local.mappings[row.mappingKeys[0]] ?? (row.assetId?.startsWith('coingecko:') ? row.assetId.slice(10) : '')
    const id = coin?.id ?? (catalogError && validId.test(saved) ? saved : '')
    if (!id) missing.push(row.name)
    else if (!candidates.has(id)) candidates.set(id, coin?.name ?? row.name)
  }
  const resolved = [...candidates]
  const selected = resolved.filter(([id]) => overrides.get(id) ?? (resolved.length <= maxCoins))
  const ready = catalog !== null || !!catalogError

  useEffect(() => {
    if (local.portfolios.length === 0) return
    const controller = new AbortController()
    request<MarketCoin[]>('/public/market/coins', { signal: controller.signal }).then(
      items => { if (!controller.signal.aborted) setCatalog(items) },
      (failure: Error) => { if (!controller.signal.aborted) setCatalogError(failure.message) },
    )
    return () => controller.abort()
  }, [local.portfolios])

  if (local.error) return <Warning title="Портфель недоступен">{local.error}</Warning>
  if (!local.portfolios.length) return <p className="text-sm">Нет сохранённых портфелей. <a className="underline" href="/portfolios">Импортировать CSV или ZIP</a></p>
  return <div className="grid gap-3 text-sm">
    <label className="grid gap-1">Портфель
      <select className="h-9 w-full min-w-0 rounded-md border bg-background px-2" value={portfolioId} disabled={disabled}
        onChange={event => { setPortfolioId(event.target.value); setOverrides(new Map()) }}>
        {local.portfolios.map(portfolio => <option key={portfolio.id} value={portfolio.id}>{portfolio.name}</option>)}
      </select>
    </label>
    {!ready && <p role="status">Проверяем сопоставления CoinGecko…</p>}
    {catalogError && <Warning title="Каталог не загрузился">Доступны только явно заданные или ранее сохранённые CoinGecko ID.</Warning>}
    {ready && <>
      {missing.length > 0 && <p role="status">Без сопоставления: {missing.length}. Эти позиции не отправятся: {missing.slice(0, 6).join(', ')}
        {missing.length > 6 ? '…' : '.'} <a className="underline" href="/portfolios">Уточнить сопоставления</a></p>}
      {resolved.length === 0 ? <p>Нет ненулевых позиций с однозначным CoinGecko ID.</p> : <>
        <p>Выбрано: {selected.length} из {resolved.length}. До {maxCoins} монет в пакете.
          {resolved.length > maxCoins && ' Отметьте нужные монеты.'}</p>
        <fieldset className="grid max-h-64 gap-2 overflow-auto rounded-md border p-3 sm:grid-cols-2" disabled={disabled}>
          <legend className="px-1 font-medium">Монеты для обзора</legend>
          {resolved.map(([id, name]) => <label key={id} className="flex items-start gap-2 break-all">
            <input type="checkbox" checked={overrides.get(id) ?? (resolved.length <= maxCoins)} onChange={event => {
              const { checked } = event.target
              setOverrides(previous => new Map(previous).set(id, checked))
            }} />
            <span>{name} <span className="text-muted-foreground">· {id}</span></span>
          </label>)}
        </fieldset>
        <p className="text-muted-foreground">Один запрос с веб-поиском на монету. Монеты обрабатываются по очереди; это расходует лимит подписки. Название портфеля и остатки остаются в браузере.</p>
        <div><Button type="button" disabled={disabled || selected.length === 0 || selected.length > maxCoins}
          onClick={() => submit(selected.map(([id]) => id))}>Запустить обзор портфеля ({selected.length})</Button></div>
      </>}
    </>}
  </div>
}
