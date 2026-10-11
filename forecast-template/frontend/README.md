# Frontend (Vite + React + TS)

Один экран: здоровье API в шапке, фильтры магазин / семейство / горизонт (в URL: `?store=44&family=GROCERY%20I&h=16`),
«Импорт данных» (`store-sales/train.csv`, затем `test.csv` в тот же датасет, опрос `/api/jobs/{id}`),
«Построить прогноз» (`POST /api/forecasts` → опрос `/api/forecasts/{runId}` раз в 2 с), график факт/прогноз
с полосой 80 % (только если `lo/hi` не `null`) и зумом, метрики CV, таблица, CSV ряда (на клиенте) и CSV прогона (`/export.csv`).

```bash
npm install --prefer-offline --no-audit --no-fund
npm run dev           # http://localhost:5173, /api → VITE_PROXY_TARGET (по умолчанию http://localhost:5080)
npm run dev:mock      # то же, но всегда на моках
npm run build         # tsc + vite build → dist/
```

**Мок-режим.** `VITE_API_MODE=mock` или автоматически, если `/api/health` не вернул 200 за 3 с (плашка «ДЕМО-ДАННЫЕ»
в шапке, причина в подсказке). Данные — `public/mock/*.json` по контракту; в моках есть ряды `all/all`, `all/GROCERY I`,
`44/all`, `44/GROCERY I`, `44/BEVERAGES`, `1/PRODUCE` (список в `index.json`), остальные дают 404 с подсказкой.
Пересобрать моки из CSV: `npm run gen:mock`; после первого живого прогона лучше положить туда реальные ответы API (curl).
Режим выбирается один раз при старте в `src/api/client.ts`, компоненты о нём не знают.

**Клиент API.** Пока рукописный: `src/api/types.ts` по `docs/contracts/public-api.yaml`, `src/api/client.ts`
(заголовок `X-Correlation-Id`, ошибки ProblemDetails → `ApiError`). `npm run gen:api` — orval → `src/api/generated/`.

**Docker.** `docker build -t forecast-frontend .` → nginx на `:80` со статикой; `/api` проксирует внешний nginx compose.
