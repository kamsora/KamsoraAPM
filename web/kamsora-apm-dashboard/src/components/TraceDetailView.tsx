import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../api/client';
import type { LogRowDto, SpanRowDto, SpanEventDto } from '../api/types';
import { ErrorBlock, Loading, Empty } from './Loading';

const SEVERITY_COLORS: Record<string, string> = {
  TRACE: '#94A1BC',
  DEBUG: '#5B89FF',
  INFO:  '#34D399',
  WARN:  '#F59E0B',
  ERROR: '#EF4444',
  FATAL: '#B91C1C',
};

/**
 * Shared body of "view one trace": loads the trace from the API and renders
 * the Endpoint card, Summary, Waterfall, and each span's full attribute set.
 * Reused by both the standalone /traces/:id route AND the slide-in drawer
 * triggered from the Traces list.
 */
export function TraceDetailView({ traceId }: { traceId: string }) {
  const traceQuery = useQuery({
    queryKey: ['trace', traceId],
    queryFn: () => api<SpanRowDto[]>(`/v1/traces/${traceId}`),
    enabled: !!traceId,
  });

  if (traceQuery.isLoading) return <Loading />;
  if (traceQuery.error)    return <ErrorBlock error={traceQuery.error} />;
  if (!traceQuery.data || traceQuery.data.length === 0) {
    return <Empty label="No spans found for this trace." />;
  }
  return <SpanList spans={traceQuery.data} traceId={traceId} />;
}

function SpanList({ spans, traceId }: { spans: SpanRowDto[]; traceId: string }) {
  const sorted = [...spans].sort((a, b) => Number(a.startTimeUnixNano) - Number(b.startTimeUnixNano));
  const minStart = Math.min(...sorted.map(s => Number(s.startTimeUnixNano)));
  const maxEnd   = Math.max(...sorted.map(s => Number(s.endTimeUnixNano)));
  const totalNs  = Math.max(1, maxEnd - minStart);
  const rootStart = new Date(minStart / 1e6).toISOString();
  const services = Array.from(new Set(sorted.map(s => s.serviceName).filter(Boolean)));
  const dbSpans = sorted.filter(s => !!s.dbSystem).length;
  const httpClientSpans = sorted.filter(s => s.spanKind === 'CLIENT' && !s.dbSystem).length;

  const rootServer = sorted.find(s => s.spanKind === 'SERVER' && !s.parentSpanId) || sorted.find(s => s.spanKind === 'SERVER');
  const slowest = sorted.slice().sort((a, b) => Number(b.durationNanos) - Number(a.durationNanos))[0];

  return (
    <>
      <div className="card" style={{ marginBottom: 16 }}>
        <h3 className="card-title">Trace ID</h3>
        <div className="mono" style={{ fontSize: 13, wordBreak: 'break-all' }}>{traceId}</div>
      </div>

      {rootServer && <EndpointCard span={rootServer} />}

      <LinkedLogsCard traceId={traceId} traceStartNs={minStart} />

      <div className="card" style={{ marginBottom: 24 }}>
        <h3 className="card-title">Summary</h3>
        <div className="attr-grid">
          <div className="k">spans</div>             <div className="v">{sorted.length}</div>
          <div className="k">started (UTC)</div>     <div className="v mono">{rootStart}</div>
          <div className="k">total duration</div>    <div className="v">{(totalNs / 1_000_000).toFixed(2)} ms</div>
          <div className="k">services</div>          <div className="v">{services.join(', ') || '—'}</div>
          <div className="k">HTTP client calls</div> <div className="v">{httpClientSpans}</div>
          <div className="k">DB calls</div>          <div className="v">{dbSpans}</div>
          {slowest && (
            <>
              <div className="k">slowest span</div>
              <div className="v">
                <span className="badge muted" style={{ marginRight: 6 }}>{slowest.spanKind}</span>
                {(Number(slowest.durationNanos) / 1_000_000).toFixed(2)} ms · {endpointLabel(slowest)}
              </div>
            </>
          )}
        </div>
      </div>

      <div className="card" style={{ padding: 0 }}>
        <h3 className="card-title" style={{ padding: '20px 20px 0' }}>Waterfall</h3>
        {sorted.map(s => {
          const offset    = (Number(s.startTimeUnixNano) - minStart) / totalNs;
          const width     = Math.max(0.005, Number(s.durationNanos) / totalNs);
          const isError   = s.statusCode === 'ERROR' || s.httpStatusCode >= 500;
          return (
            <details key={s.spanId} id={`span-${s.spanId.toLowerCase()}`} className="span-row" style={{ display: 'block' }}>
              <summary style={{ listStyle: 'none', cursor: 'pointer', display: 'grid', gridTemplateColumns: '160px 1fr 90px', gap: 16, alignItems: 'center' }}>
                <div>
                  <div style={{ fontWeight: 600, fontSize: 13 }}>{s.serviceName || '—'}</div>
                  <div className="faint" style={{ fontSize: 11 }}>{s.spanKind}</div>
                </div>
                <div>
                  <div className="mono" style={{ fontSize: 12.5, marginBottom: 4, wordBreak: 'break-all' }}>
                    <OperationLabel span={s} />
                  </div>
                  <div style={{ background: 'var(--bg-surface-2)', height: 6, borderRadius: 3, position: 'relative' }}>
                    <div style={{
                      position: 'absolute',
                      left:  `${offset * 100}%`,
                      width: `${width * 100}%`,
                      top: 0, bottom: 0,
                      borderRadius: 3,
                      background: isError ? 'var(--err)' : 'var(--accent)',
                    }} />
                  </div>
                </div>
                <div style={{ textAlign: 'right', fontSize: 13 }}>{(Number(s.durationNanos) / 1_000_000).toFixed(2)}ms</div>
              </summary>
              <SpanAttributes span={s} />
            </details>
          );
        })}
      </div>
    </>
  );
}

function OperationLabel({ span }: { span: SpanRowDto }) {
  if (span.dbSystem) {
    return (
      <>
        <span className="badge muted" style={{ marginRight: 6 }}>{span.dbSystem.toUpperCase()}</span>
        <span title={span.dbStatement}>{truncate(span.dbStatement, 140) || span.spanName}</span>
      </>
    );
  }
  if (span.httpMethod) {
    const label = span.httpRoute || tryParseUrl(span.httpUrl) || span.spanName;
    return (
      <>
        <span className="badge muted" style={{ marginRight: 6 }}>{span.httpMethod}</span>
        <span>{label}</span>
      </>
    );
  }
  return <span>{span.spanName}</span>;
}

function SpanAttributes({ span }: { span: SpanRowDto }) {
  const isError = span.statusCode === 'ERROR' || span.httpStatusCode >= 500;
  const customAttrs = Object.entries(span.attributes || {}).sort(([a], [b]) => a.localeCompare(b));

  return (
    <div style={{ paddingTop: 12 }}>
      <Section title="Identity">
        <KV k="trace_id"        v={<span className="mono">{span.traceId}</span>} />
        <KV k="span_id"         v={<span className="mono">{span.spanId}</span>} />
        <KV k="parent_span_id"  v={<span className="mono">{span.parentSpanId || '(root)'}</span>} />
        <KV k="service.name"    v={span.serviceName} />
        {span.serviceVersion && <KV k="service.version" v={span.serviceVersion} />}
        <KV k="span.name"       v={<span className="mono">{span.spanName}</span>} />
        <KV k="span.kind"       v={span.spanKind} />
      </Section>

      <Section title="Timing">
        <KV k="start (UTC)" v={<span className="mono">{new Date(Number(span.startTimeUnixNano) / 1e6).toISOString()}</span>} />
        <KV k="end (UTC)"   v={<span className="mono">{new Date(Number(span.endTimeUnixNano) / 1e6).toISOString()}</span>} />
        <KV k="duration"    v={`${(Number(span.durationNanos) / 1_000_000).toFixed(3)} ms`} />
      </Section>

      <Section title="Status">
        <KV k="status" v={
          <>
            <span className={`badge ${isError ? 'err' : 'ok'}`}>{span.statusCode}</span>
            {span.statusMessage && <span className="muted" style={{ marginLeft: 8 }}>{span.statusMessage}</span>}
          </>
        } />
        {span.httpStatusCode > 0 && <KV k="http.status_code" v={span.httpStatusCode} />}
      </Section>

      {(span.httpMethod || span.httpRoute || span.httpUrl) && (
        <Section title="HTTP">
          {span.httpMethod && <KV k="http.method" v={span.httpMethod} />}
          {span.httpRoute  && <KV k="http.route"  v={<span className="mono">{span.httpRoute}</span>} />}
          {span.httpUrl    && <KV k="http.url"    v={<span className="mono" style={{ wordBreak: 'break-all' }}>{span.httpUrl}</span>} />}
          {span.httpClientIp && <KV k="client.address" v={span.httpClientIp} />}
        </Section>
      )}

      {span.dbSystem && (
        <Section title="Database">
          <KV k="db.system"    v={span.dbSystem} />
          {span.dbDurationNs > 0 && <KV k="db.duration" v={`${(span.dbDurationNs / 1_000_000).toFixed(3)} ms`} />}
          {span.dbStatement && (
            <div style={{ gridColumn: '1 / -1', marginTop: 4 }}>
              <div className="k" style={{ marginBottom: 4 }}>db.statement</div>
              <pre className="mono" style={{
                background: 'var(--bg-app)',
                border: '1px solid var(--border)',
                borderRadius: 6,
                padding: 12,
                margin: 0,
                whiteSpace: 'pre-wrap',
                wordBreak: 'break-word',
              }}>{span.dbStatement}</pre>
            </div>
          )}
        </Section>
      )}

      {customAttrs.length > 0 && (
        <Section title={`All attributes (${customAttrs.length})`}>
          {customAttrs.map(([k, v]) => (
            <KV key={k} k={k} v={<span className="mono" style={{ wordBreak: 'break-all' }}>{v}</span>} />
          ))}
        </Section>
      )}

      {span.events && span.events.length > 0 && (
        <Section title={`Events (${span.events.length})`}>
          <div style={{ gridColumn: '1 / -1' }}>
            {span.events.map((e, i) => <EventBlock key={i} ev={e} />)}
          </div>
        </Section>
      )}
    </div>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <details open style={{ marginBottom: 12 }}>
      <summary style={{
        cursor: 'pointer', listStyle: 'none', padding: '6px 0', borderBottom: '1px solid var(--border)',
        marginBottom: 8, fontSize: 11, textTransform: 'uppercase', letterSpacing: 0.06, color: 'var(--text-muted)',
        fontWeight: 600,
      }}>{title}</summary>
      <div className="attr-grid">{children}</div>
    </details>
  );
}

function KV({ k, v }: { k: string; v: React.ReactNode }) {
  return (<><div className="k">{k}</div><div className="v">{v}</div></>);
}

function EventBlock({ ev }: { ev: SpanEventDto }) {
  let attrs: Record<string, string> = {};
  try { attrs = JSON.parse(ev.attributesJson || '{}'); } catch { /* ignore */ }
  return (
    <div style={{ background: 'var(--bg-app)', border: '1px solid var(--border)', borderRadius: 6, padding: 12, marginBottom: 8 }}>
      <div style={{ fontWeight: 600, marginBottom: 6 }}>{ev.name}</div>
      <div className="faint mono" style={{ fontSize: 11, marginBottom: 8 }}>{new Date(Number(ev.timeUnixNano) / 1e6).toISOString()}</div>
      {Object.entries(attrs).length > 0 && (
        <div className="attr-grid">
          {Object.entries(attrs).map(([k, v]) => (
            <KV key={k} k={k} v={<span className="mono" style={{ wordBreak: 'break-all' }}>{v}</span>} />
          ))}
        </div>
      )}
    </div>
  );
}

function EndpointCard({ span }: { span: SpanRowDto }) {
  const isError    = span.statusCode === 'ERROR' || span.httpStatusCode >= 500;
  const isWarn     = !isError && span.httpStatusCode >= 400;
  const statusKind = isError ? 'err' : isWarn ? 'warn' : 'ok';
  const method     = span.httpMethod || span.attributes?.['http.request.method'] || span.attributes?.['http.method'] || '';
  const route      = span.httpRoute  || span.attributes?.['http.route'] || '';
  const path       = span.attributes?.['url.path'] || tryParseUrl(span.httpUrl || span.attributes?.['url.full'] || '');
  const userAgent  = span.attributes?.['user_agent.original'] || '';
  const noRoute    = !route && span.spanName === 'Microsoft.AspNetCore.Hosting.HttpRequestIn';

  return (
    <div className="card" style={{ marginBottom: 24, borderLeft: `4px solid var(--${statusKind})` }}>
      <h3 className="card-title">Endpoint</h3>
      <div style={{ display: 'flex', alignItems: 'center', flexWrap: 'wrap', gap: 10, marginBottom: 12 }}>
        {method && <span className="badge muted" style={{ fontSize: 14 }}>{method}</span>}
        <span className="mono" style={{ fontSize: 17, fontWeight: 600, wordBreak: 'break-all' }}>
          {route || path || (noRoute ? '(no route matched — static file, swagger asset, or 404)' : span.spanName)}
        </span>
        <span className={`badge ${statusKind}`} style={{ fontSize: 14 }}>
          {span.httpStatusCode > 0 ? span.httpStatusCode : span.statusCode}
        </span>
      </div>
      <div className="attr-grid">
        <div className="k">duration</div>
        <div className="v" style={{ fontWeight: 600 }}>{(Number(span.durationNanos) / 1_000_000).toFixed(2)} ms</div>
        {route && path && route !== path && (<><div className="k">resolved path</div><div className="v mono">{path}</div></>)}
        {span.httpUrl && (<><div className="k">url</div><div className="v mono" style={{ wordBreak: 'break-all' }}>{span.httpUrl}</div></>)}
        {span.httpClientIp && (<><div className="k">client ip</div><div className="v">{span.httpClientIp}</div></>)}
        {userAgent && (<><div className="k">user agent</div><div className="v faint">{userAgent}</div></>)}
        {span.statusMessage && (<><div className="k">status message</div><div className="v">{span.statusMessage}</div></>)}
      </div>
    </div>
  );
}

function endpointLabel(s: SpanRowDto): string {
  if (s.dbSystem) return `${s.dbSystem} · ${truncate(s.dbStatement, 60) || s.spanName}`;
  const method = s.httpMethod || s.attributes?.['http.request.method'] || '';
  const path   = s.httpRoute  || s.attributes?.['http.route'] || s.attributes?.['url.path'] || tryParseUrl(s.httpUrl || s.attributes?.['url.full'] || '');
  if (method && path) return `${method} ${path}`;
  if (path) return path;
  if (s.spanName === 'Microsoft.AspNetCore.Hosting.HttpRequestIn') return 'HTTP request (no route)';
  if (s.spanName === 'System.Net.Http.HttpRequestOut') return 'HttpClient call';
  return s.spanName;
}

function tryParseUrl(raw: string): string {
  if (!raw) return '';
  try { const u = new URL(raw); return `${u.host}${u.pathname}`; } catch { return raw; }
}

function truncate(s: string, max: number): string {
  if (!s) return '';
  const c = s.replace(/\s+/g, ' ').trim();
  return c.length > max ? `${c.slice(0, max - 1)}…` : c;
}

function LinkedLogsCard({ traceId, traceStartNs }: { traceId: string; traceStartNs: number }) {
  const logs = useQuery({
    queryKey: ['logs-by-trace', traceId],
    queryFn:  () => api<LogRowDto[]>(`/v1/logs/by-trace/${encodeURIComponent(traceId)}`),
    enabled:  !!traceId,
  });

  const count = logs.data?.length ?? 0;

  function jumpToSpan(spanId: string) {
    const el = document.getElementById(`span-${spanId.toLowerCase()}`);
    if (!el) return;
    el.setAttribute('open', '');
    el.scrollIntoView({ behavior: 'smooth', block: 'center' });
    el.style.transition = 'background 1.5s ease';
    el.style.background = 'rgba(124,92,255,0.18)';
    setTimeout(() => { el.style.background = ''; }, 1600);
  }

  return (
    <div className="card" style={{ marginBottom: 24 }}>
      <h3 className="card-title">
        Linked Logs
        {count > 0 && <span className="muted" style={{ fontSize: 11, fontWeight: 400, marginLeft: 8 }}>({count})</span>}
      </h3>
      {logs.isLoading ? <Loading /> :
       logs.error    ? <ErrorBlock error={logs.error} /> :
       count === 0   ? (
        <div className="faint" style={{ fontSize: 13, padding: '4px 0' }}>
          No correlated logs for this trace. Make sure your code calls{' '}
          <span className="mono">_logger.LogX(...)</span> inside the request scope so the
          OTel logging pipeline can attach the active trace_id.
        </div>
       ) : (
        <div style={{ display: 'grid', gap: 4 }}>
          {logs.data!.map((l, i) => (
            <LogLine key={`${l.timestampUtc}-${i}`} log={l} traceStartNs={traceStartNs} onSpanClick={jumpToSpan} />
          ))}
        </div>
      )}
    </div>
  );
}

function LogLine({ log, traceStartNs, onSpanClick }:
  { log: LogRowDto; traceStartNs: number; onSpanClick: (id: string) => void }) {
  const [open, setOpen] = useState(false);
  const color    = SEVERITY_COLORS[log.severityText] ?? '#94A1BC';
  const tsNs     = new Date(log.timestampUtc).getTime() * 1e6;
  const deltaMs  = (tsNs - traceStartNs) / 1e6;
  const sign     = deltaMs >= 0 ? '+' : '';
  const attrs    = Object.entries(log.attributes || {});

  return (
    <div style={{
      display: 'grid',
      gridTemplateColumns: '70px 70px 1fr auto',
      gap: 10,
      alignItems: 'start',
      padding: '6px 8px',
      borderLeft: `3px solid ${color}`,
      background: 'var(--bg-surface-2, rgba(255,255,255,0.02))',
      borderRadius: 4,
      fontSize: 12.5,
    }}>
      <span className="badge" style={{ background: color, color: '#0a0e1a', fontWeight: 600, fontSize: 10, justifySelf: 'start' }}>
        {log.severityText || '?'}
      </span>
      <span className="mono faint" style={{ fontSize: 11 }}>{sign}{deltaMs.toFixed(1)}ms</span>
      <div>
        <div className="mono" style={{ wordBreak: 'break-word', cursor: attrs.length > 0 ? 'pointer' : 'default' }}
             onClick={() => attrs.length > 0 && setOpen(o => !o)}>
          {log.body}
        </div>
        {open && attrs.length > 0 && (
          <div className="attr-grid" style={{ marginTop: 6, fontSize: 11 }}>
            {attrs.flatMap(([k, v]) => [
              <div key={`k-${k}`} className="k">{k}</div>,
              <div key={`v-${k}`} className="v mono" style={{ wordBreak: 'break-all' }}>{v}</div>,
            ])}
          </div>
        )}
      </div>
      {log.spanIdHex && !/^0+$/.test(log.spanIdHex) && (
        <button className="secondary" type="button"
                onClick={() => onSpanClick(log.spanIdHex)}
                title={`Jump to span ${log.spanIdHex}`}
                style={{ fontSize: 10, padding: '2px 6px', height: 'fit-content' }}>
          span {log.spanIdHex.slice(0, 8)}…
        </button>
      )}
    </div>
  );
}
