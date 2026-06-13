import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { api, buildRangeQuery } from '../api/client';
import type { ConsumerSummary, EntitySparkline } from '../api/types';
import { Empty, ErrorBlock, Loading } from '../components/Loading';
import { TimeRangePicker, useTimeRange } from '../components/TimeRangePicker';
import { DataBar, Sparkline, TimeAgo } from '../components/viz';

type SortKey = 'req' | 'err' | 'errRate' | 'p99';

/**
 * Per-consumer analytics list. The "consumer" is whatever the Agent's
 * IConsumerExtractor produced — JWT sub claim by default, with client-IP
 * fallback. Click a row to drill into per-route + timeseries.
 */
export default function ConsumersPage() {
  const range = useTimeRange();
  const [sort, setSort] = useState<SortKey>('req');
  const [search, setSearch] = useState('');

  const consumers = useQuery({
    queryKey: ['consumers', range.presetKey],
    queryFn: () => api<ConsumerSummary[]>(`/v1/consumers/?${buildRangeQuery(range.from, range.to, { limit: 200 })}`),
    placeholderData: keepPreviousData,
    refetchInterval: 30_000,
  });

  const sparklines = useQuery({
    queryKey: ['consumers-sparklines', range.presetKey],
    queryFn: () => api<EntitySparkline[]>(`/v1/consumers/sparklines?${buildRangeQuery(range.from, range.to, { buckets: 30, limit: 200 })}`),
    placeholderData: keepPreviousData,
    refetchInterval: 30_000,
  });

  const sparkByConsumer = useMemo(() => {
    const m = new Map<string, EntitySparkline>();
    sparklines.data?.forEach(s => m.set(s.key, s));
    return m;
  }, [sparklines.data]);

  const sorted = useMemo(() => {
    const list = consumers.data ?? [];
    const filtered = search.trim()
      ? list.filter(c => c.consumerId.toLowerCase().includes(search.trim().toLowerCase()))
      : list;
    const cmp = (a: ConsumerSummary, b: ConsumerSummary) => {
      switch (sort) {
        case 'err':     return b.errorCount    - a.errorCount;
        case 'errRate': return b.errorRate     - a.errorRate;
        case 'p99':     return b.latencyP99Ms  - a.latencyP99Ms;
        case 'req':
        default:        return b.requestCount  - a.requestCount;
      }
    };
    return [...filtered].sort(cmp);
  }, [consumers.data, sort, search]);

  const maxRequests = useMemo(
    () => Math.max(1, ...sorted.map(c => c.requestCount)),
    [sorted]);

  const totals = useMemo(() => {
    const list = consumers.data ?? [];
    return {
      consumers: list.length,
      requests:  list.reduce((s, c) => s + c.requestCount, 0),
      errors:    list.reduce((s, c) => s + c.errorCount,   0),
      anonShare: (() => {
        const anon = list.find(c => c.consumerId === '(anonymous)');
        const total = list.reduce((s, c) => s + c.requestCount, 0);
        return total > 0 && anon ? anon.requestCount / total : 0;
      })(),
    };
  }, [consumers.data]);

  return (
    <>
      <h1 className="page-title">
        Consumers
        <TimeRangePicker />
      </h1>

      <div className="stat-grid">
        <Stat label="Distinct consumers" value={totals.consumers.toLocaleString()} />
        <Stat label="Requests"           value={totals.requests.toLocaleString()} />
        <Stat label="Errors"             value={totals.errors.toLocaleString()}
              sub={totals.requests > 0 ? `${((totals.errors / totals.requests) * 100).toFixed(2)}% error rate` : undefined}
              accent={totals.errors > 0 ? 'err' : 'ok'} />
        <Stat label="Anonymous share"    value={`${(totals.anonShare * 100).toFixed(1)}%`}
              sub="Requests without an identifiable consumer" />
      </div>

      <div className="card" style={{ marginBottom: 12 }}>
        <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap', alignItems: 'flex-end' }}>
          <label style={{ display: 'grid', gap: 4, flex: 1, minWidth: 240 }}>
            <span className="muted" style={{ fontSize: 12 }}>Search consumer</span>
            <input value={search} onChange={e => setSearch(e.target.value)}
              placeholder="JWT sub, IP, header value…" style={{ padding: '6px 10px' }} />
          </label>
          <label style={{ display: 'grid', gap: 4 }}>
            <span className="muted" style={{ fontSize: 12 }}>Sort by</span>
            <select value={sort} onChange={e => setSort(e.target.value as SortKey)}>
              <option value="req">Request count</option>
              <option value="err">Error count</option>
              <option value="errRate">Error rate</option>
              <option value="p99">p99 latency</option>
            </select>
          </label>
        </div>
      </div>

      <div className="card" style={{ padding: 0 }}>
        {consumers.isLoading ? <Loading /> :
         consumers.error    ? <ErrorBlock error={consumers.error} /> :
         sorted.length === 0 ? <Empty label="No consumer traffic captured in this time range. Make sure the Agent's ConsumerExtractor is enabled — see README." /> : (
          <table>
            <thead>
              <tr>
                <th>Consumer</th>
                <th>Activity</th>
                <th style={{ textAlign: 'right', width: 150 }}>Requests</th>
                <th style={{ textAlign: 'right' }}>4xx</th>
                <th style={{ textAlign: 'right' }}>5xx</th>
                <th style={{ textAlign: 'right' }}>Error rate</th>
                <th style={{ textAlign: 'right' }}>p50</th>
                <th style={{ textAlign: 'right' }}>p99</th>
                <th style={{ textAlign: 'right' }}>Routes</th>
                <th>Last seen</th>
              </tr>
            </thead>
            <tbody>
              {sorted.map(c => {
                const spark = sparkByConsumer.get(c.consumerId);
                return (
                <tr key={c.consumerId}>
                  <td>
                    <Link to={`/consumers/${encodeURIComponent(c.consumerId)}`} className="mono"
                      style={{ wordBreak: 'break-all' }} title={c.consumerId}>
                      {c.consumerId === '(anonymous)'
                        ? <span className="badge warn">anonymous</span>
                        : truncateMid(c.consumerId, 32)}
                    </Link>
                  </td>
                  <td>
                    {spark
                      ? <Sparkline counts={spark.counts} errors={spark.errors} width={110} />
                      : <span className="faint">—</span>}
                  </td>
                  <td style={{ textAlign: 'right' }}>
                    <DataBar value={c.requestCount} max={maxRequests} />
                  </td>
                  <td style={{ textAlign: 'right', color: c.clientErrorCount > 0 ? 'var(--warn)' : undefined }}>{c.clientErrorCount.toLocaleString()}</td>
                  <td style={{ textAlign: 'right', color: c.serverErrorCount > 0 ? 'var(--err)'  : undefined }}>{c.serverErrorCount.toLocaleString()}</td>
                  <td style={{ textAlign: 'right' }}>{(c.errorRate * 100).toFixed(2)}%</td>
                  <td style={{ textAlign: 'right' }}>{formatMs(c.latencyP50Ms)}</td>
                  <td style={{ textAlign: 'right' }}>{formatMs(c.latencyP99Ms)}</td>
                  <td style={{ textAlign: 'right' }}>{c.distinctRoutes}</td>
                  <td><TimeAgo iso={c.lastSeenUtc} /></td>
                </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </div>
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

function truncateMid(s: string, max: number): string {
  if (s.length <= max) return s;
  const half = Math.floor((max - 1) / 2);
  return `${s.slice(0, half)}…${s.slice(s.length - half)}`;
}

function formatMs(ms: number): string {
  if (!Number.isFinite(ms) || ms <= 0) return '—';
  if (ms < 10) return `${ms.toFixed(2)}ms`;
  return `${Math.round(ms)}ms`;
}

