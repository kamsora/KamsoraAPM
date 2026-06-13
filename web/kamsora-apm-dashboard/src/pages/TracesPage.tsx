import { useQuery } from '@tanstack/react-query';
import ReactECharts from 'echarts-for-react';
import { Check, Copy, Radio, Search } from 'lucide-react';
import { useEffect, useMemo, useRef, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import type { ServiceSummary, SpanRowDto, TraceListResponse } from '../api/types';
import { areaGradient } from '../charts/kamsoraTheme';
import { ErrorBlock, Loading, Empty } from '../components/Loading';
import { OperationLabel } from '../components/OperationLabel';
import { TraceDrawer } from '../components/TraceDrawer';
import { DurationBar, formatMs, StatusPill, TimeAgo } from '../components/viz';

export default function TracesPage() {
  // URL query params let other pages deep-link into pre-filtered trace lists
  // (e.g. Consumer detail → "show me 5xx for this consumer between 14:00-15:00").
  const [searchParams, setSearchParams] = useSearchParams();
  const consumerId  = searchParams.get('consumerId') ?? '';
  const routeParam  = searchParams.get('route')      ?? '';
  const fromUtcParam = searchParams.get('fromUtc')   ?? '';
  const toUtcParam   = searchParams.get('toUtc')     ?? '';
  const errorsOnly  = searchParams.get('errorsOnly') === 'true';

  const [service, setService] = useState<string>(searchParams.get('service') ?? '');
  const [kindFilter, setKindFilter] = useState<string>(searchParams.get('kind') ?? 'SERVER');
  const [openTraceId, setOpenTraceId] = useState<string | null>(null);
  const [limit, setLimit] = useState<number>(Number(searchParams.get('limit')) || 100);
  const [hidePreflight, setHidePreflight] = useState(true);
  const [search, setSearch] = useState('');
  const [showErrorsOnly, setShowErrorsOnly] = useState(false);
  const [liveTail, setLiveTail] = useState(false);

  const services = useQuery({
    queryKey: ['services-for-filter'],
    queryFn: () => api<ServiceSummary[]>('/v1/services'),
  });

  const traces = useQuery({
    queryKey: ['traces', service, kindFilter, limit, consumerId, routeParam, fromUtcParam, toUtcParam, errorsOnly],
    queryFn: () => {
      const params = new URLSearchParams();
      params.set('limit', String(limit));
      if (service)        params.set('service',    service);
      if (kindFilter)     params.set('kind',       kindFilter);
      if (consumerId)     params.set('consumerId', consumerId);
      if (routeParam)     params.set('route',      routeParam);
      if (fromUtcParam)   params.set('fromUtc',    fromUtcParam);
      if (toUtcParam)     params.set('toUtc',      toUtcParam);
      if (errorsOnly)     params.set('errorsOnly', 'true');
      return api<TraceListResponse>(`/v1/traces?${params.toString()}`);
    },
    refetchInterval: liveTail ? 5_000 : false,
  });

  // Rows that weren't in the previous load get a one-shot flash while live
  // tail is on. The ref survives re-renders without retriggering them.
  const seenKeysRef = useRef<Set<string>>(new Set());
  const [newKeys, setNewKeys] = useState<Set<string>>(new Set());
  useEffect(() => {
    const items = traces.data?.items ?? [];
    const keys = items.map(r => `${r.traceId}-${r.spanId}`);
    if (liveTail && seenKeysRef.current.size > 0) {
      const fresh = new Set(keys.filter(k => !seenKeysRef.current.has(k)));
      setNewKeys(fresh);
    } else {
      setNewKeys(new Set());
    }
    seenKeysRef.current = new Set(keys);
  }, [traces.data, liveTail]);

  function clearDrillFilters() {
    const next = new URLSearchParams();
    if (service)    next.set('service', service);
    if (kindFilter) next.set('kind',    kindFilter);
    setSearchParams(next);
  }

  // Server-side filter does the heavy lifting; preflight/errors-only/search
  // act on the loaded sample client-side so every toggle is instant.
  const allItems = useMemo(() => traces.data?.items ?? [], [traces.data]);
  const filteredItems = useMemo(() => {
    let rows = allItems;
    if (hidePreflight) rows = rows.filter(r => (r.httpMethod || '').toUpperCase() !== 'OPTIONS');
    if (showErrorsOnly) rows = rows.filter(r => r.statusCode === 'ERROR' || r.httpStatusCode >= 400);
    const q = search.trim().toLowerCase();
    if (q) {
      rows = rows.filter(r =>
        (r.httpRoute || '').toLowerCase().includes(q) ||
        (r.spanName || '').toLowerCase().includes(q) ||
        (r.httpUrl || '').toLowerCase().includes(q) ||
        (r.serviceName || '').toLowerCase().includes(q) ||
        (r.consumerId || '').toLowerCase().includes(q) ||
        (r.dbStatement || '').toLowerCase().includes(q));
    }
    return rows;
  }, [allItems, hidePreflight, showErrorsOnly, search]);

  const totalCount = filteredItems.length;
  const allCount = allItems.length;

  // KPIs + the volume strip describe the loaded sample (up to `limit` spans).
  const kpis = useMemo(() => {
    if (filteredItems.length === 0) return null;
    const durations = filteredItems.map(r => r.durationNanos / 1_000_000).sort((a, b) => a - b);
    const errors = filteredItems.filter(r => r.statusCode === 'ERROR' || r.httpStatusCode >= 500).length;
    const pick = (q: number) => durations[Math.min(durations.length - 1, Math.floor(q * durations.length))]!;
    return {
      count: filteredItems.length,
      errors,
      errorRate: errors / filteredItems.length,
      p50: pick(0.5),
      p95: pick(0.95),
      slowest: durations[durations.length - 1]!,
    };
  }, [filteredItems]);

  const hasDrillFilters = !!(consumerId || routeParam || fromUtcParam || toUtcParam || errorsOnly);

  return (
    <>
      <h1 className="page-title">
        Traces
        <span className="badge muted">{totalCount} of {allCount}</span>
      </h1>

      {hasDrillFilters && (
        <div className="card" style={{ marginBottom: 12, padding: '10px 14px', display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
          <span className="muted" style={{ fontSize: 12 }}>Drill-through filters:</span>
          {consumerId && (
            <span className="badge muted mono" title={consumerId}>
              consumer = {consumerId === '(anonymous)' ? 'anonymous' : truncMid(consumerId, 24)}
            </span>
          )}
          {routeParam && <span className="badge muted mono">route = {routeParam}</span>}
          {errorsOnly && <span className="badge err">errors only (4xx + 5xx)</span>}
          {fromUtcParam && toUtcParam && (
            <span className="badge muted mono">
              {new Date(fromUtcParam).toISOString().slice(11, 16)} → {new Date(toUtcParam).toISOString().slice(11, 16)} UTC
            </span>
          )}
          <button className="secondary" onClick={clearDrillFilters} style={{ fontSize: 11, padding: '2px 10px' }}>
            Clear
          </button>
          {consumerId && (
            <Link to={`/consumers/${encodeURIComponent(consumerId)}`} style={{ fontSize: 12, marginLeft: 'auto' }}>
              ← back to consumer detail
            </Link>
          )}
        </div>
      )}

      {kpis && (
        <div className="stat-grid">
          <Stat label="Spans loaded" value={kpis.count.toLocaleString()} sub={`latest ${limit} matching the filter`} />
          <Stat label="Errors" value={kpis.errors.toLocaleString()}
                sub={`${(kpis.errorRate * 100).toFixed(2)}% of loaded`}
                accent={kpis.errors > 0 ? 'err' : 'ok'} />
          <Stat label="p50" value={formatMs(kpis.p50)} />
          <Stat label="p95" value={formatMs(kpis.p95)} />
          <Stat label="Slowest" value={formatMs(kpis.slowest)} />
        </div>
      )}

      {filteredItems.length > 1 && (
        <div className="card" style={{ marginBottom: 16 }}>
          <h3 className="card-title">
            Volume of loaded spans
            <span className="muted" style={{ fontSize: 11, fontWeight: 400, marginLeft: 8, textTransform: 'none', letterSpacing: 0 }}>
              click a bar to zoom into that window
            </span>
          </h3>
          <VolumeStrip rows={filteredItems} onDrill={(fromMs, toMs) => {
            const next = new URLSearchParams(searchParams);
            next.set('fromUtc', new Date(fromMs).toISOString());
            next.set('toUtc',   new Date(toMs).toISOString());
            setSearchParams(next);
          }} />
        </div>
      )}

      <div className="toolbar">
        <label htmlFor="kind" className="muted">Type</label>
        <select id="kind" value={kindFilter} onChange={e => setKindFilter(e.target.value)}>
          <option value="">All</option>
          <option value="SERVER">SERVER (API requests)</option>
          <option value="CLIENT">CLIENT (outbound HTTP/DB)</option>
          <option value="INTERNAL">INTERNAL (custom)</option>
          <option value="PRODUCER">PRODUCER (messaging)</option>
          <option value="CONSUMER">CONSUMER (messaging)</option>
        </select>

        <label htmlFor="svc" className="muted">Service</label>
        <select id="svc" value={service} onChange={e => setService(e.target.value)}>
          <option value="">All</option>
          {services.data?.map(s => (
            <option key={s.serviceName} value={s.serviceName}>{s.serviceName}</option>
          ))}
        </select>

        <label htmlFor="lim" className="muted">Limit</label>
        <select id="lim" value={limit} onChange={e => setLimit(Number(e.target.value))}>
          {[50, 100, 250, 500, 1000].map(n => <option key={n} value={n}>{n}</option>)}
        </select>

        <div style={{ position: 'relative', flex: 1, minWidth: 200, maxWidth: 380 }}>
          <Search size={14} style={{ position: 'absolute', left: 10, top: '50%', transform: 'translateY(-50%)', color: 'var(--text-faint)', pointerEvents: 'none' }} />
          <input
            value={search}
            onChange={e => setSearch(e.target.value)}
            placeholder="Filter by route, consumer, SQL…"
            style={{ width: '100%', paddingLeft: 30 }}
          />
        </div>

        <button type="button" className={`chip-toggle${hidePreflight ? ' on' : ''}`}
          onClick={() => setHidePreflight(v => !v)} title="Drop OPTIONS preflight spans from the list">
          Hide OPTIONS
        </button>
        <button type="button" className={`chip-toggle danger${showErrorsOnly ? ' on' : ''}`}
          onClick={() => setShowErrorsOnly(v => !v)} title="Only 4xx, 5xx, and ERROR spans">
          Errors only
        </button>
        <button type="button" className={`chip-toggle${liveTail ? ' on' : ''}`}
          onClick={() => setLiveTail(v => !v)} title="Refresh every 5s; new spans flash">
          <Radio size={13} />
          {liveTail ? 'Live' : 'Live tail'}
        </button>

        <button onClick={() => traces.refetch()} className="secondary">Refresh</button>
      </div>

      <div className="card" style={{ padding: 0, overflow: 'hidden' }}>
        {traces.isLoading ? <Loading /> :
         traces.error    ? <ErrorBlock error={traces.error} /> :
         totalCount === 0 ? <Empty label="No spans match the current filter. Try changing 'Type' to All, or hit an API endpoint via Swagger Execute." /> :
         <TraceTable rows={filteredItems} onOpen={setOpenTraceId} newKeys={newKeys} />}
      </div>

      <TraceDrawer traceId={openTraceId} onClose={() => setOpenTraceId(null)} />
    </>
  );
}

/**
 * Mini bar strip of the loaded spans bucketed over their own time span -
 * shows the shape of the sample (bursts, gaps) at a glance.
 */
function VolumeStrip({ rows, onDrill }: { rows: SpanRowDto[]; onDrill: (fromMs: number, toMs: number) => void }) {
  const times = rows.map(r => Number(BigInt(r.startTimeUnixNano) / 1000000n));
  const min = Math.min(...times);
  const max = Math.max(...times);
  const buckets = 60;
  const width = Math.max(1, max - min);
  const bucketMs = width / buckets;
  const counts = new Array<number>(buckets).fill(0);
  const errors = new Array<number>(buckets).fill(0);
  rows.forEach((r, i) => {
    const idx = Math.min(buckets - 1, Math.floor(((times[i]! - min) / width) * buckets));
    counts[idx]!++;
    if (r.statusCode === 'ERROR' || r.httpStatusCode >= 500) errors[idx]!++;
  });
  const xs = counts.map((_, i) => new Date(min + (i + 0.5) * bucketMs));
  const option = {
    grid: { left: 40, right: 12, top: 8, bottom: 24 },
    tooltip: { trigger: 'axis' },
    xAxis: { type: 'category', data: xs.map(d => d.toISOString().slice(11, 19)) },
    yAxis: { type: 'value' },
    series: [
      { name: 'Spans',  type: 'bar', stack: 'a', data: counts.map((c, i) => c - errors[i]!), itemStyle: { color: areaGradient('#8B7CFF', 0.9, 0.5) } },
      { name: 'Errors', type: 'bar', stack: 'a', data: errors, itemStyle: { color: '#EF4444' } },
    ],
  };
  return (
    <ReactECharts
      option={option}
      style={{ height: 110 }}
      theme="dark"
      notMerge
      onEvents={{
        click: (params: { dataIndex?: number }) => {
          if (typeof params.dataIndex !== 'number') return;
          const start = min + params.dataIndex * bucketMs;
          onDrill(Math.floor(start), Math.ceil(start + bucketMs));
        },
      }}
    />
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

function TraceTable({ rows, onOpen, newKeys }: {
  rows: SpanRowDto[]; onOpen: (traceId: string) => void; newKeys: Set<string>;
}) {
  const maxMs = Math.max(1, ...rows.map(r => r.durationNanos / 1_000_000));
  return (
    <table>
      <thead>
        <tr>
          <th>When</th>
          <th>Service</th>
          <th>Operation</th>
          <th>Consumer</th>
          <th>Status</th>
          <th style={{ textAlign: 'right', width: 130 }}>Duration</th>
          <th>Trace</th>
        </tr>
      </thead>
      <tbody>
        {rows.map(r => {
          const key = `${r.traceId}-${r.spanId}`;
          const whenIso = new Date(Number(BigInt(r.startTimeUnixNano) / 1000000n)).toISOString();
          const durationMs = r.durationNanos / 1_000_000;
          return (
            <tr
              key={key}
              className={`clickable${newKeys.has(key) ? ' row-new' : ''}`}
              onClick={() => onOpen(r.traceId)}
              title="Click to view trace detail"
            >
              <td className="mono" style={{ whiteSpace: 'nowrap' }}><TimeAgo iso={whenIso} /></td>
              <td>{r.serviceName}</td>
              <td className="mono" style={{ wordBreak: 'break-all', maxWidth: 520 }}>
                <OperationLabel span={r} />
              </td>
              <td><ConsumerChip consumerId={r.consumerId} /></td>
              <td><StatusPill httpStatus={r.httpStatusCode} statusCode={r.statusCode} /></td>
              <td style={{ textAlign: 'right' }}><DurationBar ms={durationMs} maxMs={maxMs} /></td>
              <td className="mono faint" style={{ whiteSpace: 'nowrap' }}>
                {r.traceId.slice(0, 12)}…
                <CopyButton text={r.traceId} title="Copy full trace id" />
              </td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}

function ConsumerChip({ consumerId }: { consumerId: string }) {
  if (!consumerId) return <span className="faint">-</span>;
  return (
    <Link
      to={`/consumers/${encodeURIComponent(consumerId)}`}
      onClick={e => e.stopPropagation()}
      className="badge muted mono"
      style={{ textDecoration: 'none', maxWidth: 160, overflow: 'hidden', textOverflow: 'ellipsis', display: 'inline-block', verticalAlign: 'middle' }}
      title={consumerId}
    >
      {consumerId}
    </Link>
  );
}

function CopyButton({ text, title }: { text: string; title: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <button
      type="button"
      className="icon-btn"
      style={{ marginLeft: 4, verticalAlign: 'middle' }}
      title={title}
      onClick={e => {
        e.stopPropagation();
        void navigator.clipboard.writeText(text).then(() => {
          setCopied(true);
          window.setTimeout(() => setCopied(false), 1200);
        });
      }}
    >
      {copied ? <Check size={13} style={{ color: 'var(--ok)' }} /> : <Copy size={13} />}
    </button>
  );
}

function truncMid(s: string, max: number): string {
  if (s.length <= max) return s;
  const half = Math.floor((max - 1) / 2);
  return `${s.slice(0, half)}…${s.slice(s.length - half)}`;
}
