import { useCallback, useState } from 'react';

export interface Filters { store: string; family: string; h: number }

const clampH = (v: number) => (Number.isFinite(v) ? Math.min(16, Math.max(1, Math.round(v))) : 16);

function read(): Filters {
  const p = new URLSearchParams(window.location.search);
  return { store: p.get('store') || 'all', family: p.get('family') || 'all', h: clampH(Number(p.get('h') ?? 16)) };
}

/** Фильтры живут в URL: ?store=44&family=GROCERY%20I&h=16 */
export function useUrlFilters(): [Filters, (patch: Partial<Filters>) => void] {
  const [filters, setFilters] = useState<Filters>(read);
  const update = useCallback((patch: Partial<Filters>) => {
    setFilters((prev) => {
      const next = { ...prev, ...patch };
      const p = new URLSearchParams(window.location.search);
      p.set('store', next.store);
      p.set('family', next.family);
      p.set('h', String(next.h));
      window.history.replaceState(null, '', `${window.location.pathname}?${p}`);
      return next;
    });
  }, []);
  return [filters, update];
}
