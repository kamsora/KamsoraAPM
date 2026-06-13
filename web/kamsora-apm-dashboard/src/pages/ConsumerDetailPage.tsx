import { keepPreviousData, useQuery } from '@tanstack/react-query';
import ReactECharts from 'echarts-for-react';
import type { EChartsOption } from 'echarts';
import { useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { api, buildRangeQuery } from '../api/client';
import type { ConsumerRouteWithSparkline, ConsumerTimeseriesPoint } from '../api/types';
import { Empty, ErrorBlock, Loading } from '../components/Loading';
import { TimeRangePicker, useTimeRange } from '../components/TimeRangePicker';
import { TracePeekDrawer, type TraceFilter } from '../components/TracePeek';

export default function ConsumerDetailPage() {
  const { consumerId = '' } = useParams();
  const range = useTimeRange();
  const decoded = decodeURIComponent(consumerId);
  const [peek, setPeek] = useState<TraceFilter | null>(null);

  const timeseries = useQuery({
    queryKey: ['consumer-ts', decoded, range.presetKey],
    queryFn: () => api<ConsumerTimeseriesPoint[]>(
      `/v1/consumers/${encodeURIComponent(decoded)}/timeseries?${buildRangeQuery(range.from, range.to, { bucketSeconds: 3600 })}`),
    placeholderData: keepPreviousData,
    refetchInterval: 30_000,
  });

  const routes = useQuery({
    queryKey: ['consumer-routes-detailed', decoded, range.presetKey],
    queryFn: () => api<ConsumerRouteWithSparkline[]>(
      `/v1/consumers/${encodeURIComponent(decoded)}/routes-detailed?${buildRangeQuery(range.from, range.to, { limit: 25 })}`),
    placeholderData: keepPreviousData,
  });

  const totals = useMemo(() => {
    const list = timeseries.data ?? [];
    return {
      req:    list.reduce((s, p) => s + p.requestCount,      0),
      err:    list.reduce((s, p) => s + p.errorCount,        0),
      s4xx:   list.reduce((s, p) => s + p.clientErrorCount,  0),
      s5xx:   list.reduce((s, p) => s + p.serverErrorCount,  0),
    };
  }, [timeseries.data]);

  /** Click handler for chart bars: peek at this consumer's traces for the bucket, in place. */
  function drillToTraces(bucketStartUtc: Date, opts?: { errorsOnly?: boolean }) {
    setPeek({
      consumerId: decoded,
      fromUtc:    bucketStartUtc.toISOString(),
      toUtc:      new Date(bucketStartUtc.getTime() + 3600 * 1000).toISOString(),
      kind:       'SERVER',
      errorsOnly: opts?.errorsOnly,
    });
  }

  function drillToRoute(serviceName: string, httpRoute: string) {
    setPeek({
      consumerId: decoded,
      service:    serviceName,
      route:      httpRoute,
      kind:       'SERVER',
      fromUtc:    range.from.toISOString(),
      toUtc:      range.to.toISOString(),
    });
  }

  return (
    <>
      <h1 className="page-title" style={{ display: 'flex', flexWrap: 'wrap', gap: 12, alignItems: 'baseline' }}>
        <Link to="/consumers" style={{ fontSize: 14, color: 'var(--text-muted)' }}>← All consumers</Link>
        <span className="mono" title={decoded} style={{ wordBreak: 'break-all' }}>
          {decoded === '(anonymous)' ? <span className="badge warn">anonymous</span> : decoded}
        </span>
        <TimeRangePicker />
      </h1>

      <div className="stat-grid">
        <Stat label="Requests"   value={totals.req.toLocaleString()} />
        <Stat label="4xx errors" value={totals.s4xx.toLocaleString()} accent={totals.s4xx > 0 ? 'warn' : undefined} />
        <Stat label="5xx errors" value={totals.s5xx.toLocaleString()} accent={totals.s5xx > 0 ? 'err'  : undefined} />
        <Stat label="Error rate" value={totals.req > 0 ? `${((totals.err / totals.req) * 100).toFixed(2)}%` : '—'} />
      </div>

      <div className="card" style={{ marginBottom: 24 }}>
        <h3 className="card-title">
          Traffic + errors
          <span className="muted" style={{ fontSize: 11, fontWeight: 400, marginLeft: 8 }}>
            click a bar to drill into traces · drag to zoom
          </span>
        </h3>
        {timeseries.isLoading ? <Loading /> :
         timeseries.error    ? <ErrorBlock error={timeseries.error} /> :
         (timeseries.data?.length ?? 0) === 0 ? <Empty /> :
         <TrafficChart points={timeseries.data!} onClickBucket={drillToTraces} />}
      </div>

      <div className="card" style={{ marginBottom: 24 }}>
        <h3 className="card-title">
          Latency
          <span className="muted" style={{ fontSize: 11, fontWeight: 400, marginLeft: 8 }}>drag to zoom</span>
        </h3>
        {timeseries.isLoading ? <Loading /> :
         timeseries.error    ? <ErrorBlock error={timeseries.error} /> :
         (timeseries.data?.length ?? 0) === 0 ? <Empty /> : <LatencyChart points={timeseries.data!} />}
      </div>

      <div className="card" style={{ padding: 0 }}>
        <h3 className="card-title" style={{ padding: '12px 16px 0' }}>
          Top routes
          <span className="muted" style={{ fontSize: 11, fontWeight: 400, marginLeft: 8 }}>
            click a row to filter Traces by route + consumer
          </span>
        </h3>
        {routes.isLoading ? <Loading /> :
         routes.error    ? <ErrorBlock error={routes.error} /> :
         (routes.data?.length ?? 0) === 0 ? <Empty /> : (
          <table>
            <thead>
              <tr>
                <th>Service</th>
                <th>Route</th>
                <th style={{ textAlign: 'right' }}>Requests</th>
                <th style={{ textAlign: 'right' }}>Errors</th>
                <th style={{ textAlign: 'right' }}>Error rate</th>
                <th style={{ textAlign: 'right' }}>p50</th>
                <th style={{ textAlign: 'right' }}>p99</th>
                <th>Trend</th>
              </tr>
            </thead>
            <tbody>
              {routes.data!.map((r, i) => (
                <tr key={i} className="clickable" onClick={() => drillToRoute(r.serviceName, r.httpRoute)}
                    title="Open Traces page filtered by this route + consumer">
                  <td>{r.serviceName}</td>
                  <td className="mono" style={{ wordBreak: 'break-all' }}>{r.httpRoute || '—'}</td>
                  <td style={{ textAlign: 'right' }}>{r.requestCount.toLocaleString()}</td>
                  <td style={{ textAlign: 'right', color: r.errorCount > 0 ? 'var(--err)' : undefined }}>{r.errorCount.toLocaleString()}</td>
                  <td style={{ textAlign: 'right' }}>{(r.errorRate * 100).toFixed(2)}%</td>
                  <td style={{ textAlign: 'right' }}>{formatMs(r.latencyP50Ms)}</td>
                  <td style={{ textAlign: 'right' }}>{formatMs(r.latencyP99Ms)}</td>
                  <td style={{ width: 140 }}><Sparkline data={r.sparkline} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      <TracePeekDrawer filter={peek} onClose={() => setPeek(null)} />
    </>
  );
}

function Stat({ label, value, sub, accent }: { label: string; value: string; sub?: string; accent?: 'ok' | 'err' | 'warn' }) {
  return (
    <div className="card">
      <h3 className="card-title">{label}</h3>
      <div className="stat-value" style={accent ? { color: `var(--${accent})` } : undefined}>{value}</div>
      {sub && <div className="stat-sub">{sub}</div>}
    </div>
  );
}

function TrafficChart({
  points,
  onClickBucket,
}: {
  points: ConsumerTimeseriesPoint[];
  onClickBucket: (bucketStartUtc: Date, opts?: { errorsOnly?: boolean }) => void;
}) {
  const xs = points.map(p => new Date(p.bucketStartUtc).getTime());
  const option: EChartsOption = {
    grid: { left: 50, right: 24, top: 24, bottom: 60 },
    legend: { textStyle: { color: '#94A1BC' }, top: 0 },
    tooltip: { trigger: 'axis', backgroundColor: '#16213A', borderColor: '#243049', textStyle: { color: '#E5EAF3' } },
    xAxis: { type: 'time', axisLabel: { color: '#94A1BC' } },
    yAxis: { type: 'value', name: 'requests', axisLabel: { color: '#94A1BC' }, nameTextStyle: { color: '#94A1BC' } },
    dataZoom: [
      { type: 'inside', xAxisIndex: 0 },
      { type: 'slider', xAxisIndex: 0, height: 18, bottom: 4, borderColor: '#243049', textStyle: { color: '#94A1BC' } },
    ],
    series: [
      { name: '2xx/3xx', type: 'bar', stack: 'a',
        data: xs.map((x, i) => [x, Math.max(0, points[i]!.requestCount - points[i]!.clientErrorCount - points[i]!.serverErrorCount)]),
        itemStyle: { color: '#7C5CFF' } },
      { name: '4xx', type: 'bar', stack: 'a',
        data: xs.map((x, i) => [x, points[i]!.clientErrorCount]),
        itemStyle: { color: '#F59E0B' } },
      { name: '5xx', type: 'bar', stack: 'a',
        data: xs.map((x, i) => [x, points[i]!.serverErrorCount]),
        itemStyle: { color: '#EF4444' } },
    ],
  };

  return (
    <ReactECharts
      option={option}
      style={{ height: 260 }}
      theme="dark"
      notMerge
      onEvents={{
        click: (params: any) => {
          const ts = Array.isArray(params.value) ? Number(params.value[0]) : Number(params.value);
          if (!Number.isFinite(ts)) return;
          const bucketStartUtc = new Date(ts);
          // Series name reveals which segment was clicked: 4xx/5xx → errorsOnly drill.
          const errorsOnly = params.seriesName === '4xx' || params.seriesName === '5xx';
          onClickBucket(bucketStartUtc, { errorsOnly });
        },
      }}
    />
  );
}

function LatencyChart({ points }: { points: ConsumerTimeseriesPoint[] }) {
  // Defensive: drop buckets where every quantile is 0 (rollup hadn't accumulated
  // enough samples to produce a meaningful tdigest). Otherwise the line drops
  // to y=0 and hides at the X axis — that was the "blank chart" symptom.
  const valid = points.filter(p => p.latencyP50Ms > 0 || p.latencyP90Ms > 0 || p.latencyP99Ms > 0);
  if (valid.length === 0) return <Empty label="No latency data in the selected window." />;

  const xs = valid.map(p => new Date(p.bucketStartUtc).getTime());
  // Compute a sane Y-axis ceiling so the lines never run off the top.
  const maxVal = Math.max(...valid.map(p => p.latencyP99Ms || 0), 10);
  const yMax = Math.ceil(maxVal * 1.15);

  const option: EChartsOption = {
    grid: { left: 50, right: 24, top: 24, bottom: 60 },
    legend: { textStyle: { color: '#94A1BC' }, top: 0 },
    tooltip: { trigger: 'axis', backgroundColor: '#16213A', borderColor: '#243049', textStyle: { color: '#E5EAF3' } },
    xAxis: { type: 'time', axisLabel: { color: '#94A1BC' } },
    yAxis: { type: 'value', name: 'ms', max: yMax, axisLabel: { color: '#94A1BC' }, nameTextStyle: { color: '#94A1BC' } },
    dataZoom: [
      { type: 'inside', xAxisIndex: 0 },
      { type: 'slider', xAxisIndex: 0, height: 18, bottom: 4, borderColor: '#243049', textStyle: { color: '#94A1BC' } },
    ],
    series: [
      { name: 'p50', type: 'line', smooth: true, symbol: 'circle', symbolSize: 5,
        data: xs.map((x, i) => [x, valid[i]!.latencyP50Ms]), itemStyle: { color: '#34D399' } },
      { name: 'p90', type: 'line', smooth: true, symbol: 'circle', symbolSize: 5,
        data: xs.map((x, i) => [x, valid[i]!.latencyP90Ms]), itemStyle: { color: '#F59E0B' } },
      { name: 'p99', type: 'line', smooth: true, symbol: 'circle', symbolSize: 5,
        data: xs.map((x, i) => [x, valid[i]!.latencyP99Ms]), itemStyle: { color: '#EF4444' } },
    ],
  };
  return <ReactECharts option={option} style={{ height: 260 }} theme="dark" notMerge />;
}

/** Tiny inline SVG sparkline for the Top Routes table. No ECharts overhead per row. */
function Sparkline({ data }: { data: number[] }) {
  if (!data || data.length === 0) return <span className="faint">—</span>;
  const w = 120, h = 28, pad = 2;
  const max = Math.max(...data, 1);
  const stepX = data.length > 1 ? (w - 2 * pad) / (data.length - 1) : 0;
  const points = data.map((v, i) => {
    const x = pad + i * stepX;
    const y = h - pad - (v / max) * (h - 2 * pad);
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  }).join(' ');
  const last = data[data.length - 1] ?? 0;
  return (
    <svg width={w} height={h} style={{ display: 'block' }}>
      <polyline fill="none" stroke="#7C5CFF" strokeWidth="1.5" points={points} />
      {/* end dot */}
      <circle cx={pad + (data.length - 1) * stepX} cy={h - pad - (last / max) * (h - 2 * pad)} r="2" fill="#7C5CFF" />
    </svg>
  );
}

function formatMs(ms: number): string {
  if (!Number.isFinite(ms) || ms <= 0) return '—';
  if (ms < 10) return `${ms.toFixed(2)}ms`;
  return `${Math.round(ms)}ms`;
}
