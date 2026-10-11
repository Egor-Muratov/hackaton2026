import { useMutation, useQueryClient } from '@tanstack/react-query';
import { App as AntApp, Button, Progress, Space, Typography } from 'antd';
import { useState } from 'react';
import { api } from '../api/client';
import { sleep } from '../api/http';
import type { Job } from '../api/types';

const FILES = ['store-sales/train.csv', 'store-sales/test.csv'];

async function waitJob(jobId: string, onTick: (j: Job) => void): Promise<Job> {
  for (;;) {
    const j = await api.getJob(jobId);
    onTick(j);
    if (j.status === 'done') return j;
    if (j.status === 'failed') throw new Error(j.error ?? 'Импорт упал');
    await sleep(2000);
  }
}

/** Импорт train.csv, затем test.csv в тот же датасет; опрос /api/jobs/{id} раз в 2 с. */
export default function ImportButton() {
  const qc = useQueryClient();
  const { message } = AntApp.useApp();
  const [stage, setStage] = useState<{ file: string; job: Job | null } | null>(null);

  const m = useMutation({
    mutationFn: async () => {
      let datasetId: string | null = null;
      let last: Job | null = null;
      for (const path of FILES) {
        setStage({ file: path, job: null });
        const acc = await api.importDataset(datasetId ? { path, datasetId } : { path, name: 'store-sales' });
        datasetId = acc.datasetId;
        last = await waitJob(acc.jobId, (job) => setStage({ file: path, job }));
      }
      return last;
    },
    onSuccess: (j) => {
      message.success(`Импорт завершён${j?.warnings.length ? `, предупреждений: ${j.warnings.length}` : ''}`);
      qc.invalidateQueries({ queryKey: ['meta'] });
      qc.invalidateQueries({ queryKey: ['series'] });
    },
    onError: (e) => message.error(`Импорт: ${(e as Error).message}`),
    onSettled: () => setTimeout(() => setStage(null), 1500),
  });

  const j = stage?.job;
  return (
    <Space>
      <Button onClick={() => m.mutate()} loading={m.isPending}>Импорт данных</Button>
      {stage && (
        <Space size="small">
          <Typography.Text type="secondary">{stage.file}: {j?.message ?? j?.status ?? 'отправка…'}</Typography.Text>
          <Progress percent={Math.round((j?.progress ?? 0) * 100)} size="small" style={{ width: 120 }} status={j?.status === 'failed' ? 'exception' : undefined} />
        </Space>
      )}
    </Space>
  );
}
