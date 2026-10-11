import ReactEChartsCore from 'echarts-for-react/lib/core';
import * as echarts from 'echarts/core';
import { LineChart } from 'echarts/charts';
import {
  DataZoomComponent, GridComponent, LegendComponent, MarkLineComponent, TooltipComponent,
} from 'echarts/components';
import { CanvasRenderer } from 'echarts/renderers';
import { useMemo } from 'react';
import type { SeriesForecast } from '../api/types';
import { fmtNum } from '../format';

echarts.use([LineChart, GridComponent, TooltipComponent, LegendComponent, DataZoomComponent, MarkLineComponent, CanvasRenderer]);

const INITIAL_WINDOW_DAYS = 120;

export default function ForecastChart({ data }: { data: SeriesForecast }) {
  const option = useMemo(() => {
    const { history, forecast, cutoffDate } = data;
    const dates = [...history.map((p) => p.date), ...forecast.map((p) => p.date)];
    const nH = history.length;
    const pad = <T,>(n: number) => Array<T | null>(n).fill(null);

    const fact = [...history.map((p) => p.sales), ...pad<number>(forecast.length)];
    // Прогноз начинаем с последней точки факта, чтобы линии стыковались.
    const yhat = [...pad<number>(Math.max(0, nH - 1)), ...(nH ? [history[nH - 1].sales] : []), ...forecast.map((p) => p.yhat)];
    const hasBand = forecast.some((p) => p.lo != null && p.hi != null);
    const lo = [...pad<number>(nH), ...forecast.map((p) => (p.lo != null && p.hi != null ? p.lo : null))];
    const width = [...pad<number>(nH), ...forecast.map((p) => (p.lo != null && p.hi != null ? p.hi - p.lo : null))];

    const startIdx = Math.max(0, dates.length - forecast.length - INITIAL_WINDOW_DAYS);
    const startPct = dates.length ? (startIdx / dates.length) * 100 : 0;
    const pct = Math.round(data.intervalLevel * 100);

    const series: object[] = [
      { name: 'Факт', type: 'line', data: fact, showSymbol: false, lineStyle: { width: 1.5 }, color: '#1f6feb' },
      {
        name: 'Прогноз', type: 'line', data: yhat, showSymbol: false, color: '#d4380d',
        lineStyle: { width: 2, type: 'dashed' },
        markLine: cutoffDate
          ? { symbol: 'none', silent: true, lineStyle: { color: '#8c8c8c', type: 'solid' }, label: { formatter: 'cutoff' }, data: [{ xAxis: cutoffDate }] }
          : undefined,
      },
    ];
    if (hasBand) {
      series.push(
        { name: 'lo', type: 'line', data: lo, stack: 'band', symbol: 'none', lineStyle: { opacity: 0 }, tooltip: { show: false } },
        {
          name: `Интервал ${pct}%`, type: 'line', data: width, stack: 'band', symbol: 'none', color: '#fa8c16',
          lineStyle: { opacity: 0 }, areaStyle: { color: 'rgba(250,140,22,0.22)' },
        },
      );
    }

    return {
      animation: false,
      grid: { left: 70, right: 24, top: 40, bottom: 80 },
      legend: { top: 4, data: ['Факт', 'Прогноз', ...(hasBand ? [`Интервал ${pct}%`] : [])] },
      tooltip: {
        trigger: 'axis',
        formatter: (params: { dataIndex: number }[]) => {
          const i = params[0]?.dataIndex ?? 0;
          const d = dates[i];
          if (i < nH) {
            const h = history[i];
            return `${d}<br/>Факт: <b>${fmtNum(h.sales)}</b>${h.onpromotion != null ? `<br/>Промо: ${h.onpromotion}` : ''}`;
          }
          const f = forecast[i - nH];
          const band = f.lo != null && f.hi != null ? `<br/>${pct}%: ${fmtNum(f.lo)} … ${fmtNum(f.hi)}` : '';
          return `${d}<br/>Прогноз: <b>${fmtNum(f.yhat)}</b>${band}`;
        },
      },
      xAxis: { type: 'category', data: dates, boundaryGap: false },
      yAxis: { type: 'value', scale: true, axisLabel: { formatter: (v: number) => fmtNum(v, 0) } },
      dataZoom: [
        { type: 'inside', start: startPct, end: 100 },
        { type: 'slider', start: startPct, end: 100, bottom: 20 },
      ],
      series,
    };
  }, [data]);

  return <ReactEChartsCore echarts={echarts} option={option} notMerge style={{ height: 420 }} />;
}
