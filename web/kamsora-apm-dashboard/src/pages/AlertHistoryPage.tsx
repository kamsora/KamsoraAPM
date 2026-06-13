import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../api/client';
import type { AlertFiringPage } from '../api/types';
import { Empty, ErrorBlock, Loading } from '../components/Loading';

export default function AlertHistoryPage() {
  const [activeOnly, setActiveOnly] = useState(false);
  const [page, setPage] = useState(1);
  const pageSize = 50;

  const firings = useQuery({
    queryKey: ['alert-firings', activeOnly, page],
    queryFn:  () => api<AlertFiringPage>(`/v1/alerts/firings?page=${page}&pageSize=${pageSize}&activeOnly=${activeOnly}`),
    placeholderData: keepPreviousData,
    refetchInterval: 15_000,
  });

  const totalPages = firings.data ? Math.max(1, Math.ceil(firings.data.total / pageSize)) : 1;

  return (
    <>
      <h1 className="page-title">
        Alert history
        <label style={{ marginLeft: 16, fontSize: 13, display: 'inline-flex', gap: 6, alignItems: 'center' }}>
          <input type="checkbox" checked={activeOnly} onChange={e => { setPage(1); setActiveOnly(e.target.checked); }} />
          <span className="muted">active only</span>
        </label>
      </h1>

      <div className="card" style={{ padding: 0 }}>
        {firings.isLoading ? <Loading /> :
         firings.error    ? <ErrorBlock error={firings.error} /> :
         (firings.data?.items.length ?? 0) === 0 ? <Empty label={activeOnly ? "No active alerts firing right now. 🎉" : "No alerts have fired yet."} /> : (
          <table>
            <thead>
              <tr>
                <th>Fired at (UTC)</th>
                <th>Rule</th>
                <th>Signal</th>
                <th style={{ textAlign: 'right' }}>Observed</th>
                <th>Severity</th>
                <th>Status</th>
                <th>Resolved at</th>
                <th>Duration</th>
              </tr>
            </thead>
            <tbody>
              {firings.data!.items.map(f => {
                const fired = new Date(f.firedAtUtc);
                const resolved = f.resolvedAtUtc ? new Date(f.resolvedAtUtc) : null;
                const durationMs = resolved ? resolved.getTime() - fired.getTime() : Date.now() - fired.getTime();
                return (
                  <tr key={f.sysFiringTransId}>
                    <td className="mono faint">{fired.toISOString().slice(0, 19).replace('T', ' ')}</td>
                    <td><strong>{f.ruleName}</strong></td>
                    <td><span className="badge muted mono">{f.signalType}</span></td>
                    <td className="mono" style={{ textAlign: 'right' }}>{formatValue(f.signalType, f.observedValue)}</td>
                    <td><span className={`badge ${f.severity === 'critical' ? 'err' : f.severity === 'warning' ? 'warn' : 'muted'}`}>{f.severity}</span></td>
                    <td><span className={`badge ${resolved ? 'ok' : 'err'}`}>{resolved ? 'resolved' : 'firing'}</span></td>
                    <td className="mono faint">{resolved ? resolved.toISOString().slice(0, 19).replace('T', ' ') : '—'}</td>
                    <td className="faint">{formatDuration(durationMs)}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </div>

      {firings.data && firings.data.total > pageSize && (
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginTop: 12, fontSize: 13 }}>
          <span className="faint">{firings.data.total.toLocaleString()} firings · page {page} of {totalPages}</span>
          <span style={{ display: 'flex', gap: 8 }}>
            <button className="secondary" disabled={page <= 1}          onClick={() => setPage(p => Math.max(1, p - 1))}>← Prev</button>
            <button className="secondary" disabled={page >= totalPages} onClick={() => setPage(p => p + 1)}>Next →</button>
          </span>
        </div>
      )}
    </>
  );
}

function formatValue(signal: string, v: number): string {
  if (signal === 'error_rate')     return `${(v * 100).toFixed(2)}%`;
  if (signal === 'request_volume') return v.toLocaleString();
  return `${Math.round(v)}ms`;
}

function formatDuration(ms: number): string {
  if (ms < 60_000)       return `${Math.round(ms / 1000)}s`;
  if (ms < 3_600_000)    return `${Math.round(ms / 60_000)}m`;
  return `${(ms / 3_600_000).toFixed(1)}h`;
}
