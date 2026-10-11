import type { SeriesForecast } from './api/types';

/** CSV текущего ряда на клиенте: формат как у export.csv (null — пустая ячейка, точка). */
export function seriesToCsv(s: SeriesForecast): string {
  const esc = (v: string) => (/[",\n]/.test(v) ? `"${v.replace(/"/g, '""')}"` : v);
  const num = (v: number | null) => (v == null ? '' : String(v));
  const lines = ['store_nbr,family,date,yhat,lo,hi'];
  for (const p of s.forecast) lines.push([esc(s.store), esc(s.family), p.date, num(p.yhat), num(p.lo), num(p.hi)].join(','));
  return lines.join('\n') + '\n';
}

export function downloadText(text: string, filename: string, type = 'text/csv;charset=utf-8') {
  const url = URL.createObjectURL(new Blob([text], { type }));
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
