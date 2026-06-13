import { useQuery } from '@tanstack/react-query';
import ReactECharts from 'echarts-for-react';
import { ArrowLeft } from 'lucide-react';
import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api, buildRangeQuery } from '../api/client';
import type { RouteDetail, StatusCodeBucket } from '../api/types';
import { bucketWindow } from '../charts/drill';
import { areaGradient } from '../charts/kamsoraTheme';
import { useTimeRange } from './TimeRangePicker';
import { Empty, ErrorBlock, Loading } from './Loading';
import { TraceListExplorer, windowLabel, type TraceFilter } from './TracePeek';
import { DataBar, formatMs, MethodBadge, RouteLabel, StatusPill } from './viz';

/**
 * Slide-in drill-down for one HTTP route: summary percentiles, request
 * volume over time, a log2 latency histogram, and the status-code
 * distribution. Opened from the Errors table and Overview's top routes.
 */
export function RouteDrillDrawer({
  service, route, onClose,
}: {
  service: string | null; route: string | null; onClose: () => void;
}) {
  const open = !!(service && route !== null);

  useEffect(() => {
    if (!open) return;
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose();
    }
    document.addEventListener('keydown', onKey);
    document.body.classList.add('drawer-open');
    return () => {
      document.removeEventListener('keydown', onKey);
      document.body.classList.remove('drawer-open');
    };
  }, [open, onClose]);

  if (!open) return null;

  return (
    <>
      <div className="drawer-backdrop" onClick={onClose} aria-hidden />
      <aside className="drawer" role="dialog" aria-modal="true" aria-label="Route detail">
        <header className="drawer-header">
          <h2 style={{ display: 'flex', alignItems: 'center', gap: 10, minWidth: 0 }}>
            Route detail
            <span style={{ fontWeight: 400, fontSize: 13, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              <RouteLabel route={route!} />
            </span>
          </h2>
          <div className="actions">
            <Link
              to={`/traces?service=${encodeURIComponent(service!)}&route=${encodeURIComponent(route!)}`}
              className="secondary"
              style={{
                fontSize: 12, padding: '6px 10px',
                border: '1px solid var(--border)', borderRadius: 'var(--radius-sm)',
                background: 'var(--bg-surface-2)', color: 'var(--text)', textDecoration: 'none',
              }}
              onClick={onClose}
            >
              View traces ↗
            </Link>
            <button className="drawer-close" onClick={onClose} title="Close (Esc)">×</button>
          </div>
        </header>
        <div className="drawer-body">
          <RouteDrillBody service={service!} route={route!} />
        </div>
      </aside>
    </>
  );
}

function RouteDrillBody({ service, route }: { service: string; route: string }) {
  const range = useTimeRange();
  const bucketSec = pickBucket(range.from, range.to);
  // Clicking a volume bar swaps the drawer body to that window's trace list.
  const [peek, setPeek] = useState<TraceFilter | null>(null);

  function drillToWindow(timeMs: number) {
    setPeek({ ...bucketWindow(timeMs, bucketSec), service, route, kind: 'SERVER' });
  }

  const detail = useQuery({
    queryKey: ['route-detail', service, route, range.presetKey],
    queryFn: () => api<RouteDetail>(
      `/v1/routes/detail?service=${encodeURIComponent(service)}&route=${encodeURIComponent(route)}&${buildRangeQuery(range.from, range.to, { bucketSeconds: bucketSec })}`),
  });

  const statuses = useQuery({
    queryKey: ['route-statuses', service, route, range.presetKey],
    queryFn: () => api<StatusCodeBucket[]>(
      `/v1/errors/routes/${encodeURIComponent(service)}/${encodeURIComponent(route || '_')}?${buildRangeQuery(range.from, range.to)}`),
  });

  if (peek) {
    return (
      <>
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 14, flexWrap: 'wrap' }}>
          <button className="secondary" onClick={() => setPeek(null)}
            style={{ display: 'inline-flex', alignItems: 'center', gap: 6, fontSize: 12 }}>
            <ArrowLeft size={13} /> Back to route summary
          </button>
          <span className="badge muted mono">{windowLabel(peek)}</span>
        </div>
        <TraceListExplorer filter={peek} />
      </>
    );
  }

  if (detail.isLoading) return <Loading shape="stats" />;
  if (detail.error)     return <ErrorBlock error={detail.error} />;
  if (!detail.data)     return <Empty label="No traffic for this route in the selected window." />;

  const d = detail.data;
  const statusMax = Math.max(1, ...(statuses.data ?? []).map(s => s.requestCount));

  return (
    <>
      <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 16, flexWrap: 'wrap' }}>
        <MethodBadge method={d.httpMethod} />
        <span style={{ fontSize: 15 }}><RouteLabel route={d.httpRoute} /></span>
        <span className="badge muted">{d.serviceName}</span>
      </div>

      <div className="stat-grid">
        <MiniStat label="Requests"    value={d.count.toLocaleString()} sub={`${d.requestsPerMinute.toFixed(2)} / min`} />
        <MiniStat label="Errors"      value={d.errorCount.toLocaleString()} sub={`${(d.errorRate * 100).toFixed(2)}% error rate`}
          accent={d.errorCount > 0 ? 'err' : 'ok'} />
        <MiniStat label="p50"         value={formatMs(d.latencyP50Ms)} />
        <MiniStat label="p75"         value={formatMs(d.latencyP75Ms)} />
        <MiniStat label="p95"         value={formatMs(d.latencyP95Ms)} />
        <MiniStat label="p99"         value={formatMs(d.latencyP99Ms)} />
      </div>

      <div className="card" style={{ marginBottom: 16 }}>
        <h3 className="card-title">
          Requests over time
          <span className="muted" style={{ fontSize: 11, fontWeight: 400, marginLeft: 8, textTransform: 'none', letterSpacing: 0 }}>
            click a bar to peek at that window's traces
          </span>
        </h3>
        {d.timeseries.length === 0 ? <Empty /> : <RouteVolumeChart detail={d} onDrill={drillToWindow} />}
      </div>

      <div className="card" style={{ marginBottom: 16 }}>
        <h3 className="card-title">Response time distribution</h3>
        {d.histogram.length === 0 ? <Empty /> : <HistogramChart detail={d} />}
      </div>

      <div className="card">
        <h3 className="card-title">Status codes</h3>
        {statuses.isLoading ? <Loading /> :
         statuses.error    ? <ErrorBlock error={statuses.error} /> :
         (statuses.data?.length ?? 0) === 0 ? <Empty /> : (
          <table>
            <thead>
              <tr>
                <th>Status</th>
                <th style={{ textAlign: 'right', width: 180 }}>Requests</th>
                <th style={{ textAlign: 'right' }}>Share</th>
                <th style={{ textAlign: 'right' }}>p50</th>
                <th style={{ textAlign: 'right' }}>p99</th>
              </tr>
            </thead>
            <tbody>
              {statuses.data!.map(b => (
                <tr key={b.httpStatusCode}>
                  <td><StatusPill httpStatus={b.httpStatusCode} /></td>
                  <td style={{ textAlign: 'right' }}>
                    <DataBar value={b.requestCount} max={statusMax}
                      color={b.httpStatusCode >= 500 ? 'rgba(239,68,68,0.25)'
                           : b.httpStatusCode >= 400 ? 'rgba(245,158,11,0.22)'
                           : 'rgba(52,211,153,0.18)'} />
                  </td>
                  <td style={{ textAlign: 'right' }}>
                    {d.count > 0 ? `${((b.requestCount / d.count) * 100).toFixed(1)}%` : '-'}
                  </td>
                  <td style={{ textAlign: 'right' }}>{formatMs(b.latencyP50Ms)}</td>
                  <td style={{ textAlign: 'right' }}>{formatMs(b.latencyP99Ms)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </>
  );
}

function RouteVolumeChart({ detail, onDrill }: { detail: RouteDetail; onDrill: (timeMs: number) => void }) {
  const xs = detail.timeseries.map(p => new Date(p.bucketStartUtc).getTime());
  const option = {
    grid: { left: 48, right: 16, top: 28, bottom: 28 },
    legend: { top: 0 },
    tooltip: { trigger: 'axis' },
    xAxis: { type: 'time' },
    yAxis: { type: 'value' },
    series: [
      { name: 'Requests', type: 'bar', stack: 'a', data: xs.map((x, i) => [x, detail.timeseries[i]!.count - detail.timeseries[i]!.errorCount]), itemStyle: { color: areaGradient('#8B7CFF', 0.95, 0.55) } },
      { name: 'Errors',   type: 'bar', stack: 'a', data: xs.map((x, i) => [x, detail.timeseries[i]!.errorCount]), itemStyle: { color: '#EF4444' } },
    ],
  };
  return (
    <ReactECharts
      option={option}
      style={{ height: 200 }}
      theme="dark"
      notMerge
      onEvents={{
        click: (params: { value?: [number, number] }) => {
          const t = params.value?.[0];
          if (typeof t === 'number') onDrill(t);
        },
      }}
    />
  );
}

function HistogramChart({ detail }: { detail: RouteDetail }) {
  const labels = detail.histogram.map(b => formatBucket(b.fromMs));
  const option = {
    grid: { left: 48, right: 16, top: 12, bottom: 28 },
    tooltip: {
      trigger: 'axis',
      formatter: (ps: Array<{ dataIndex: number; value: number }>) => {
        const p = ps[0]!;
        const b = detail.histogram[p.dataIndex]!;
        return `${formatBucket(b.fromMs)} - ${formatBucket(b.toMs)}<br/>requests: ${b.count.toLocaleString()}`;
      },
    },
    xAxis: { type: 'category', data: labels },
    yAxis: { type: 'value' },
    series: [{
      type: 'bar',
      data: detail.histogram.map(b => b.count),
      itemStyle: { color: areaGradient('#4FA3FF', 0.95, 0.45) },
      barCategoryGap: '18%',
    }],
  };
  return <ReactECharts option={option} style={{ height: 180 }} theme="dark" notMerge />;
}

function formatBucket(ms: number): string {
  if (ms < 1)     return `${(ms * 1000).toFixed(0)}µs`;
  if (ms < 1000)  return `${Math.round(ms)}ms`;
  return `${(ms / 1000).toFixed(ms >= 10_000 ? 0 : 1)}s`;
}

function MiniStat({ label, value, sub, accent }: { label: string; value: string; sub?: string; accent?: 'ok' | 'err' | 'warn' }) {
  return (
    <div className="card" style={{ padding: 16 }}>
      <h3 className="card-title">{label}</h3>
      <div className="stat-value" style={{ fontSize: 22, ...(accent ? { color: `var(--${accent})` } : {}) }}>{value}</div>
      {sub && <div className="stat-sub">{sub}</div>}
    </div>
  );
}

function pickBucket(from: Date, to: Date): number {
  const minutes = (to.getTime() - from.getTime()) / 60_000;
  if (minutes <= 30)      return 60;
  if (minutes <= 120)     return 120;
  if (minutes <= 6 * 60)  return 5 * 60;
  if (minutes <= 24 * 60) return 15 * 60;
  return 60 * 60;
}
