# Навигатор по задачам

Команды — в [README](../../README.md), правила работы — в [AGENTS.md](../../AGENTS.md).

| Задача или симптом | Где смотреть и что сохранить | Ближайшая проверка |
| --- | --- | --- |
| `local.cmd` не находит Node.js или pnpm | [Локальный запуск](../../scripts/local.ps1) ищет инструменты также в основном Git-репозитории. `-NoNode` раздаёт готовый `frontend/dist` и не пытается пересобрать его. | `.\local.cmd test -NoNode` без Node.js в `PATH` |
| Профиль, переключение портфелей или подсказки | [Навигация](../../frontend/src/main.tsx), [компоненты профиля](../../frontend/src/components/profile.tsx), [инструменты](../../frontend/src/components/portfolio-tools.tsx). [Данные профиля](../portfolio-profile.md) остаются в браузере. | [Профиль и автономный импорт](../../frontend/tests/portfolio-profile.spec.ts) |
| Импорт не читает файл или заменяет не тот снимок | [Разбор CSV и книга](../../frontend/src/lib/csv-portfolios.ts), [чтение ZIP](../../frontend/src/lib/portfolio-archive.ts). Замена обновляет снимок полностью, исходный файл не меняется. | [Фикстуры импорта](../../frontend/tests/csv-portfolios.spec.ts) |
| Нет котировки или неверная сводка | [CoinGeckoMarket](../../src/App.Infrastructure/CoinGeckoMarket.cs) получает и кэширует котировки, [оценка в браузере](../../frontend/src/lib/portfolio-valuation.ts) не включает старые цены. [Правила оценки](../live-valuation.md). | [Сервер](../../tests/App.Tests/CoinGeckoMarketChecks.cs), [интерфейс](../../frontend/tests/portfolio-prices.spec.ts) |
| Не видны запрос, ответ или код HTTP | [Рыночный API](../../src/App.Api/ApiEndpoints.cs) объединяет журналы. [CoinMarketCapHistory](../../src/App.Infrastructure/CoinMarketCapHistory.cs) сохраняет краткий ответ и лимит в SQLite; CoinGecko ведёт журнал в памяти. Просмотр кэша и журнала не вызывает провайдеров. [Контракт](../coinmarketcap-history.md). | [CoinMarketCap](../../tests/App.Tests/CoinMarketCapHistoryChecks.cs), [Playwright](../../frontend/tests/cmc-monitor.spec.ts) |
| Не открывается старый файл SQLite | [Миграции](../../src/App.Infrastructure/Migrations) не должны удалять предыдущие записи и менять существующие ID. Учебные API и worker сняты, но прежние таблицы нужно сохранять. | [SQLite и восстановление](../../tests/App.Tests/SqliteChecks.cs), [старые строки журнала](../../tests/App.Tests/Program.cs) |
| Ошибка в офлайн-отчёте | [Input](../../src/DotnetCrptPortfel/Input.cs) проверяет TSV; [Portfolio](../../src/DotnetCrptPortfel/Portfolio.cs) выбирает котировку; [FixedPoint](../../src/DotnetCrptPortfel/FixedPoint.cs) задаёт масштаб. | [CLI-тесты](../../tests/DotnetCrptPortfel.Tests/Program.cs) и [примеры TSV](../../examples) |

UI-проверки используют локальный API в Development и отдельную SQLite. Развёртывание двух сервисов описано в [compose.yaml](../../compose.yaml). Прежние сценарии анализа сохранены только для проверок контрактов и совместимости хранения; см. [историческое ревью](../review-tz-1.1.md).
