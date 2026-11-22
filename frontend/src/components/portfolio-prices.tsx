import { useEffect, useMemo, useState } from 'react'
import { date, decimal, dollars, percent, request } from '@/lib/api'
import type { SummaryRow } from '@/lib/csv-portfolios'
import { coinSuggestions, makeCoinIndex, quoteBatches, readMappings, resolveCoin, saveMapping, valuePortfolio,
  type Mapping, type MarketCoin, type UsdQuote } from '@/lib/portfolio-valuation'
import { Button, Input, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from './ui/primitives'
import { ExternalLink, Warning } from './shared'

const endpoint = '/public/market'
const zone = Intl.DateTimeFormat().resolvedOptions().timeZone
const marketLinks = 'https://www.coingecko.com/en/coins/'

function CoinPicker({ row, coin, coins, onPick }: {
  row: SummaryRow; coin?: MarketCoin; coins: MarketCoin[]; onPick: (id: string) => Promise<void>
}) {
  const [open, setOpen] = useState(false)
  const [text, setText] = useState(row.name)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const results = open ? coinSuggestions(coins, text) : []
  return <div className="mt-1 grid gap-1 text-xs text-muted-foreground">
    <div className="flex flex-wrap items-center gap-x-2">
      {coin && <ExternalLink href={`${marketLinks}${encodeURIComponent(coin.id)}`}>{coin.symbol.toUpperCase()} · {coin.id}</ExternalLink>}
      {coins.length > 0 && <button type="button" className="underline underline-offset-2" aria-expanded={open}
        onClick={() => { if (!open) setText(coin?.id ?? row.name); setOpen(!open) }}>
        {open ? 'Закрыть выбор' : coin ? 'Изменить монету' : 'Выбрать монету'}
      </button>}
    </div>
    {open && <div className="grid min-w-48 gap-1 rounded-md border p-2">
      <Input aria-label={`Поиск CoinGecko для ${row.name}`} value={text} onChange={event => setText(event.target.value)} maxLength={200} />
      {results.length ? results.map(item => <Button key={item.id} className="h-auto justify-start whitespace-normal text-left"
        size="sm" variant="ghost" disabled={busy} onClick={async () => {
          setBusy(true)
          setError('')
          try { await onPick(item.id); setOpen(false) }
          catch { setError('Не удалось сохранить сопоставление в браузере.') }
          finally { setBusy(false) }
        }}>{item.name} ({item.symbol.toUpperCase()}) · {item.id}</Button>) :
        <span>{text.trim() ? 'Нет совпадений. Попробуйте тикер или CoinGecko ID.' : 'Введите название, тикер или CoinGecko ID.'}</span>}
      {error && <span role="alert">{error}</span>}
    </div>}
  </div>
}

export function PortfolioPrices({ summary }: { summary: SummaryRow[] }) {
  const [initial] = useState(() => {
    try { return { mappings: readMappings(), error: '' } }
    catch (failure) { return { mappings: {} as Mapping, error: (failure as Error).message } }
  })
  const [mappings, setMappings] = useState(initial.mappings)
  const [catalog, setCatalog] = useState<MarketCoin[] | null>(null)
  const [catalogError, setCatalogError] = useState('')
  const [catalogRevision, setCatalogRevision] = useState(0)
  const [quotes, setQuotes] = useState<ReadonlyMap<string, UsdQuote>>(new Map())
  const [quoteError, setQuoteError] = useState('')
  const [refreshing, setRefreshing] = useState(false)
  const [quoteRevision, setQuoteRevision] = useState(0)
  const [now, setNow] = useState(Date.now)
  const index = useMemo(() => makeCoinIndex(catalog ?? []), [catalog])
  const resolved = summary.map(row => ({ row, coin: resolveCoin(row, index, mappings) }))
  const ids = [...new Set(resolved.flatMap(({ row, coin }) => coin && row.quantity !== '0' ? [coin.id] : []))].sort().join(',')
  const catalogLoading = catalog === null && !catalogError

  useEffect(() => {
    const controller = new AbortController()
    setCatalogError('')
    request<MarketCoin[]>(`${endpoint}/coins`, { signal: controller.signal }).then(
      data => { if (!controller.signal.aborted) setCatalog(data) },
      (failure: Error) => { if (!controller.signal.aborted) setCatalogError(failure.message) },
    )
    return () => controller.abort()
  }, [catalogRevision])

  useEffect(() => {
    setQuoteError('')
    if (!ids) { setRefreshing(false); return }
    const controller = new AbortController()
    setRefreshing(true)
    const batches = quoteBatches(ids.split(','))
    async function fetchQuotes() {
      try {
        for (const batch of batches) {
          const result = await request<UsdQuote[]>(`${endpoint}/quotes?ids=${batch.join(',')}`, { signal: controller.signal })
          if (controller.signal.aborted) return
          setQuotes(previous => {
            const next = new Map(previous)
            for (const id of batch) next.delete(id)
            for (const quote of result) next.set(quote.id, quote)
            return next
          })
          setNow(Date.now())
        }
      } catch (failure) {
        if (!controller.signal.aborted) setQuoteError((failure as Error).message)
      } finally { if (!controller.signal.aborted) setRefreshing(false) }
    }
    void fetchQuotes()
    return () => controller.abort()
  }, [ids, quoteRevision])

  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 15_000)
    return () => window.clearInterval(timer)
  }, [])

  const valuation = valuePortfolio(resolved, quotes, now)
  const label = valuation.complete ? 'Общая стоимость' : 'Оценённая часть портфеля'
  return <div className="grid min-w-0 gap-3">
    <div className="flex flex-wrap items-start justify-between gap-3 rounded-md border p-3">
      <div className="min-w-0">
        <h2 className="text-base font-semibold">{label}: <span data-testid="portfolio-value" title={valuation.total ?? undefined}
          className="break-all">{dollars(valuation.total)}</span></h2>
        <p className="text-xs text-muted-foreground">Оценено позиций: {valuation.covered} из {valuation.rows.length}. Стоимость в USD.
          {!valuation.complete && ' Доли появятся, когда будут оценены все позиции.'}</p>
        <div className="mt-1 text-xs text-muted-foreground"><ExternalLink href="https://www.coingecko.com/en/api">Data provided by CoinGecko</ExternalLink></div>
      </div>
      <Button variant="outline" size="sm" disabled={catalogLoading || refreshing || (!ids && !catalogError)}
        onClick={() => catalog === null ? setCatalogRevision(value => value + 1) : setQuoteRevision(value => value + 1)}>
        {catalogLoading || refreshing ? 'Загрузка котировок…' : 'Обновить котировки'}
      </Button>
    </div>
    {catalogError && <Warning title="Каталог CoinGecko недоступен">{catalogError}</Warning>}
    {quoteError && <Warning title="Не удалось обновить котировки">{quoteError}</Warning>}
    {initial.error && <Warning title="Сопоставления недоступны">{initial.error}</Warning>}
    <Table aria-label="Сводка остатков">
      <TableHeader><TableRow>{['Актив', 'Количество', 'Цена USD', 'Стоимость USD', 'Доля', 'Портфели'].map(title =>
        <TableHead key={title}>{title}</TableHead>)}</TableRow></TableHeader>
      <TableBody>{valuation.rows.map(({ row, coin, quote, status, value, share }) => <TableRow key={row.key}>
        <TableCell className="whitespace-normal break-words">{row.name}
          {row.assetId && <div className="text-xs text-muted-foreground">{row.assetId}</div>}
          {status !== 'empty' && <CoinPicker row={row} coin={coin} coins={catalog ?? []}
            onPick={async id => setMappings(await saveMapping(row.mappingKeys, id))} />}
        </TableCell>
        <TableCell className="tabular-nums">{row.quantity}</TableCell>
        <TableCell className="tabular-nums">
          {status === 'ok' && quote ? <div title={quote.usd}>{decimal(quote.usd, 8, 2)}</div> :
            <span className="text-muted-foreground">{status === 'empty' ? '—' : catalogLoading ? 'Загрузка…'
              : status === 'unmapped' ? 'Нет сопоставления' : status === 'stale' ? 'Устарела' : 'Нет котировки'}</span>}
          {quote && Number.isFinite(Date.parse(quote.as_of)) &&
            <time dateTime={quote.as_of} title={quote.as_of} className="text-xs text-muted-foreground">{date(quote.as_of, zone)}</time>}
        </TableCell>
        <TableCell className="tabular-nums" title={value ?? undefined}>{dollars(value)}</TableCell>
        <TableCell className="tabular-nums">{percent(share)}</TableCell>
        <TableCell className="whitespace-normal break-words">{row.portfolios.join(', ')}</TableCell>
      </TableRow>)}</TableBody>
    </Table>
  </div>
}
