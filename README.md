# dotnetCrptPortfel

Веб-приложение для ведения и оценки криптопортфелей. ASP.NET Core API работает на .NET 11 с Runtime Async V2, интерфейс — React, TypeScript, Vite и shadcn/ui. Остатки портфелей хранятся в браузере; SQLite хранит кэш истории CoinMarketCap и журнал обращений к нему.

Для работы с кодом: [навигатор по задачам и тестам](docs/agents/README.md) · [правила для агентов](AGENTS.md).

## Запуск сайта

### Локально на Windows

Из корня проекта с SDK .NET `11.0.100`, Node.js `24.22.3` и pnpm `11.25.0`:

```powershell
.\local.cmd
.\local.cmd test
.\local.cmd stop
```

Запуск открывает [127.0.0.1:5080](http://127.0.0.1:5080). Docker не нужен. Скрипт использует SQLite и ищет переносимые `node.exe` и `pnpm.cmd` в `.portfel/tools/` рабочего каталога и основного Git-репозитория. Добавьте `-NoBrowser`, чтобы не открывать браузер. Тесты используют отдельный экземпляр (порт `5081` по умолчанию), отдельный файл БД и Chromium; установленный Edge можно выбрать через `PORTFEL_E2E_CHANNEL=msedge`. Другой порт задаётся через `-Port`.

**Запуск без Node.js:** положите готовый интерфейс в `frontend/dist` и выполните `.\local.cmd -NoNode`. ASP.NET Core раздаёт эти файлы; Node.js и pnpm на машине запуска не нужны, нужен SDK .NET. Сборка React-интерфейса по-прежнему требует Node.js: `pnpm --dir frontend install --frozen-lockfile`, затем `pnpm --dir frontend build`. После изменения интерфейса соберите `frontend/dist` заново. `.\local.cmd test -NoNode` проверяет .NET и раздачу готовой страницы, но не запускает Playwright.

На главной открыт **локальный профиль** со своими портфелями из CSV/ZIP. Переключатель в шапке открывает отдельный портфель или сводку по выбранным. Есть поиск позиций, фильтр «Только без оценки» и подсказки. [Профиль](docs/portfolio-profile.md) · [Импорт](docs/csv-portfolios.md) · [Оценка](docs/live-valuation.md).

Раздел **Мониторинг запросов** открывает общий журнал CoinGecko и CoinMarketCap: HTTP-коды источника, результат и краткий ответ. Полный график можно открыть из кэша, не делая внешний запрос. [Контракт и лимиты](docs/coinmarketcap-history.md). В окне `local.cmd` видны начало, результат, кэш и лимиты обращений; HTML, JSON и данные портфеля не выводятся. Файлы логов остаются в `.portfel/local/`.

### Docker Compose

```powershell
Copy-Item .env.example .env
docker compose up --build --remove-orphans
```

Откройте [сайт](http://127.0.0.1:5080) и нажмите **Открыть свои портфели**. Compose запускает frontend и API. `--remove-orphans` останавливает оставшийся от старой версии worker, не удаляя том с БД. Для рыночных инструментов на локальном хосте оставьте `PORTFEL_ENVIRONMENT=Development`.

Для ручного запуска соберите `pnpm --dir frontend install --frozen-lockfile` и `pnpm --dir frontend build`. Из корня проекта:

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
$env:Storage__DatabasePath = Join-Path $PWD '.portfel/portfel.db'
$env:Frontend__Directory = Join-Path $PWD 'frontend/dist'
$env:Security__KeyDirectory = Join-Path $PWD '.portfel/keys'
dotnet run --project src/App.Api --urls http://127.0.0.1:5080
```

EF Core применяет миграции при запуске. Прежние таблицы и записи не удаляются. В Compose API использует `/data/portfel.db` в томе `data`; сохраняйте имя проекта и не удаляйте том при обновлении. `GET /api/v1/health/ready` проверяет БД, контракт доступен на `/openapi/v1.json`. Рыночные маршруты включены только в Development, их не следует публиковать наружу.

### Статус реализации

- Используются импорт остатков и публичные рыночные данные. Вход в аккаунт CoinMarketCap, чтение личных позиций через сервис и AI-аналитика не подключены.
- Учебный интерфейс, его API и worker больше не входят в запуск. Тестовые сценарии и миграции сохранены для регрессий SQLite и старых данных. [История решений](docs/review-tz-1.1.md).
- Интерфейс использует нейтральный preset `nova` с Radix, созданный `shadcn@4.23.0 init --preset nova --base radix --template vite --no-monorepo`.
- [API](src/App.Api), [миграции и рыночные адаптеры](src/App.Infrastructure), [интерфейс](frontend), [точные расчёты сводки](frontend/src/lib/portfolio-valuation.ts).

### Проверки веб-приложения

```powershell
dotnet restore dotnetCrptPortfel.slnx --locked-mode
dotnet build dotnetCrptPortfel.slnx --no-restore -warnaserror
dotnet run --project tests/App.Tests --no-build
pnpm --dir frontend install --frozen-lockfile
pnpm --dir frontend build
pnpm --dir frontend exec playwright install chromium
pnpm --dir frontend test:e2e
```

Тесты хранилища используют отдельные временные файлы SQLite. UI-тесты требуют запущенный экземпляр Development с отдельными данными. По умолчанию тестируют `http://127.0.0.1:5080`; другой адрес можно передать через `PORTFEL_E2E_URL`, установленный браузер — через `PORTFEL_E2E_CHANNEL` (`msedge` / `chrome`).

## Режим CLI

CLI использует стандартную библиотеку .NET. Команда `value` оценивает криптоактивы по TSV-файлам офлайн; `cmc-chart` загружает историю CoinMarketCap.

## Запуск

Выполняй команды из корня репозитория. Для сборки и запуска нужен SDK .NET 11.

```powershell
dotnet run --project src/DotnetCrptPortfel -- --help
dotnet run --project src/DotnetCrptPortfel -- value examples/holdings.tsv examples/quotes.tsv --currency USD --as-of 1700000900 --max-age 300
```

Пример оценивает `0.125 BTC` в `10000.0154320975 USD` и `1.250000000000000001 ETH` в `2500.000000000000002 USD`. Итог — `12500.015432097500002 USD`. Для каждой позиции в отчёте указаны количество, статус оценки, цена, время и источник выбранной котировки, когда она есть, и стоимость, когда её можно вычислить.

`--currency`, `--as-of` и `--max-age` обязательны. Их можно указать в любом порядке после путей. `--as-of` задаёт момент оценки в секундах Unix, `--max-age` — допустимый возраст котировки в секундах. Оба значения принимают целые числа от 0 до 18 446 744 073 709 551 615.

Для каждой позиции выбирается самая свежая котировка в указанной валюте с временем не позже момента оценки. Котировка актуальна, если её возраст не превышает `--max-age`. Отчёт различает отсутствие подходящей котировки и устаревшую котировку. В обоих случаях он показывает промежуточный итог по оценённым позициям. При замене `--max-age 300` на `--max-age 200` в примере котировка BTC устаревает, а итог по оценённой позиции ETH составляет `2500.000000000000002 USD`.

| Код выхода | Результат |
| --- | --- |
| `0` | Полная оценка, загрузка графика или вывод справки |
| `1` | Ошибка аргументов, входных данных, чтения, расчёта или загрузки графика |
| `2` | Частичная оценка из-за отсутствующих или устаревших котировок |

## Входные файлы

Скопируй [пример позиций](examples/holdings.tsv) и [пример котировок](examples/quotes.tsv), чтобы подготовить свои данные. Используй UTF-8 с BOM или без него, табуляцию между полями и `LF` или `CRLF` между строками. Первая строка должна содержать заголовок в указанном порядке. Пустые строки после заголовка пропускаются. Допускается файл только с заголовком. Поля записываются без экранирования и кавычек.

| Файл | Поля по порядку |
| --- | --- |
| Позиции | `asset`, `quantity` |
| Котировки | `asset`, `currency`, `price`, `timestamp`, `source` |

- `asset` — идентификатор с учётом регистра из латинских букв, цифр и символов `-_.:`. Для каждого актива укажи одну строку в файле позиций.
- `quantity` и `price` — положительные десятичные числа с точкой и без знака. Количество допускает до 18 знаков после точки, цена — до 8. Когда используется точка, должны быть цифры с обеих сторон.
- `currency` — от 2 до 12 заглавных латинских букв и цифр, с хотя бы одной буквой. Цена выражена в этой валюте за единицу актива.
- `timestamp` — целое число секунд Unix от 0 до 18 446 744 073 709 551 615 в десятичной записи. Сочетание `asset`, `currency` и `timestamp` должно быть уникальным.
- `source` — подпись источника котировки без управляющих символов, с хотя бы одним непробельным символом.

При оценке в другой валюте передай котировки в этой валюте. Количество и цена хранятся как `UInt128` с фиксированным масштабом, чтобы точно вычислять произведения и суммы. Результат содержит до 26 дробных знаков без округления. Максимальная стоимость позиции и итоговая стоимость ограничены диапазоном `UInt128` при этом масштабе, примерно `3.4 × 10¹²` единиц валюты. При переполнении программа сообщает об ошибке. Входные файлы используются только для чтения.

## История CoinMarketCap

```powershell
dotnet run --project src/DotnetCrptPortfel -- cmc-chart dash
```

Команда извлекает `platformInfo` со страницы монеты, использует `cryptocurrencyId` (для Dash — `131`, не `161`) и возвращает JSON с `platformInfo` и `chart.data.points`. График недельный, в USD, за всю доступную историю. CLI не использует серверный кэш и предназначена для разовых проверок; для повторных запросов используй [внутренний API](docs/coinmarketcap-history.md).

## Код и проверки

- [`Program.cs`](src/DotnetCrptPortfel/Program.cs) разбирает аргументы и выводит отчёт.
- [`CoinMarketCapChart.cs`](src/Shared/CoinMarketCapChart.cs) загружает `platformInfo` и график CoinMarketCap; [`CoinMarketCapHistory.cs`](src/App.Infrastructure/CoinMarketCapHistory.cs) ведёт серверный кэш, лимиты и журнал.
- [`Input.cs`](src/DotnetCrptPortfel/Input.cs) читает и проверяет TSV.
- [`Portfolio.cs`](src/DotnetCrptPortfel/Portfolio.cs) выбирает котировки и рассчитывает стоимость.
- [`FixedPoint.cs`](src/DotnetCrptPortfel/FixedPoint.cs) преобразует точные десятичные числа.
- [`DotnetCrptPortfel.Tests`](tests/DotnetCrptPortfel.Tests) запускает CLI на синтетических данных и проверяет отчёты и ошибки.

```powershell
dotnet build dotnetCrptPortfel.slnx -warnaserror
dotnet run --project tests/DotnetCrptPortfel.Tests
```
