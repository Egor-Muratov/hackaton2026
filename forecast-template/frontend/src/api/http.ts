import type { ProblemDetails } from './types';

export class ApiError extends Error {
  constructor(
    public status: number,
    public problem: ProblemDetails | null,
    public correlationId: string,
  ) {
    super(ApiError.describe(status, problem));
  }

  static describe(status: number, p: ProblemDetails | null): string {
    if (!p) return `HTTP ${status}`;
    const fields = p.errors
      ? ' — ' + Object.entries(p.errors).map(([k, v]) => `${k}: ${v.join(', ')}`).join('; ')
      : '';
    return `${p.title}${p.detail ? `: ${p.detail}` : ''}${fields}`;
  }
}

export const newId = () =>
  typeof crypto !== 'undefined' && 'randomUUID' in crypto
    ? crypto.randomUUID()
    : 'xxxxxxxx-xxxx-4xxx-8xxx-xxxxxxxxxxxx'.replace(/x/g, () => ((Math.random() * 16) | 0).toString(16));

/** fetch + JSON + ProblemDetails + X-Correlation-Id. */
export async function http<T>(url: string, init: RequestInit & { timeoutMs?: number } = {}): Promise<T> {
  const correlationId = newId();
  const ctrl = new AbortController();
  const timer = init.timeoutMs ? setTimeout(() => ctrl.abort(), init.timeoutMs) : undefined;
  try {
    const res = await fetch(url, {
      ...init,
      signal: init.signal ?? ctrl.signal,
      headers: {
        Accept: 'application/json, application/problem+json',
        ...(init.body ? { 'Content-Type': 'application/json' } : {}),
        'X-Correlation-Id': correlationId,
        ...init.headers,
      },
    });
    const text = await res.text();
    const body = text ? safeJson(text) : null;
    if (!res.ok) {
      const problem = body && typeof body === 'object' && 'title' in body ? (body as ProblemDetails) : null;
      throw new ApiError(res.status, problem, correlationId);
    }
    return body as T;
  } finally {
    if (timer) clearTimeout(timer);
  }
}

function safeJson(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}

export const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
