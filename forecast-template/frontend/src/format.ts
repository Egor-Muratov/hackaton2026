export const fmtNum = (v: number | null | undefined, digits = 1) =>
  v == null ? '—' : v.toLocaleString('ru-RU', { maximumFractionDigits: digits, minimumFractionDigits: 0 });

export const fmtTime = (iso: string | null | undefined) =>
  iso ? new Date(iso).toLocaleString('ru-RU', { dateStyle: 'short', timeStyle: 'short' }) : '—';
