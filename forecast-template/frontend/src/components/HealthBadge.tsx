import { useQuery } from '@tanstack/react-query';
import { Badge, Space, Tag, Tooltip } from 'antd';
import { api, type ModeInfo } from '../api/client';

const color = { Healthy: 'success', Degraded: 'warning', Unhealthy: 'error' } as const;

export default function HealthBadge({ modeInfo }: { modeInfo: ModeInfo }) {
  const q = useQuery({ queryKey: ['health'], queryFn: () => api.getHealth(), refetchInterval: 15_000, retry: 0 });
  const h = q.data;
  return (
    <Space size="middle">
      {modeInfo.mode === 'mock' && (
        <Tooltip title={modeInfo.reason}>
          <Tag color="magenta" style={{ fontWeight: 600 }}>ДЕМО-ДАННЫЕ (mock)</Tag>
        </Tooltip>
      )}
      {q.isError ? (
        <Badge status="error" text={<span style={{ color: '#fff' }}>API недоступен</span>} />
      ) : h ? (
        <Tooltip title={`db: ${h.checks.db}, ml: ${h.checks.ml}`}>
          <Badge status={color[h.status]} text={<span style={{ color: '#fff' }}>API {h.status} · ML {h.checks.ml} · {h.modelVersion ?? 'модель —'}</span>} />
        </Tooltip>
      ) : (
        <Badge status="processing" text={<span style={{ color: '#fff' }}>проверка…</span>} />
      )}
    </Space>
  );
}
