import { CircleHelpIcon, SearchIcon } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { dollars, percent } from '@/lib/api'
import { Badge, Card, CardContent, CardDescription, CardHeader, CardTitle, Input,
  Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from './ui/primitives'

export const portfolioHelpPath = '/help/portfolios'

export function PortfolioHeading({ title, description, source, children }: {
  title: string; description: string; source?: string; children?: ReactNode
}) {
  return <div className="flex flex-wrap items-start justify-between gap-3">
    <div className="min-w-0">
      {source && <Badge variant="outline" className="mb-2">{source}</Badge>}
      <h1 className="break-words text-3xl font-semibold tracking-tight">{title}</h1>
      <p className="mt-1 text-sm text-muted-foreground">{description}</p>
    </div>
    {children}
  </div>
}

export function PortfolioValuation({ total, covered, count, complete, source, action, hint }: {
  total: string | null; covered: number; count: number; complete: boolean; source: ReactNode; action?: ReactNode; hint?: string
}) {
  const label = complete ? 'Общая стоимость' : covered ? 'Оценённая часть портфеля' : 'Стоимость пока неизвестна'
  return <Card className="min-w-0"><CardHeader className="flex flex-wrap items-start justify-between gap-3">
    <div className="grid min-w-0 gap-1">
      <h2 className="break-words text-base font-semibold">{label}: <span data-testid="portfolio-value" title={total ?? undefined}
        className="break-all tabular-nums">{dollars(total)}</span></h2>
      <CardDescription>Оценено позиций: {covered} из {count}. Стоимость в USD.
        {!complete && count > 0 && ' Доли появятся, когда будут оценены все позиции.'}</CardDescription>
      <div className="text-xs text-muted-foreground">{source}</div>
    </div>
    {action}
  </CardHeader>{hint && <CardContent className="text-sm text-muted-foreground">{hint}</CardContent>}</Card>
}

export type PositionRow = {
  key: string; search: string[]; asset: ReactNode; quantity: string; quote: ReactNode; value: string | null;
  share: string | null; origins?: string[]
}

export function PortfolioPositions({ rows, label = 'Сводка остатков' }: { rows: PositionRow[]; label?: string }) {
  const [query, setQuery] = useState('')
  const [unpriced, setUnpriced] = useState(false)
  if (!rows.length) return <p className="text-sm text-muted-foreground">В этом портфеле пока нет позиций.</p>
  const needle = query.trim().normalize().toLowerCase()
  const visible = rows.filter(row => (!unpriced || row.value === null) &&
    (!needle || row.search.some(value => value.normalize().toLowerCase().includes(needle))))
  const origins = rows.some(row => row.origins?.length)
  return <div className="grid min-w-0 gap-3">
    <div className="flex flex-wrap items-center gap-3">
      <label className="flex min-w-0 max-w-full items-center gap-2 text-sm">
        <SearchIcon className="size-4 shrink-0 text-muted-foreground" aria-hidden />
        <Input aria-label="Поиск по позициям" placeholder="Название или тикер" value={query} onChange={event => setQuery(event.target.value)} />
      </label>
      <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={unpriced}
        onChange={event => setUnpriced(event.target.checked)} />Только без оценки</label>
      {(needle || unpriced) && <span className="text-xs text-muted-foreground">Показано: {visible.length} из {rows.length}</span>}
    </div>
    {visible.length ? <Table aria-label={label} className="min-w-[36rem]">
      <TableHeader><TableRow>
        <TableHead>Актив</TableHead><TableHead>Количество</TableHead><TableHead>Цена USD</TableHead>
        <TableHead>Стоимость USD</TableHead><TableHead>Доля</TableHead>{origins && <TableHead>Портфели</TableHead>}
      </TableRow></TableHeader>
      <TableBody>{visible.map(row => <TableRow key={row.key}>
        <TableCell className="whitespace-normal break-words">{row.asset}</TableCell>
        <TableCell className="tabular-nums">{row.quantity}</TableCell>
        <TableCell className="tabular-nums">{row.quote}</TableCell>
        <TableCell className="tabular-nums" title={row.value ?? undefined}>{dollars(row.value)}</TableCell>
        <TableCell className="tabular-nums">{percent(row.share)}</TableCell>
        {origins && <TableCell className="whitespace-normal break-words">{row.origins?.join(', ')}</TableCell>}
      </TableRow>)}</TableBody>
    </Table> : <p className="text-sm text-muted-foreground">Нет позиций по этим фильтрам.</p>}
  </div>
}

export function PortfolioHints({ children }: { children: ReactNode }) {
  return <details className="rounded-lg border bg-card px-4 py-3 text-sm">
    <summary className="cursor-pointer font-medium">Подсказки по портфелю</summary>
    <div className="mt-2 grid gap-2 text-muted-foreground">{children}
      <a className="w-fit underline underline-offset-2" href={portfolioHelpPath}>Все подсказки</a>
    </div>
  </details>
}

const guide = [
  ['Портфели и сводка', 'Откройте один портфель из списка или отметьте несколько своих портфелей для общей оценки. Не складывайте два снимка одних и тех же средств.'],
  ['Импорт и замена', 'Выберите CSV с остатками либо ZIP с обзором. Перед подтверждением проверьте, создаётся новый портфель или заменяется существующий. Замена не прибавляет остатки к старым.'],
  ['Позиции и котировки', 'Ищите по названию или тикеру. Фильтр «Только без оценки» меняет таблицу, а не итог. Если монета не найдена, выберите её в каталоге по названию и тикеру. При неполной оценке доли скрыты.'],
  ['Рыночные инструменты', 'CoinGecko даёт цены для файловых портфелей. В мониторинге можно посмотреть HTTP-коды и результаты запросов. Недельная история CoinMarketCap не заменяет текущие цены.'],
  ['Хранение', 'Имя профиля, импортированные остатки и сопоставления монет остаются в браузере на этом адресе. Для переноса сохраните исходные выгрузки.'],
]

export function PortfolioHelp() {
  return <div className="grid gap-5">
    <PortfolioHeading title="Подсказки" description="Как выбрать портфель, проверить оценку и работать с источниками." />
    <div className="grid gap-4 sm:grid-cols-2">{guide.map(([title, text]) => <Card key={title}>
      <CardHeader><CardTitle className="flex items-center gap-2"><CircleHelpIcon className="size-4 shrink-0" aria-hidden />{title}</CardTitle></CardHeader>
      <CardContent className="text-sm text-muted-foreground">{text}</CardContent>
    </Card>)}</div>
  </div>
}
