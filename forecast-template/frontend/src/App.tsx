import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { Alert, App as AntApp, Button, Card, Col, Empty, Layout, Row, Select, Slider, Space, Spin, Typography } from 'antd';
import { api, exportCsvUrl, type ModeInfo } from './api/client';
import { ApiError } from './api/http';
import ForecastButton from './components/ForecastButton';
import ForecastChart from './components/ForecastChart';
import ForecastTable from './components/ForecastTable';
import HealthBadge from './components/HealthBadge';
import ImportButton from './components/ImportButton';
import MetricsCards from './components/MetricsCards';
import { downloadText, seriesToCsv } from './csv';
import { fmtTime } from './format';
import { useUrlFilters } from './urlState';

const ALL = 'all';

export default function App({ modeInfo }: { modeInfo: ModeInfo }) {
  const [f, setF] = useUrlFilters();
  const isMock = modeInfo.mode === 'mock';

  const meta = useQuery({ queryKey: ['meta'], queryFn: () => api.getMeta() });
  const hasData = !!meta.data?.latestDatasetId;

  const series = useQuery({
    queryKey: ['series', f.store, f.family, f.h],
    queryFn: () => api.getLatestForecastSeries({ store: f.store, family: f.family, horizon: f.h }),
    enabled: hasData,
    placeholderData: keepPreviousData,
  });

  const storeOptions = [
    { value: ALL, label: 'Все магазины' },
    ...(meta.data?.stores ?? []).map((s) => ({
      value: String(s.storeNbr),
      label: `${s.storeNbr}${s.city ? ` · ${s.city}` : ''}${s.type ? ` (${s.type})` : ''}`,
    })),
  ];
  const familyOptions = [{ value: ALL, label: 'Все семейства' }, ...(meta.data?.families ?? []).map((x) => ({ value: x, label: x }))];
  const s = series.data;
  const seriesErr = series.error as ApiError | null;

  return (
    <AntApp>
      <Layout style={{ minHeight: '100vh' }}>
        <Layout.Header style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 16, flexWrap: 'wrap', height: 'auto', minHeight: 64 }}>
          <Typography.Title level={4} style={{ color: '#fff', margin: 0 }}>Прогноз продаж</Typography.Title>
          <HealthBadge modeInfo={modeInfo} />
        </Layout.Header>

        <Layout.Content style={{ padding: 16, maxWidth: 1400, width: '100%', margin: '0 auto' }}>
          <Space direction="vertical" size={16} style={{ width: '100%' }}>
            <Card size="small">
              <Row gutter={[16, 12]} align="middle">
                <Col xs={24} md={7}>
                  <Typography.Text type="secondary">Магазин</Typography.Text>
                  <Select showSearch optionFilterProp="label" style={{ width: '100%' }} value={f.store}
                    options={storeOptions} loading={meta.isLoading} onChange={(v) => setF({ store: v })} />
                </Col>
                <Col xs={24} md={7}>
                  <Typography.Text type="secondary">Семейство</Typography.Text>
                  <Select showSearch optionFilterProp="label" style={{ width: '100%' }} value={f.family}
                    options={familyOptions} loading={meta.isLoading} onChange={(v) => setF({ family: v })} />
                </Col>
                <Col xs={24} md={5}>
                  <Typography.Text type="secondary">Горизонт: {f.h} дн.</Typography.Text>
                  <Slider min={1} max={meta.data?.maxHorizon ?? 16} value={f.h} marks={{ 1: '1', 7: '7', 14: '14', 16: '16' }}
                    onChange={(v) => setF({ h: v })} />
                </Col>
                <Col xs={24} md={5}>
                  <Space direction="vertical" size={8}>
                    <ForecastButton horizon={f.h} disabled={!hasData} />
                    <ImportButton />
                  </Space>
                </Col>
              </Row>
            </Card>

            {meta.isError && <Alert type="error" showIcon message="Не удалось получить /api/meta" description={(meta.error as Error).message} />}
            {meta.data && !hasData && (
              <Alert type="info" showIcon message="Данных нет" description="Нажмите «Импорт данных» (store-sales/train.csv + test.csv)." />
            )}

            {seriesErr && (
              <Alert type="warning" showIcon message="Ряд не получен"
                description={`${seriesErr.message}${seriesErr.problem?.traceId ? ` (traceId ${seriesErr.problem.traceId})` : ''}`}
                action={<Button size="small" onClick={() => setF({ store: ALL, family: ALL })}>Показать агрегат</Button>} />
            )}

            {hasData && (
              <Card size="small"
                title={`${f.store === ALL ? 'Все магазины' : `Магазин ${f.store}`} · ${f.family === ALL ? 'все семейства' : f.family}`}
                extra={
                  <Space>
                    {s && <Typography.Text type="secondary" style={{ fontSize: 12 }}>cutoff {s.cutoffDate ?? '—'} · сгенерирован {fmtTime(s.generatedAt)}</Typography.Text>}
                    <Button size="small" disabled={!s?.forecast.length}
                      onClick={() => s && downloadText(seriesToCsv(s), `forecast-${s.store}-${s.family}.csv`)}>CSV ряда</Button>
                    <Button size="small" disabled={isMock || !s?.runId} href={s?.runId ? exportCsvUrl(s.runId) : undefined}>
                      CSV прогона (все ряды)
                    </Button>
                  </Space>
                }>
                {series.isLoading ? (
                  <div style={{ height: 420, display: 'grid', placeItems: 'center' }}><Spin /></div>
                ) : s ? (
                  <>
                    {s.runId == null && <Alert type="info" showIcon style={{ marginBottom: 8 }} message="Готового прогона ещё нет — показана только история. Нажмите «Построить прогноз»." />}
                    <ForecastChart data={s} />
                  </>
                ) : (
                  <Empty />
                )}
              </Card>
            )}

            {s && s.runId && (
              <Row gutter={[16, 16]}>
                <Col xs={24} lg={12}>
                  <Card size="small" title="Метрики модели">
                    <MetricsCards metrics={s.metrics} modelVersion={s.modelVersion} />
                  </Card>
                </Col>
                <Col xs={24} lg={12}>
                  <Card size="small" title={`Прогноз на ${s.forecast.length} дн.`}>
                    <ForecastTable rows={s.forecast} />
                  </Card>
                </Col>
              </Row>
            )}
          </Space>
        </Layout.Content>
      </Layout>
    </AntApp>
  );
}
