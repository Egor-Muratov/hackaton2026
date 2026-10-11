import { Table } from 'antd';
import type { ForecastPoint } from '../api/types';
import { fmtNum } from '../format';

export default function ForecastTable({ rows }: { rows: ForecastPoint[] }) {
  return (
    <Table<ForecastPoint>
      size="small"
      rowKey="date"
      dataSource={rows}
      pagination={false}
      columns={[
        { title: 'Дата', dataIndex: 'date' },
        { title: 'Прогноз', dataIndex: 'yhat', align: 'right', render: (v: number) => fmtNum(v) },
        { title: 'Нижняя (p10)', dataIndex: 'lo', align: 'right', render: (v: number | null) => fmtNum(v) },
        { title: 'Верхняя (p90)', dataIndex: 'hi', align: 'right', render: (v: number | null) => fmtNum(v) },
      ]}
    />
  );
}
