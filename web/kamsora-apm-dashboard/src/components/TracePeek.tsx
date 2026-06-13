import { useQuery } from '@tanstack/react-query';
import { ArrowLeft } from 'lucide-react';
import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api } from '../api/client';
import type { SpanRowDto, TraceListResponse } from '../api/types';
import { tracesUrl } from '../charts/drill';
import { Empty, ErrorBlock, Loading } from './Loading';
import { OperationLabel } from './OperationLabel';
import { TraceDetailView } from './TraceDetailView';
import { DurationBar, StatusPill, TimeAgo } from './viz';

/**
 * In-place trace exploration: a filtered span list that swaps to the full
 * trace detail on row click (with a back button) - no page navigation.
 * Used by every chart drill-through so the user never loses their context.
 */

export interface TraceFilter {
  service?:    string;
  route?:      string;
  consumerId?: string;
  fromUtc?:    string;
  toUtc?:      string;
  errorsOnly?: boolean;
  kind?:       string;
  limit?:      number;
}

export function traceFilterQuery(f: TraceFilter): string {
  const params = new URLSearchParams();
  params.set('limit', String(f.limit ?? 100));
  if (f.service)    params.set('service',    f.service);
  if (f.route)      params.set('route',      f.route);
  if (f.consumerId) params.set('consumerId', f.consumerId);
  if (f.fromUtc)    params.set('fromUtc',    f.fromUtc);
  if (f.toUtc)      params.set('toUtc',      f.toUtc);
  if (f.errorsOnly) params.set('errorsOnly', 'true');
  if (f.kind)       params.set('kind',       f.kind);
  return params.toString();
}

export function windowLabel(f: TraceFilter): string {
  if (!f.fromUtc || !f.toUtc) return '';
  return `${f.fromUtc.slice(11, 19)} - ${f.toUtc.slice(11, 19)} UTC`;
}

/** List ⇄ detail explorer, embeddable in any drawer body. */
export function TraceListExplorer({ filter }: { filter: TraceFilter }) {
  const [traceId, setTraceId] = useState<string | null>(null);

  // A new drill resets the explorer back to the list view.
  useEffect(() => { setTraceId(null); }, [filter]);

  const traces = useQuery({
    queryKey: ['peek-traces', filter],
    queryFn: () => api<TraceListResponse>(`/v1/traces?${traceFilterQuery(filter)}`),
  });

  if (traceId) {
    return (
      <>
        <button className="secondary" onClick={() => setTraceId(null)}
          style={{ display: 'inline-flex', alignItems: 'center', gap: 6, fontSize: 12, marginBottom: 14 }}>
          <ArrowLeft size={13} /> Back to list
        </button>
        <TraceDetailView traceId={traceId} />
      </>
    );
  }

  const rows = traces.data?.items ?? [];
  const maxMs = Math.max(1, ...rows.map(r => r.durationNanos / 1_000_000));
  const showService = !filter.service;

  return (
    traces.isLoading ? <Loading /> :
    traces.error    ? <ErrorBlock error={traces.error} /> :
    rows.length === 0 ? <Empty label="No spans in this window with these filters." /> : (
      <div className="card" style={{ padding: 0, overflow: 'hidden' }}>
        <table>
          <thead>
            <tr>
              <th>When</th>
              {showService && <th>Service</th>}
              <th>Operation</th>
              <th>Status</th>
              <th style={{ textAlign: 'right', width: 130 }}>Duration</th>
            </tr>
          </thead>
          <tbody>
            {rows.map(r => (
              <PeekRow key={`${r.traceId}-${r.spanId}`} row={r} maxMs={maxMs}
                showService={showService} onOpen={() => setTraceId(r.traceId)} />
            ))}
          </tbody>
        </table>
      </div>
    )
  );
}

function PeekRow({ row: r, maxMs, showService, onOpen }: {
  row: SpanRowDto; maxMs: number; showService: boolean; onOpen: () => void;
}) {
  const whenIso = new Date(Number(BigInt(r.startTimeUnixNano) / 1000000n)).toISOString();
  return (
    <tr className="clickable" onClick={onOpen} title="Click to view trace detail">
      <td className="mono" style={{ whiteSpace: 'nowrap' }}><TimeAgo iso={whenIso} /></td>
      {showService && <td>{r.serviceName}</td>}
      <td className="mono" style={{ wordBreak: 'break-all', maxWidth: 520 }}>
        <OperationLabel span={r} />
      </td>
      <td><StatusPill httpStatus={r.httpStatusCode} statusCode={r.statusCode} /></td>
      <td style={{ textAlign: 'right' }}><DurationBar ms={r.durationNanos / 1_000_000} maxMs={maxMs} /></td>
    </tr>
  );
}

/** Slide-in drawer wrapper around the explorer - chart drills open this. */
export function TracePeekDrawer({ filter, title, onClose }: {
  filter: TraceFilter | null; title?: string; onClose: () => void;
}) {
  const open = !!filter;

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

  if (!filter) return null;

  const label = title ?? 'Traces';
  const wl = windowLabel(filter);

  return (
    <>
      <div className="drawer-backdrop" onClick={onClose} aria-hidden />
      <aside className="drawer" role="dialog" aria-modal="true" aria-label={label}>
        <header className="drawer-header">
          <h2 style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
            {label}
            {wl && <span className="badge muted mono">{wl}</span>}
            {filter.errorsOnly && <span className="badge err">errors only</span>}
          </h2>
          <div className="actions">
            <Link
              to={tracesUrl({
                service: filter.service, route: filter.route, consumerId: filter.consumerId,
                fromUtc: filter.fromUtc, toUtc: filter.toUtc, kind: filter.kind,
                errorsOnly: filter.errorsOnly ? 'true' : undefined,
              })}
              className="secondary"
              style={{
                fontSize: 12, padding: '6px 10px',
                border: '1px solid var(--border)', borderRadius: 'var(--radius-sm)',
                background: 'var(--bg-surface-2)', color: 'var(--text)', textDecoration: 'none',
              }}
              onClick={onClose}
            >
              Open full page ↗
            </Link>
            <button className="drawer-close" onClick={onClose} title="Close (Esc)">×</button>
          </div>
        </header>
        <div className="drawer-body">
          <TraceListExplorer filter={filter} />
        </div>
      </aside>
    </>
  );
}
