// Мок-реализация API на статических JSON из public/mock/. Долгие операции имитируются по времени.
import { ApiError, http, newId } from './http';
import type {
  CreateForecastRequest, ForecastAccepted, ForecastRun, Health, ImportAccepted, ImportDatasetRequest,
  Job, Meta, SeriesForecast, SeriesQuery,
} from './types';

const base = `${import.meta.env.BASE_URL}mock/`;
const file = <T>(name: string) => http<T>(base + name);

export const mockSlug = (store: string, family: string) =>
  `forecast_${store}_${family.replace(/[^A-Za-z0-9]+/g, '_')}.json`;

interface MockIndex { series: { store: string; family: string; file: string }[] }
let indexCache: Promise<MockIndex> | null = null;
export const mockIndex = () => (indexCache ??= file<MockIndex>('index.json'));

const started = new Map<string, { at: number; req: ImportDatasetRequest | CreateForecastRequest }>();
const uuid = newId;
const now = () => new Date().toISOString().replace(/\.\d+Z$/, 'Z');

function phase(id: string): { status: 'queued' | 'running' | 'done'; progress: number } {
  const s = started.get(id);
  const t = s ? (Date.now() - s.at) / 1000 : 999;
  if (t < 1.5) return { status: 'queued', progress: 0 };
  if (t < 6) return { status: 'running', progress: Math.min(0.99, (t - 1.5) / 4.5) };
  return { status: 'done', progress: 1 };
}

const notFound = (detail: string) =>
  new ApiError(404, { type: 'about:blank', title: 'Not Found', status: 404, detail, traceId: 'mock' }, 'mock');

export const mockApi = {
  getHealth: () => file<Health>('health.json'),
  getMeta: () => file<Meta>('meta.json'),

  async importDataset(req: ImportDatasetRequest): Promise<ImportAccepted> {
    const meta = await mockApi.getMeta();
    const jobId = uuid();
    started.set(jobId, { at: Date.now(), req });
    return { jobId, datasetId: req.datasetId ?? meta.latestDatasetId ?? uuid() };
  },

  async getJob(jobId: string): Promise<Job> {
    const tpl = await file<Job>('job.json');
    const s = started.get(jobId);
    if (!s) throw notFound(`job ${jobId}`);
    const req = s.req as ImportDatasetRequest;
    const isTest = req.path.includes('test');
    const { status, progress } = phase(jobId);
    const total = isTest ? 28512 : (tpl.rows ?? 3000888);
    const done = status === 'done';
    return {
      ...tpl,
      jobId,
      status,
      progress,
      message: done ? 'Готово' : status === 'queued' ? 'В очереди' : `COPY ${Math.round(progress * total).toLocaleString('ru')}/${total.toLocaleString('ru')} строк`,
      rows: done ? total : null,
      seriesCount: done ? tpl.seriesCount : null,
      dateFrom: done ? (isTest ? '2017-08-16' : tpl.dateFrom) : null,
      dateTo: done ? (isTest ? '2017-08-31' : tpl.dateTo) : null,
      createdAt: new Date(s.at).toISOString(),
      startedAt: status === 'queued' ? null : new Date(s.at + 1500).toISOString(),
      finishedAt: done ? now() : null,
    };
  },

  async createForecast(req: CreateForecastRequest): Promise<ForecastAccepted> {
    const runId = uuid();
    started.set(runId, { at: Date.now(), req });
    return { runId, status: 'queued' };
  },

  async getForecastRun(runId: string): Promise<ForecastRun> {
    const tpl = await file<ForecastRun>('run.json');
    if (runId === tpl.runId) return tpl;
    const s = started.get(runId);
    if (!s) throw notFound(`run ${runId}`);
    const { status } = phase(runId);
    const done = status === 'done';
    return {
      ...tpl,
      runId,
      status,
      horizon: (s.req as CreateForecastRequest).horizon ?? 16,
      modelVersion: done ? tpl.modelVersion : null,
      metrics: done ? tpl.metrics : null,
      createdAt: new Date(s.at).toISOString(),
      startedAt: status === 'queued' ? null : new Date(s.at + 1500).toISOString(),
      finishedAt: done ? now() : null,
    };
  },

  async getLatestForecastSeries(q: SeriesQuery): Promise<SeriesForecast> {
    const idx = await mockIndex();
    const hit = idx.series.find((s) => s.store === q.store && s.family === q.family);
    if (!hit) {
      const avail = idx.series.map((s) => `${s.store}/${s.family}`).join(', ');
      throw notFound(`В мок-данных нет ряда ${q.store}/${q.family}. Доступны: ${avail}`);
    }
    const data = await file<SeriesForecast>(hit.file);
    return {
      ...data,
      history: q.historyDays ? data.history.slice(-q.historyDays) : data.history,
      forecast: q.horizon ? data.forecast.slice(0, q.horizon) : data.forecast,
    };
  },
};
