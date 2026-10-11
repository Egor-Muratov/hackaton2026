// Генерирует public/mock/*.json по контракту public-api.yaml из сырых CSV store-sales.
// Запуск: node scripts/make-mock.mjs ../../data/store-sales
// После первого живого прогона лучше заменить на реальные ответы API (curl > public/mock/...).
import fs from 'node:fs';
import path from 'node:path';
import readline from 'node:readline';

const dataDir = path.resolve(process.argv[2] ?? '../../data/store-sales');
const outDir = path.resolve(path.dirname(new URL(import.meta.url).pathname), '../public/mock');
fs.mkdirSync(outDir, { recursive: true });

const DATASET_ID = '3f1c2a9e-7b1d-4c55-9a51-2b0c1d7e8f00';
const RUN_ID = '9b2e4d17-5c3a-4f6e-8d21-7a0b9c1e2f34';
const MODEL_VERSION = 'mock-lgbm-2017-08-15';
const HORIZON = 16;
const METRICS = { rmsle: 0.401, mae: 71.8, baselineRmsle: 0.523 };
const FINISHED_AT = '2017-08-16T06:12:41Z';

// Ряды для мока: [store, family, withInterval]
const PICKS = [
  ['all', 'all', true],
  ['all', 'GROCERY I', false],
  ['44', 'all', false],
  ['44', 'GROCERY I', true],
  ['44', 'BEVERAGES', true],
  ['1', 'PRODUCE', true],
];
const want = new Map(PICKS.map(([s, f]) => [`${s}|${f}`, new Map()]));

export const slug = (store, family) => `forecast_${store}_${family.replace(/[^A-Za-z0-9]+/g, '_')}.json`;

// Простейший CSV-сплит с кавычками (в store-sales есть "LIQUOR,WINE,BEER").
function splitCsv(line) {
  const out = [];
  let cur = '', q = false;
  for (const ch of line) {
    if (ch === '"') q = !q;
    else if (ch === ',' && !q) { out.push(cur); cur = ''; }
    else cur += ch;
  }
  out.push(cur);
  return out;
}

function add(key, date, sales, promo) {
  const m = want.get(key);
  if (!m) return;
  const p = m.get(date) ?? { sales: 0, promo: 0 };
  p.sales += sales;
  p.promo += promo;
  m.set(date, p);
}

const families = new Set();
const rl = readline.createInterface({ input: fs.createReadStream(path.join(dataDir, 'train.csv')) });
let header = true;
for await (const line of rl) {
  if (header) { header = false; continue; }
  const [, date, store, family, sales, promo] = splitCsv(line);
  families.add(family);
  const s = Number(sales), pr = Number(promo);
  for (const k of [`all|all`, `all|${family}`, `${store}|all`, `${store}|${family}`]) add(k, date, s, pr);
}

const stores = fs.readFileSync(path.join(dataDir, 'stores.csv'), 'utf8').trim().split('\n').slice(1)
  .map((l) => { const [n, city, state, type, cluster] = l.split(','); return { storeNbr: +n, city, state, type, cluster: +cluster }; })
  .sort((a, b) => a.storeNbr - b.storeNbr);

const addDays = (iso, n) => { const d = new Date(iso + 'T00:00:00Z'); d.setUTCDate(d.getUTCDate() + n); return d.toISOString().slice(0, 10); };
const r2 = (x) => Math.round(x * 100) / 100;

let historyFrom = null, historyTo = null;
for (const [store, family, withInterval] of PICKS) {
  const m = want.get(`${store}|${family}`);
  const history = [...m.entries()].sort(([a], [b]) => a.localeCompare(b))
    .map(([date, p]) => ({ date, sales: r2(p.sales), onpromotion: p.promo }));
  historyFrom = history[0].date; historyTo = history.at(-1).date;
  // Прогноз: среднее того же дня недели за последние 8 недель (сезонный naive), интервал расширяется с шагом.
  const last = history.slice(-56);
  const forecast = [];
  for (let h = 1; h <= HORIZON; h++) {
    const date = addDays(historyTo, h);
    const wd = new Date(date + 'T00:00:00Z').getUTCDay();
    const same = last.filter((p) => new Date(p.date + 'T00:00:00Z').getUTCDay() === wd);
    const yhat = r2(same.reduce((a, p) => a + p.sales, 0) / Math.max(1, same.length));
    const w = 0.1 + 0.008 * h;
    forecast.push({ date, yhat, lo: withInterval ? r2(yhat * (1 - w)) : null, hi: withInterval ? r2(yhat * (1 + w * 1.2)) : null });
  }
  const body = {
    runId: RUN_ID, datasetId: DATASET_ID, modelVersion: MODEL_VERSION, store, family,
    cutoffDate: historyTo, intervalLevel: 0.8, history, forecast, metrics: METRICS, generatedAt: FINISHED_AT,
  };
  fs.writeFileSync(path.join(outDir, slug(store, family)), JSON.stringify(body));
}

const write = (name, obj) => fs.writeFileSync(path.join(outDir, name), JSON.stringify(obj, null, 2) + '\n');
write('health.json', { status: 'Healthy', checks: { db: 'Healthy', ml: 'Healthy' }, modelVersion: MODEL_VERSION });
write('meta.json', {
  datasetId: DATASET_ID, latestDatasetId: DATASET_ID, stores, families: [...families].sort(),
  seriesCount: stores.length * families.size, historyFrom, historyTo, maxHorizon: 16, intervalLevel: 0.8,
  latestRunId: RUN_ID, modelVersion: MODEL_VERSION,
});
write('run.json', {
  runId: RUN_ID, datasetId: DATASET_ID, status: 'done', horizon: HORIZON, cutoffDate: historyTo, modelVersion: MODEL_VERSION,
  intervalLevel: 0.8, metrics: METRICS, error: null, createdAt: '2017-08-16T06:10:02Z', startedAt: '2017-08-16T06:10:03Z', finishedAt: FINISHED_AT,
});
write('job.json', {
  jobId: '5d0c8a3b-1e2f-4a7b-9c6d-0e1f2a3b4c5d', kind: 'import', status: 'done', progress: 1, message: 'Готово',
  datasetId: DATASET_ID, rows: 3000888, seriesCount: 1782, dateFrom: historyFrom, dateTo: historyTo,
  warnings: [], error: null, createdAt: '2017-08-16T06:00:00Z', startedAt: '2017-08-16T06:00:01Z', finishedAt: '2017-08-16T06:01:30Z',
});
write('index.json', { series: PICKS.map(([store, family]) => ({ store, family, file: slug(store, family) })) });
console.log('mock written to', outDir, fs.readdirSync(outDir));
