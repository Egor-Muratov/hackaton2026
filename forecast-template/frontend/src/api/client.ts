// Единая точка доступа к API. Компоненты не знают, live это или mock.
import { http } from './http';
import { mockApi } from './mock';
import type {
  CreateForecastRequest, ForecastAccepted, ForecastRun, Health, ImportAccepted, ImportDatasetRequest,
  Job, Meta, SeriesForecast, SeriesQuery,
} from './types';

export type ApiMode = 'live' | 'mock';
export interface ModeInfo { mode: ApiMode; reason: string }

const liveApi = {
  getHealth: () => http<Health>('/api/health', { timeoutMs: 3000 }),
  getMeta: (datasetId?: string) =>
    http<Meta>('/api/meta' + (datasetId ? `?datasetId=${encodeURIComponent(datasetId)}` : '')),
  importDataset: (req: ImportDatasetRequest) =>
    http<ImportAccepted>('/api/datasets/import', { method: 'POST', body: JSON.stringify(req) }),
  getJob: (jobId: string) => http<Job>(`/api/jobs/${encodeURIComponent(jobId)}`),
  createForecast: (req: CreateForecastRequest) =>
    http<ForecastAccepted>('/api/forecasts', { method: 'POST', body: JSON.stringify(req) }),
  getForecastRun: (runId: string) => http<ForecastRun>(`/api/forecasts/${encodeURIComponent(runId)}`),
  getLatestForecastSeries: (q: SeriesQuery) => {
    const p = new URLSearchParams({ store: q.store, family: q.family });
    if (q.horizon) p.set('horizon', String(q.horizon));
    if (q.historyDays) p.set('historyDays', String(q.historyDays));
    if (q.runId) p.set('runId', q.runId);
    return http<SeriesForecast>(`/api/forecasts/latest/series?${p}`);
  },
};

export type Api = typeof liveApi;

let current: Api = liveApi;
let modeInfo: ModeInfo = { mode: 'live', reason: '' };

/** Определяет режим один раз при старте: VITE_API_MODE=mock или /api/health не дал 200 за 3 с. */
export async function detectMode(): Promise<ModeInfo> {
  if (import.meta.env.VITE_API_MODE === 'mock') {
    modeInfo = { mode: 'mock', reason: 'VITE_API_MODE=mock' };
  } else {
    try {
      await liveApi.getHealth();
      modeInfo = { mode: 'live', reason: '' };
    } catch (e) {
      const why = e instanceof DOMException && e.name === 'AbortError' ? 'таймаут 3 с' : (e as Error).message;
      modeInfo = { mode: 'mock', reason: `/api/health недоступен (${why})` };
    }
  }
  current = modeInfo.mode === 'mock' ? (mockApi as Api) : liveApi;
  return modeInfo;
}

export const getModeInfo = () => modeInfo;

/** Прокси: вызовы уходят в текущую реализацию. */
export const api: Api = new Proxy({} as Api, {
  get: (_t, key: keyof Api) => current[key],
});

/** Серверная выгрузка CSV прогона (все ряды, без агрегата). */
export const exportCsvUrl = (runId: string) => `/api/forecasts/${encodeURIComponent(runId)}/export.csv`;
