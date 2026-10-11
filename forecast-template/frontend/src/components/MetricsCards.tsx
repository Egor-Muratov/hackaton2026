import { Card, Col, Row, Statistic, Tooltip } from 'antd';
import type { Metrics } from '../api/types';

export default function MetricsCards({ metrics, modelVersion }: { metrics: Metrics | null; modelVersion: string | null }) {
  const m = metrics;
  const gain = m?.rmsle != null && m?.baselineRmsle ? (1 - m.rmsle / m.baselineRmsle) * 100 : null;
  const items = [
    { title: 'RMSLE', value: m?.rmsle, precision: 3, hint: 'Метрика Kaggle, CV по всем рядам' },
    { title: 'MAE', value: m?.mae, precision: 1, hint: 'Средняя абсолютная ошибка, CV по всем рядам' },
    { title: 'RMSLE бейзлайна', value: m?.baselineRmsle, precision: 3, hint: 'Сезонный naive / среднее по дню недели' },
    { title: 'Лучше бейзлайна', value: gain, precision: 1, suffix: '%', hint: '1 − RMSLE / RMSLE бейзлайна' },
  ];
  return (
    <Row gutter={[12, 12]}>
      {items.map((it) => (
        <Col xs={12} md={6} key={it.title}>
          <Card size="small">
            <Tooltip title={it.hint}>
              <Statistic
                title={it.title}
                value={it.value ?? '—'}
                precision={it.value != null ? it.precision : undefined}
                suffix={it.value != null ? it.suffix : undefined}
              />
            </Tooltip>
          </Card>
        </Col>
      ))}
      <Col span={24} style={{ color: '#8c8c8c', fontSize: 12 }}>
        Модель: {modelVersion ?? '—'} · метрики CV по всем рядам, не по выбранному
      </Col>
    </Row>
  );
}
