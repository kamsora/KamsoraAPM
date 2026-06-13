import { keepPreviousData, useQuery } from '@tanstack/react-query';
import ReactECharts from 'echarts-for-react';
import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { api, buildRangeQuery } from '../api/client';
import type { LogListResponse, LogRowDto, LogVolumePoint, ServiceSummary } from '../api/types';
import { Empty, ErrorBlock, Loading } from '../components/Loading';
import { TimeRangePicker, useTimeRange } from '../components/TimeRangePicker';

const SEVERITY_COLORS: Record<string, string> = {
  TRACE: '#94A1BC',
  DEBUG: '#5B89FF',
  INFO:  '#34D399',
  WARN:  '#F59E0B',
  ERROR: '#EF4444',
  FATAL: '#B91C1C',
};

const MIN_SEVERITY_OPTIONS = [
  { label: 'All', value: 0 },
  { label: '≥ DEBUG', value: 5 },
  { label: '≥ INFO',  value: 9 },
  { label: '≥ WARN',  value: 13 },
  { label: '≥ ERROR', value: 17 },
  { label: '≥ FATAL', value: 21 },
];

/** severityText → minSeverity number for the API's floor filter. */
const SEVERITY_FLOOR: Record<string, number> = {
  TRACE: 1, DEBUG: 5, INFO: 9, WARN: 13, ERROR: 17, FATAL: 21,
};

export default function LogsPage() {
  const range = useTimeRange();
  const [service, setService]     = useState('');
  const [minSeverity, setMinSev]  = useState(0);
  const [bodySearch, setBodySearch] = useState('');
  const [traceFilter, setTraceFilter] = useState('');
  // Set by clicking a bar on the volume chart: narrows the list (not the
  // chart) to one bucket + that severity floor until cleared.
  const [drill, setDrill] = useState<{ fromUtc: string; toUtc: string; severity?: string } | null>(null);

  const services = useQuery({
    queryKey: ['services-for-logs'],
    queryFn:  () => api<ServiceSummary[]>('/v1/services'),
  });

  const volume = useQuery({
    queryKey: ['log-volume', range.presetKey, service],
    queryFn:  () => api<LogVolumePoint[]>(
      `/v1/logs/timeseries?${buildRangeQuery(range.from, range.to, { serviceName: service, bucketSeconds: 60 })}`),
    placeholderData: keepPreviousData,
    refetchInterval: 30_000,
  });

  const logs = useQuery({
    queryKey: ['logs', range.presetKey, service, minSeverity, bodySearch, traceFilter, drill],
    queryFn:  () => {
      const params = new URLSearchParams();
      params.set('limit', '200');
      params.set('fromUtc', drill?.fromUtc ?? range.from.toISOString());
      params.set('toUtc',   drill?.toUtc   ?? range.to.toISOString());
      if (service)        params.set('serviceName', service);
      const sevFloor = drill?.severity ? (SEVERITY_FLOOR[drill.severity] ?? 0) : minSeverity;
      if (sevFloor)       params.set('minSeverity', String(sevFloor));
      if (bodySearch)     params.set('body',        bodySearch);
      if (traceFilter)    params.set('traceId',     traceFilter);
      return api<LogListResponse>(`/v1/logs/?${params.toString()}`);
    },
    placeholderData: keepPreviousData,
    refetchInterval: 30_000,
  });

  const totals = useMemo(() => {
    const items = logs.data?.items ?? [];
    const bySev: Record<string, number> = {};
    for (const l of items) bySev[l.severityText || '?'] = (bySev[l.severityText || '?'] ?? 0) + 1;
    return {
      shown: items.length,
      err:   (bySev.ERROR ?? 0) + (bySev.FATAL ?? 0),
      warn:  bySev.WARN ?? 0,
    };
  }, [logs.data]);

  return (
    <>
      <h1 className="page-title">
        Logs
        <TimeRangePicker />
      </h1>

      <div className="stat-grid" style={{ marginBottom: 12 }}>
        <Stat label="Shown" value={totals.shown.toLocaleString()} />
        <Stat label="WARN"  value={totals.warn.toLocaleString()}  accent={totals.warn > 0 ? 'warn' : 'ok'} />
        <Stat label="ERROR/FATAL" value={totals.err.toLocaleString()} accent={totals.err > 0 ? 'err' : 'ok'} />
        <Stat label="Window" value={range.presetKey} />
      </div>

      <div className="card" style={{ marginBottom: 12 }}>
        <h3 className="card-title">
          Volume by severity
          <span className="muted" style={{ fontSize: 11, fontWeight: 400, marginLeft: 8, textTransform: 'none', letterSpacing: 0 }}>
            click a bar to filter the list to that minute + severity
          </span>
        </h3>
        {volume.isLoading ? <Loading shape="chart" /> :
         volume.error    ? <ErrorBlock error={volume.error} /> :
         (volume.data?.length ?? 0) === 0 ? <Empty label="No logs in this window." /> :
         <VolumeChart points={volume.data!} onDrill={(bucketMs, severity) => {
           setDrill({
             fromUtc: new Date(bucketMs).toISOString(),
             toUtc:   new Date(bucketMs + 60_000).toISOString(),
             severity,
           });
         }} />}
      </div>

      {drill && (
        <div className="card" style={{ marginBottom: 12, padding: '10px 14px', display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
          <span className="muted" style={{ fontSize: 12 }}>Chart drill-down:</span>
          <span className="badge muted mono">
            {drill.fromUtc.slice(11, 19)} → {drill.toUtc.slice(11, 19)} UTC
          </span>
          {drill.severity && <span className="badge warn">≥ {drill.severity}</span>}
          <button className="secondary" onClick={() => setDrill(null)} style={{ fontSize: 11, padding: '2px 10px' }}>
            Clear
          </button>
        </div>
      )}

      <div className="card" style={{ marginBottom: 12 }}>
        <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap', alignItems: 'flex-end' }}>
          <label style={{ display: 'grid', gap: 4, minWidth: 180 }}>
            <span className="muted" style={{ fontSize: 12 }}>Service</span>
            <select value={service} onChange={e => setService(e.target.value)}>
              <option value="">All</option>
              {services.data?.map(s => <option key={s.serviceName} value={s.serviceName}>{s.serviceName}</option>)}
            </select>
          </label>
          <label style={{ display: 'grid', gap: 4, minWidth: 140 }}>
            <span className="muted" style={{ fontSize: 12 }}>Severity</span>
            <select value={minSeverity} onChange={e => setMinSev(Number(e.target.value))}>
              {MIN_SEVERITY_OPTIONS.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
            </select>
          </label>
          <label style={{ display: 'grid', gap: 4, flex: 1, minWidth: 240 }}>
            <span className="muted" style={{ fontSize: 12 }}>Search body</span>
            <input value={bodySearch} onChange={e => setBodySearch(e.target.value)}
              placeholder="case-insensitive substring (e.g. timeout, NullReference)" style={{ padding: '6px 10px' }} />
          </label>
          <label style={{ display: 'grid', gap: 4, minWidth: 220 }}>
            <span className="muted" style={{ fontSize: 12 }}>Trace id</span>
            <input value={traceFilter} onChange={e => setTraceFilter(e.target.value)}
              placeholder="32-char hex" className="mono" style={{ padding: '6px 10px' }} />
          </label>
        </div>
      </div>

      <div className="card" style={{ padding: 0 }}>
        {logs.isLoading ? <Loading /> :
         logs.error    ? <ErrorBlock error={logs.error} /> :
         (logs.data?.items.length ?? 0) === 0 ? <Empty label="No logs match these filters." /> : (
          <table>
            <thead>
              <tr>
                <th>Time (UTC)</th>
                <th>Severity</th>
                <th>Service</th>
                <th>Message</th>
                <th>Trace</th>
              </tr>
            </thead>
            <tbody>
              {logs.data!.items.map(l => <LogRow key={`${l.timestampUtc}-${l.traceIdHex}-${l.body.slice(0, 30)}`} log={l} />)}
            </tbody>
          </table>
        )}
      </div>
    </>
  );
}

function LogRow({ log }: { log: LogRowDto }) {
  const color = SEVERITY_COLORS[log.severityText] ?? '#94A1BC';
  const time  = new Date(log.timestampUtc).toISOString().slice(0, 23).replace('T', ' ');
  return (
    <tr>
      <td className="mono faint" style={{ whiteSpace: 'nowrap' }}>{time}</td>
      <td><span className="badge" style={{ background: color, color: '#0a0e1a', fontWeight: 600 }}>{log.severityText || '?'}</span></td>
      <td>{log.serviceName || '—'}</td>
      <td className="mono" style={{ wordBreak: 'break-word', maxWidth: 720 }}>{log.body}</td>
      <td className="mono faint">
        {log.traceIdHex && !/^0+$/.test(log.traceIdHex)
          ? <Link to={`/traces?traceId=${encodeURIComponent(log.traceIdHex)}`}>{log.traceIdHex.slice(0, 12)}…</Link>
          : '—'}
      </td>
    </tr>
  );
}

function VolumeChart({ points, onDrill }: {
  points: LogVolumePoint[];
  onDrill: (bucketStartMs: number, severity?: string) => void;
}) {
  const allTimes = Array.from(new Set(points.map(p => new Date(p.bucketStartUtc).getTime()))).sort((a, b) => a - b);
  const sevs     = Array.from(new Set(points.map(p => p.severityText || '?')));
  const bySev: Record<string, number[]> = {};
  for (const s of sevs) bySev[s] = allTimes.map(() => 0);
  for (const p of points) {
    const idx = allTimes.indexOf(new Date(p.bucketStartUtc).getTime());
    if (idx >= 0) bySev[p.severityText || '?']![idx] = p.logCount;
  }
  const option = {
    grid: { left: 50, right: 24, top: 24, bottom: 32 },
    legend: { textStyle: { color: '#94A1BC' }, top: 0 },
    tooltip: { trigger: 'axis', backgroundColor: '#16213A', borderColor: '#243049', textStyle: { color: '#E5EAF3' } },
    xAxis: { type: 'time', axisLabel: { color: '#94A1BC' } },
    yAxis: { type: 'value', name: 'logs', axisLabel: { color: '#94A1BC' }, nameTextStyle: { color: '#94A1BC' } },
    series: sevs.map(s => ({
      name: s,
      type: 'bar',
      stack: 'volume',
      data: allTimes.map((t, i) => [t, bySev[s]![i]]),
      itemStyle: { color: SEVERITY_COLORS[s] ?? '#7C5CFF' },
    })),
  } as const;
  return (
    <ReactECharts
      option={option}
      style={{ height: 220 }}
      theme="dark"
      onEvents={{
        click: (params: { seriesName?: string; value?: [number, number] }) => {
          const t = params.value?.[0];
          if (typeof t === 'number') onDrill(t, params.seriesName);
        },
      }}
    />
  );
}

function Stat({ label, value, accent }: { label: string; value: string; accent?: 'ok' | 'err' | 'warn' }) {
  return (
    <div className="card">
      <h3 className="card-title">{label}</h3>
      <div className="stat-value" style={accent ? { color: `var(--${accent})` } : undefined}>{value}</div>
    </div>
  );
}
