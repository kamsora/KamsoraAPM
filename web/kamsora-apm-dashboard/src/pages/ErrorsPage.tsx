import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { api, buildRangeQuery } from '../api/client';
import type { RouteStatusSummary } from '../api/types';
import { Empty, ErrorBlock, Loading } from '../components/Loading';
import { RouteDrillDrawer } from '../components/RouteDrillDrawer';
import { TimeRangePicker, useTimeRange } from '../components/TimeRangePicker';
import { DataBar, RouteLabel } from '../components/viz';

/**
 * 4xx/5xx breakdown across all routes. Default sort = worst-offender first
 * (most 4xx+5xx). Click a row to open the full route drill-down (timeseries,
 * latency histogram, status distribution).
 */
export default function ErrorsPage() {
  const range = useTimeRange();
  const [drill, setDrill] = useState<RouteStatusSummary | null>(null);

  const routes = useQuery({
    queryKey: ['errors-routes', range.presetKey],
    queryFn: () => api<RouteStatusSummary[]>(`/v1/errors/routes?${buildRangeQuery(range.from, range.to, { limit: 100 })}`),
    placeholderData: keepPreviousData,
    refetchInterval: 30_000,
  });

  const totals = useMemo(() => {
    const list = routes.data ?? [];
    return {
      requests: list.reduce((s, r) => s + r.requestCount, 0),
      s4xx:     list.reduce((s, r) => s + r.status4xx,    0),
      s5xx:     list.reduce((s, r) => s + r.status5xx,    0),
      worst:    list[0],
    };
  }, [routes.data]);

  const errorRate = totals.requests > 0 ? (totals.s4xx + totals.s5xx) / totals.requests : 0;

  return (
    <>
      <h1 className="page-title">
        Errors
        <TimeRangePicker />
      </h1>

      <div className="stat-grid">
        <Stat label="Total requests" value={totals.requests.toLocaleString()} />
        <Stat label="4xx" value={totals.s4xx.toLocaleString()} accent={totals.s4xx > 0 ? 'warn' : 'ok'} />
        <Stat label="5xx" value={totals.s5xx.toLocaleString()} accent={totals.s5xx > 0 ? 'err'  : 'ok'} />
        <Stat label="Error rate" value={`${(errorRate * 100).toFixed(2)}%`}
              sub={totals.worst ? `worst: ${totals.worst.httpRoute || totals.worst.serviceName}` : undefined} />
      </div>

      <div className="card" style={{ padding: 0 }}>
        <h3 className="card-title" style={{ padding: '12px 16px 0' }}>
          Routes by error volume
          <span className="muted" style={{ fontSize: 12, fontWeight: 400, marginLeft: 8 }}>click a row for the full route drill-down</span>
        </h3>
        {routes.isLoading ? <Loading /> :
         routes.error    ? <ErrorBlock error={routes.error} /> :
         (routes.data?.length ?? 0) === 0 ? <Empty label="No traffic captured in this time range yet." /> : (
          <table>
            <thead>
              <tr>
                <th>Service</th>
                <th>Route</th>
                <th style={{ textAlign: 'right' }}>2xx</th>
                <th style={{ textAlign: 'right' }}>3xx</th>
                <th style={{ textAlign: 'right', width: 120 }}>4xx</th>
                <th style={{ textAlign: 'right', width: 120 }}>5xx</th>
                <th style={{ textAlign: 'right' }}>Error rate</th>
                <th style={{ textAlign: 'right' }}>p50</th>
                <th style={{ textAlign: 'right' }}>p99</th>
              </tr>
            </thead>
            <tbody>
              {(() => {
                const max4xx = Math.max(1, ...routes.data!.map(r => r.status4xx));
                const max5xx = Math.max(1, ...routes.data!.map(r => r.status5xx));
                return routes.data!.map((r, i) => (
                  <tr key={i} className="clickable" onClick={() => setDrill(r)}>
                    <td>{r.serviceName}</td>
                    <td><RouteLabel route={r.httpRoute || ''} /></td>
                    <td style={{ textAlign: 'right' }} className="faint">{r.status2xx.toLocaleString()}</td>
                    <td style={{ textAlign: 'right' }} className="faint">{r.status3xx.toLocaleString()}</td>
                    <td style={{ textAlign: 'right' }}>
                      {r.status4xx > 0
                        ? <DataBar value={r.status4xx} max={max4xx} color="rgba(245, 158, 11, 0.25)" />
                        : <span className="faint">0</span>}
                    </td>
                    <td style={{ textAlign: 'right' }}>
                      {r.status5xx > 0
                        ? <DataBar value={r.status5xx} max={max5xx} color="rgba(239, 68, 68, 0.28)" />
                        : <span className="faint">0</span>}
                    </td>
                    <td style={{ textAlign: 'right' }}>{(r.errorRate * 100).toFixed(2)}%</td>
                    <td style={{ textAlign: 'right' }}>{formatMs(r.latencyP50Ms)}</td>
                    <td style={{ textAlign: 'right' }}>{formatMs(r.latencyP99Ms)}</td>
                  </tr>
                ));
              })()}
            </tbody>
          </table>
        )}
      </div>

      <RouteDrillDrawer
        service={drill?.serviceName ?? null}
        route={drill ? drill.httpRoute : null}
        onClose={() => setDrill(null)} />
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

function formatMs(ms: number): string {
  if (!Number.isFinite(ms) || ms <= 0) return '—';
  if (ms < 10) return `${ms.toFixed(2)}ms`;
  return `${Math.round(ms)}ms`;
}
