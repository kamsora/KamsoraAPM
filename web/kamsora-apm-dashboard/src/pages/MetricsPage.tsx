import { keepPreviousData, useQuery } from '@tanstack/react-query';
import ReactECharts from 'echarts-for-react';
import { useState } from 'react';
import { api, buildRangeQuery } from '../api/client';
import type { MetricCatalogEntry, MetricSeriesPoint } from '../api/types';
import { Empty, ErrorBlock, Loading } from '../components/Loading';
import { TimeRangePicker, useTimeRange } from '../components/TimeRangePicker';

export default function MetricsPage() {
  const range = useTimeRange();
  const [selected, setSelected] = useState<string | null>(null);
  const [filter, setFilter]     = useState('');

  const catalog = useQuery({
    queryKey: ['metrics-catalog', range.presetKey],
    queryFn:  () => api<MetricCatalogEntry[]>(`/v1/metrics/?${buildRangeQuery(range.from, range.to)}`),
    placeholderData: keepPreviousData,
    refetchInterval: 60_000,
  });

  const series = useQuery({
    queryKey: ['metric-series', selected, range.presetKey],
    enabled: !!selected,
    queryFn:  () => api<MetricSeriesPoint[]>(
      `/v1/metrics/${encodeURIComponent(selected!)}/series?${buildRangeQuery(range.from, range.to, { bucketSeconds: 60 })}`),
    placeholderData: keepPreviousData,
    refetchInterval: 30_000,
  });

  const filtered = (catalog.data ?? []).filter(m =>
    !filter.trim() || m.metricName.toLowerCase().includes(filter.toLowerCase()));

  return (
    <>
      <h1 className="page-title">
        Metrics
        <TimeRangePicker />
      </h1>

      <div style={{ display: 'grid', gridTemplateColumns: '320px 1fr', gap: 12 }}>
        <div className="card" style={{ padding: 0, maxHeight: '70vh', overflowY: 'auto' }}>
          <div style={{ padding: 12, borderBottom: '1px solid var(--border, #1e293b)' }}>
            <input value={filter} onChange={e => setFilter(e.target.value)}
              placeholder="Filter metric names…" style={{ width: '100%', padding: '6px 10px' }} />
          </div>
          {catalog.isLoading ? <Loading /> :
           catalog.error    ? <ErrorBlock error={catalog.error} /> :
           filtered.length === 0 ? <Empty label="No metrics ingested yet. Make sure the Agent has EnableMetrics=true and the Collector is reachable." /> : (
            <table>
              <thead>
                <tr>
                  <th>Metric</th>
                  <th>Kind</th>
                  <th style={{ textAlign: 'right' }}>Points</th>
                </tr>
              </thead>
              <tbody>
                {filtered.map(m => (
                  <tr key={`${m.metricName}-${m.serviceName}`}
                      className="clickable"
                      onClick={() => setSelected(m.metricName)}
                      style={{ background: selected === m.metricName ? 'rgba(124,92,255,0.10)' : undefined }}>
                    <td>
                      <div className="mono" style={{ fontSize: 13 }}>{m.metricName}</div>
                      <div className="faint" style={{ fontSize: 11 }}>{m.serviceName}{m.metricUnit ? ` · ${m.metricUnit}` : ''}</div>
                    </td>
                    <td><span className="badge muted" style={{ fontSize: 10 }}>{m.metricKind}</span></td>
                    <td style={{ textAlign: 'right' }} className="faint">{m.pointCount.toLocaleString()}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>

        <div className="card">
          {!selected ? <Empty label="Pick a metric from the left to see its series." /> :
           series.isLoading ? <Loading /> :
           series.error    ? <ErrorBlock error={series.error} /> :
           (series.data?.length ?? 0) === 0 ? <Empty label={`No data points for "${selected}" in this window.`} /> : (
            <>
              <h3 className="card-title">
                <span className="mono">{selected}</span>
                <span className="muted" style={{ fontSize: 11, fontWeight: 400, marginLeft: 8 }}>last-value per minute, grouped by attributes</span>
              </h3>
              <SeriesChart points={series.data!} />
            </>
          )}
        </div>
      </div>
    </>
  );
}

function SeriesChart({ points }: { points: MetricSeriesPoint[] }) {
  // Group by seriesKey for legend.
  const byKey: Record<string, [number, number][]> = {};
  for (const p of points) {
    const t = new Date(p.bucketStartUtc).getTime();
    const k = p.seriesKey || '(no attrs)';
    (byKey[k] ??= []).push([t, p.valueLast]);
  }
  const keys = Object.keys(byKey).sort();
  const palette = ['#7C5CFF', '#34D399', '#F59E0B', '#EF4444', '#5B89FF', '#10B981', '#F472B6', '#FBBF24', '#A855F7'];
  const option = {
    grid: { left: 60, right: 24, top: 32, bottom: 60 },
    legend: { textStyle: { color: '#94A1BC' }, top: 0, type: 'scroll' as const },
    tooltip: { trigger: 'axis', backgroundColor: '#16213A', borderColor: '#243049', textStyle: { color: '#E5EAF3' } },
    xAxis: { type: 'time', axisLabel: { color: '#94A1BC' } },
    yAxis: { type: 'value', axisLabel: { color: '#94A1BC' } },
    dataZoom: [
      { type: 'inside', xAxisIndex: 0 },
      { type: 'slider', xAxisIndex: 0, height: 18, bottom: 4, borderColor: '#243049', textStyle: { color: '#94A1BC' } },
    ],
    series: keys.map((k, i) => ({
      name:  k.length > 60 ? `${k.slice(0, 59)}…` : k,
      type:  'line' as const,
      smooth: true,
      symbol: 'none' as const,
      data:  byKey[k]!,
      itemStyle: { color: palette[i % palette.length] },
    })),
  };
  return <ReactECharts option={option} style={{ height: 360 }} theme="dark" />;
}
