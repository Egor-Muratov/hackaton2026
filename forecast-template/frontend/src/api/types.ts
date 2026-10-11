// Типы по docs/contracts/public-api.yaml v0.1.0 (рукописно; см. npm run gen:api).

export type LocalDate = string; // YYYY-MM-DD
export type Uuid = string;
export type JobStatus = 'queued' | 'running' | 'done' | 'failed';
export type HealthStatus = 'Healthy' | 'Degraded' | 'Unhealthy';

export interface Health {
  status: HealthStatus;
  checks: { db: HealthStatus; ml: HealthStatus };
  modelVersion: string | null;
}

export interface ImportDatasetRequest {
  path: string;
  name?: string | null;
  datasetId?: Uuid | null;
}

export interface ImportAccepted {
  datasetId: Uuid;
  jobId: Uuid;
}

export interface ImportWarning {
  line: number | null;
  message: string;
}

export interface Job {
  jobId: Uuid;
  kind: 'import';
  status: JobStatus;
  progress: number | null;
  message: string | null;
  datasetId: Uuid | null;
  rows: number | null;
  seriesCount: number | null;
  dateFrom: LocalDate | null;
  dateTo: LocalDate | null;
  warnings: ImportWarning[];
  error: string | null;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
}

export interface Store {
  storeNbr: number;
  city: string | null;
  state: string | null;
  type: string | null;
  cluster: number | null;
}

export interface Meta {
  datasetId: Uuid | null;
  latestDatasetId: Uuid | null;
  stores: Store[];
  families: string[];
  seriesCount: number;
  historyFrom: LocalDate | null;
  historyTo: LocalDate | null;
  maxHorizon: 16;
  intervalLevel: 0.8;
  latestRunId: Uuid | null;
  modelVersion: string | null;
}

export interface CreateForecastRequest {
  datasetId?: Uuid | null;
  horizon?: number;
  cutoffDate?: LocalDate | null;
}

export interface ForecastAccepted {
  runId: Uuid;
  status: 'queued';
}

export interface Metrics {
  rmsle: number | null;
  mae: number | null;
  baselineRmsle: number | null;
}

export interface ForecastRun {
  runId: Uuid;
  datasetId: Uuid;
  status: JobStatus;
  horizon: number;
  cutoffDate: LocalDate;
  modelVersion: string | null;
  intervalLevel: 0.8;
  metrics: Metrics | null;
  error: string | null;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
}

export interface HistoryPoint {
  date: LocalDate;
  sales: number;
  onpromotion: number | null;
}

export interface ForecastPoint {
  date: LocalDate;
  yhat: number;
  lo: number | null;
  hi: number | null;
}

export interface SeriesForecast {
  runId: Uuid | null;
  datasetId: Uuid;
  modelVersion: string | null;
  store: string;
  family: string;
  cutoffDate: LocalDate | null;
  intervalLevel: 0.8;
  history: HistoryPoint[];
  forecast: ForecastPoint[];
  metrics: Metrics | null;
  generatedAt: string | null;
}

export interface SeriesQuery {
  store: string; // 'all' | номер
  family: string; // 'all' | имя
  horizon?: number;
  historyDays?: number;
  runId?: Uuid;
}

export interface ProblemDetails {
  type?: string;
  title: string;
  status: number;
  detail?: string | null;
  instance?: string | null;
  traceId?: string | null;
  errors?: Record<string, string[]>;
  [k: string]: unknown;
}
