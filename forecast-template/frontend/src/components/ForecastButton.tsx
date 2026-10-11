import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { App as AntApp, Button, Space, Tag, Typography } from 'antd';
import { useEffect, useState } from 'react';
import { api } from '../api/client';

const statusColor = { queued: 'default', running: 'processing', done: 'success', failed: 'error' } as const;

/** POST /api/forecasts, затем опрос GET /api/forecasts/{runId} раз в 2 с до done/failed. */
export default function ForecastButton({ horizon, disabled }: { horizon: number; disabled?: boolean }) {
  const qc = useQueryClient();
  const { message } = AntApp.useApp();
  const [runId, setRunId] = useState<string | null>(null);

  const create = useMutation({
    mutationFn: () => api.createForecast({ horizon }),
    onSuccess: (r) => setRunId(r.runId),
    onError: (e) => message.error(`Прогноз: ${(e as Error).message}`),
  });

  const run = useQuery({
    queryKey: ['run', runId],
    queryFn: () => api.getForecastRun(runId!),
    enabled: !!runId,
    refetchInterval: (q) => (q.state.data && ['done', 'failed'].includes(q.state.data.status) ? false : 2000),
  });

  const status = run.data?.status;
  useEffect(() => {
    if (status === 'done') {
      message.success('Прогноз готов');
      qc.invalidateQueries({ queryKey: ['series'] });
      qc.invalidateQueries({ queryKey: ['meta'] });
    } else if (status === 'failed') {
      message.error(`Прогноз упал: ${run.data?.error ?? 'без описания'}`);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [status]);

  const busy = create.isPending || (!!runId && !status) || status === 'queued' || status === 'running';
  return (
    <Space>
      <Button type="primary" onClick={() => create.mutate()} loading={busy} disabled={disabled}>
        Построить прогноз
      </Button>
      {runId && (
        <Space size={4}>
          <Tag color={status ? statusColor[status] : 'default'}>{status ?? 'отправка'}</Tag>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>run {runId.slice(0, 8)}</Typography.Text>
        </Space>
      )}
    </Space>
  );
}
