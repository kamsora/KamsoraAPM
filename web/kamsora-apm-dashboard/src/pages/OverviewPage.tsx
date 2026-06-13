import { keepPreviousData, useQuery } from '@tanstack/react-query';
import ReactECharts from 'echarts-for-react';
import type { ECharts } from 'echarts';
import { useState } from 'react';
import { Link } from 'react-router-dom';
import { api, buildRangeQuery } from '../api/client';
import type {
  DatabaseOverview, DbSystemBreakdown, OverviewSnapshot,
  TimeseriesPoint, TopQuery, TopRoute,
} from '../api/types';
import { bucketWindow, onTimeClick } from '../charts/drill';
import { areaGradient } from '../charts/kamsoraTheme';
import { ErrorBlock, Loading, Empty } from '../components/Loading';
import { RouteDrillDrawer } from '../components/RouteDrillDrawer';
import { TimeRangePicker, useTimeRange } from '../components/TimeRangePicker';
import { TracePeekDrawer, type TraceFilter } from '../components/TracePeek';
import { DataBar, MethodBadge, RouteLabel } from '../components/viz';

export default function OverviewPage() {
  const range = useTimeRange();
  const [drillRoute, setDrillRoute] = useState<TopRoute | null>(null);
  const [peek, setPeek] = useState<TraceFilter | null>(null);
  const bucketSec = pickBucket(range.from, range.to);

  function drillToWindow(timeMs: number, errorsOnly = false) {
    const w = bucketWindow(timeMs, bucketSec);
    setPeek({ ...w, kind: 'SERVER', errorsOnly });
  }

  const overview = useQuery({
    queryKey: ['overview', range.presetKey],
    queryFn: () => api<OverviewSnapshot>(`/v1/overview?${buildRangeQuery(range.from, range.to)}`),
    placeholderData: keepPreviousData,
  });

  const series = useQuery({
    queryKey: ['timeseries', range.presetKey],
    queryFn: () => api<TimeseriesPoint[]>(`/v1/timeseries/latency?${buildRangeQuery(range.from, range.to, { bucketSeconds: bucketSec })}`),
    placeholderData: keepPreviousData,
  });

  const topRoutes = useQuery({
    queryKey: ['top-routes', range.presetKey],
    queryFn: () => api<TopRoute[]>(`/v1/top-routes?${buildRangeQuery(range.from, range.to, { limit: 10 })}`),
    placeholderData: keepPreviousData,
  });

  const dbOverview = useQuery({
    queryKey: ['db-overview', range.presetKey],
    queryFn: () => api<DatabaseOverview>(`/v1/database/overview?${buildRangeQuery(range.from, range.to)}`),
    placeholderData: keepPreviousData,
  });

  const dbSystems = useQuery({
    queryKey: ['db-systems', range.presetKey],
    queryFn: () => api<DbSystemBreakdown[]>(`/v1/database/systems?${buildRangeQuery(range.from, range.to)}`),
    placeholderData: keepPreviousData,
  });

  const topQueries = useQuery({
    queryKey: ['top-queries', range.presetKey],
    queryFn: () => api<TopQuery[]>(`/v1/database/top-queries?${buildRangeQuery(range.from, range.to, { limit: 15 })}`),
    placeholderData: keepPreviousData,
  });

  return (
    <>
      <h1 className="page-title">
        Overview
        <TimeRangePicker />
      </h1>

      {overview.isLoading ? <Loading shape="stats" /> : overview.error ? <ErrorBlock error={overview.error} /> : (
        <div className="stat-grid">
          <Stat label="Requests" value={overview.data!.totalSpans.toLocaleString()} />
          <Stat label="Errors"   value={overview.data!.errorSpans.toLocaleString()} sub={`${(overview.data!.errorRate * 100).toFixed(2)}% error rate`} accent={overview.data!.errorSpans > 0 ? 'err' : 'ok'} />
          <Stat label="p50 latency" value={formatMs(overview.data!.latencyP50Ms)} />
          <Stat label="p90 latency" value={formatMs(overview.data!.latencyP90Ms)} />
          <Stat label="p99 latency" value={formatMs(overview.data!.latencyP99Ms)} />
          <Stat label="Services" value={overview.data!.distinctServices.toString()} />
        </div>
      )}

      <div className="card" style={{ marginBottom: 24 }}>
        <h3 className="card-title">
          Latency percentiles
          <ChartHint />
        </h3>
        {series.isLoading ? <Loading shape="chart" /> : series.error ? <ErrorBlock error={series.error} /> :
          !series.data || series.data.length === 0 ? <Empty /> :
          <LatencyChart points={series.data} onDrill={drillToWindow} />}
      </div>

      <div className="card" style={{ marginBottom: 24 }}>
        <h3 className="card-title">
          Request volume + errors
          <ChartHint label="click a bar to peek at that window's traces · errors bar peeks errors only" />
        </h3>
        {series.isLoading ? <Loading shape="chart" /> : series.error ? <ErrorBlock error={series.error} /> :
          !series.data || series.data.length === 0 ? <Empty /> :
          <VolumeChart points={series.data} onDrill={drillToWindow} />}
      </div>

      <div className="card">
        <h3 className="card-title">
          Top routes
          <span className="muted" style={{ fontSize: 12, fontWeight: 400, marginLeft: 8, textTransform: 'none', letterSpacing: 0 }}>
            click a route for the drill-down
          </span>
        </h3>
        {topRoutes.isLoading ? <Loading /> : topRoutes.error ? <ErrorBlock error={topRoutes.error} /> :
          !topRoutes.data || topRoutes.data.length === 0 ? <Empty /> : (
          <table>
            <thead>
              <tr>
                <th>Service</th>
                <th>Method</th>
                <th>Route / span name</th>
                <th style={{ textAlign: 'right', width: 150 }}>Requests</th>
                <th style={{ textAlign: 'right' }}>Errors</th>
                <th style={{ textAlign: 'right' }}>p50</th>
                <th style={{ textAlign: 'right' }}>p90</th>
                <th style={{ textAlign: 'right' }}>p99</th>
              </tr>
            </thead>
            <tbody>
              {(() => {
                const maxCount = Math.max(1, ...topRoutes.data.map(r => r.count));
                return topRoutes.data.map((r, i) => (
                  <tr key={i} className={r.httpRoute ? 'clickable' : undefined}
                      onClick={() => r.httpRoute && setDrillRoute(r)}>
                    <td>
                      <Link to={`/services?name=${encodeURIComponent(r.serviceName)}`}
                        onClick={e => e.stopPropagation()}>
                        {r.serviceName}
                      </Link>
                    </td>
                    <td><MethodBadge method={r.httpMethod} /></td>
                    <td><RouteLabel route={r.httpRoute || r.spanName} /></td>
                    <td style={{ textAlign: 'right' }}><DataBar value={r.count} max={maxCount} /></td>
                    <td style={{ textAlign: 'right', color: r.errorCount > 0 ? 'var(--err)' : undefined }}>{r.errorCount.toLocaleString()}</td>
                    <td style={{ textAlign: 'right' }}>{formatMs(r.latencyP50Ms)}</td>
                    <td style={{ textAlign: 'right' }}>{formatMs(r.latencyP90Ms)}</td>
                    <td style={{ textAlign: 'right' }}>{formatMs(r.latencyP99Ms)}</td>
                  </tr>
                ));
              })()}
            </tbody>
          </table>
        )}
      </div>

      <RouteDrillDrawer
        service={drillRoute?.serviceName ?? null}
        route={drillRoute ? drillRoute.httpRoute : null}
        onClose={() => setDrillRoute(null)} />

      <TracePeekDrawer filter={peek} onClose={() => setPeek(null)} />

      {/* --- Database section --- */}

      <h2 style={{ fontSize: 18, fontWeight: 600, margin: '32px 0 16px', color: 'var(--text-muted)', textTransform: 'uppercase', letterSpacing: 0.06 }}>
        Database
      </h2>

      {dbOverview.isLoading ? <Loading /> : dbOverview.error ? <ErrorBlock error={dbOverview.error} /> : dbOverview.data && (
        <div className="stat-grid">
          <Stat label="DB queries" value={dbOverview.data.totalQueries.toLocaleString()} />
          <Stat label="DB errors"  value={dbOverview.data.errorQueries.toLocaleString()}
                sub={`${(dbOverview.data.errorRate * 100).toFixed(2)}% error rate`}
                accent={dbOverview.data.errorQueries > 0 ? 'err' : 'ok'} />
          <Stat label="DB p50" value={formatMs(dbOverview.data.latencyP50Ms)} />
          <Stat label="DB p90" value={formatMs(dbOverview.data.latencyP90Ms)} />
          <Stat label="DB p99" value={formatMs(dbOverview.data.latencyP99Ms)} />
          <Stat label="Total DB time"
                value={formatTotalMs(dbOverview.data.totalDbTimeMs)}
                sub={dbOverview.data.distinctSystems > 0
                  ? `${dbOverview.data.distinctSystems} system${dbOverview.data.distinctSystems > 1 ? 's' : ''}`
                  : undefined} />
        </div>
      )}

      {dbOverview.data && dbOverview.data.totalQueries === 0 && (
        <div className="card" style={{ marginBottom: 24 }}>
          <Empty label="No database queries captured in this time range. The Agent listens to Microsoft.Data.SqlClient, Npgsql, MySqlConnector, and EF Core — make sure your app actually queries a DB while running a captured endpoint." />
        </div>
      )}

      {dbSystems.data && dbSystems.data.length > 0 && (
        <div className="card" style={{ marginBottom: 24 }}>
          <h3 className="card-title">By database system</h3>
          <table>
            <thead>
              <tr>
                <th>System</th>
                <th style={{ textAlign: 'right' }}>Queries</th>
                <th style={{ textAlign: 'right' }}>p50</th>
                <th style={{ textAlign: 'right' }}>p99</th>
              </tr>
            </thead>
            <tbody>
              {dbSystems.data.map(s => (
                <tr key={s.dbSystem}>
                  <td><span className="badge muted">{s.dbSystem.toUpperCase()}</span></td>
                  <td style={{ textAlign: 'right' }}>{s.count.toLocaleString()}</td>
                  <td style={{ textAlign: 'right' }}>{formatMs(s.latencyP50Ms)}</td>
                  <td style={{ textAlign: 'right' }}>{formatMs(s.latencyP99Ms)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <div className="card">
        <h3 className="card-title">Top database queries</h3>
        {topQueries.isLoading ? <Loading /> : topQueries.error ? <ErrorBlock error={topQueries.error} /> :
          !topQueries.data || topQueries.data.length === 0 ? <Empty label="No database queries captured yet." /> : (
          <table>
            <thead>
              <tr>
                <th>System</th>
                <th>Statement</th>
                <th style={{ textAlign: 'right' }}>Calls</th>
                <th style={{ textAlign: 'right' }}>Errors</th>
                <th style={{ textAlign: 'right' }}>p50</th>
                <th style={{ textAlign: 'right' }}>p99</th>
                <th style={{ textAlign: 'right' }}>Total time</th>
              </tr>
            </thead>
            <tbody>
              {topQueries.data.map((q, i) => (
                <tr key={i}>
                  <td><span className="badge muted">{q.dbSystem.toUpperCase()}</span></td>
                  <td className="mono" style={{ fontSize: 12, wordBreak: 'break-word', maxWidth: 520 }} title={q.statement}>
                    {squashSql(q.statement)}
                  </td>
                  <td style={{ textAlign: 'right' }}>{q.count.toLocaleString()}</td>
                  <td style={{ textAlign: 'right', color: q.errorCount > 0 ? 'var(--err)' : undefined }}>{q.errorCount.toLocaleString()}</td>
                  <td style={{ textAlign: 'right' }}>{formatMs(q.latencyP50Ms)}</td>
                  <td style={{ textAlign: 'right' }}>{formatMs(q.latencyP99Ms)}</td>
                  <td style={{ textAlign: 'right' }}>{formatTotalMs(q.totalDbTimeMs)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </>
  );
}

function squashSql(sql: string): string {
  const c = (sql || '').replace(/\s+/g, ' ').trim();
  return c.length > 180 ? `${c.slice(0, 179)}…` : c;
}

function formatTotalMs(ms: number): string {
  if (!Number.isFinite(ms) || ms <= 0) return '—';
  if (ms < 1000)        return `${Math.round(ms)}ms`;
  if (ms < 60_000)      return `${(ms / 1000).toFixed(1)}s`;
  return `${(ms / 60_000).toFixed(1)}m`;
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

function ChartHint({ label = 'click anywhere on the chart to peek at that window’s traces' }: { label?: string }) {
  return (
    <span className="muted" style={{ fontSize: 11, fontWeight: 400, marginLeft: 8, textTransform: 'none', letterSpacing: 0 }}>
      {label}
    </span>
  );
}

function LatencyChart({ points, onDrill }: { points: TimeseriesPoint[]; onDrill: (timeMs: number) => void }) {
  const xs = points.map(p => new Date(p.bucketStartUtc).getTime());
  const option = {
    grid: { left: 50, right: 24, top: 28, bottom: 32 },
    legend: { top: 0 },
    tooltip: { trigger: 'axis' },
    xAxis: { type: 'time' },
    yAxis: { type: 'value', name: 'ms' },
    series: [
      { name: 'p50', type: 'line', smooth: true, showSymbol: false, data: xs.map((x, i) => [x, points[i]!.latencyP50Ms]), itemStyle: { color: '#34D399' }, areaStyle: { color: areaGradient('#34D399') } },
      { name: 'p90', type: 'line', smooth: true, showSymbol: false, data: xs.map((x, i) => [x, points[i]!.latencyP90Ms]), itemStyle: { color: '#F59E0B' }, areaStyle: { color: areaGradient('#F59E0B', 0.16) } },
      { name: 'p99', type: 'line', smooth: true, showSymbol: false, data: xs.map((x, i) => [x, points[i]!.latencyP99Ms]), itemStyle: { color: '#EF4444' }, areaStyle: { color: areaGradient('#EF4444', 0.10) } },
    ],
  } as const;
  return (
    <ReactECharts
      option={option}
      style={{ height: 280 }}
      theme="dark"
      onChartReady={(chart: ECharts) => onTimeClick(chart, onDrill)}
    />
  );
}

function VolumeChart({ points, onDrill }: { points: TimeseriesPoint[]; onDrill: (timeMs: number, errorsOnly?: boolean) => void }) {
  const xs = points.map(p => new Date(p.bucketStartUtc).getTime());
  const option = {
    grid: { left: 50, right: 24, top: 28, bottom: 32 },
    legend: { top: 0 },
    tooltip: { trigger: 'axis' },
    xAxis: { type: 'time' },
    yAxis: { type: 'value', name: 'requests' },
    series: [
      { name: 'Total', type: 'bar', stack: 'a', data: xs.map((x, i) => [x, points[i]!.count - points[i]!.errorCount]), itemStyle: { color: areaGradient('#8B7CFF', 0.95, 0.55) } },
      { name: 'Errors', type: 'bar', stack: 'a', data: xs.map((x, i) => [x, points[i]!.errorCount]), itemStyle: { color: '#EF4444' } },
    ],
  } as const;
  return (
    <ReactECharts
      option={option}
      style={{ height: 220 }}
      theme="dark"
      onEvents={{
        click: (params: { seriesName?: string; value?: [number, number] }) => {
          const t = params.value?.[0];
          if (typeof t === 'number') onDrill(t, params.seriesName === 'Errors');
        },
      }}
    />
  );
}

function pickBucket(from: Date, to: Date): number {
  const minutes = (to.getTime() - from.getTime()) / 60_000;
  if (minutes <= 30)        return 10;
  if (minutes <= 120)       return 60;
  if (minutes <= 6 * 60)    return 5 * 60;
  if (minutes <= 24 * 60)   return 15 * 60;
  return 60 * 60;
}

function formatMs(ms: number): string {
  if (!Number.isFinite(ms) || ms <= 0) return '—';
  if (ms < 1)  return `${(ms * 1000).toFixed(0)}µs`;
  if (ms < 10) return `${ms.toFixed(2)}ms`;
  return `${Math.round(ms)}ms`;
}
