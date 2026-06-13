import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { api } from '../api/client';
import type { AuditLogPage, TenantSummary } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ErrorBlock, Loading, Empty } from '../components/Loading';

/**
 * Audit-log viewer. Platform admins see ALL tenants with a tenant filter;
 * tenant owners see only their own (the route on the server enforces this).
 * Same component - branches on `isPlatformAdmin` at the API level.
 */
export default function AuditLogPage() {
  const { isPlatformAdmin, role, tenantSlug } = useAuth();
  const [actionFilter, setActionFilter] = useState('');
  const [actorFilter, setActorFilter]   = useState('');
  const [tenantFilter, setTenantFilter] = useState('');
  const [page, setPage]                 = useState(1);
  const pageSize                        = 50;

  const canView = isPlatformAdmin || role === 'owner';

  // For platform admin only - load tenants for the filter dropdown.
  const tenants = useQuery({
    queryKey: ['admin-tenants-for-audit'],
    enabled: isPlatformAdmin,
    queryFn: () => api<TenantSummary[]>('/v1/admin/tenants'),
    staleTime: 60_000,
  });

  const params = useMemo(() => {
    const p = new URLSearchParams();
    p.set('page',     String(page));
    p.set('pageSize', String(pageSize));
    if (actionFilter.trim()) p.set('action', actionFilter.trim());
    if (actorFilter.trim())  p.set('actor',  actorFilter.trim());
    if (isPlatformAdmin && tenantFilter.trim()) p.set('tenantUuid', tenantFilter.trim());
    return p.toString();
  }, [page, actionFilter, actorFilter, tenantFilter, isPlatformAdmin]);

  const apiPath = isPlatformAdmin ? '/v1/admin/audit-log' : '/v1/tenant/audit-log';

  const log = useQuery({
    queryKey: ['audit-log', isPlatformAdmin, params],
    enabled: canView,
    queryFn: () => api<AuditLogPage>(`${apiPath}?${params}`),
    placeholderData: keepPreviousData,
    refetchInterval: 30_000,
  });

  if (!canView) {
    return (
      <>
        <h1 className="page-title">Audit log</h1>
        <div className="card">
          <ErrorBlock error={new Error('You do not have permission to view the audit log.')} />
        </div>
      </>
    );
  }

  const totalPages = log.data ? Math.max(1, Math.ceil(log.data.total / pageSize)) : 1;

  return (
    <>
      <h1 className="page-title">
        Audit log
        {!isPlatformAdmin && tenantSlug && <span className="badge muted" style={{ marginLeft: 8 }}>{tenantSlug}</span>}
        {isPlatformAdmin && <span className="badge muted" style={{ marginLeft: 8 }}>all tenants</span>}
      </h1>

      <div className="card" style={{ marginBottom: 12 }}>
        <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap', alignItems: 'flex-end' }}>
          <Filter label="Action prefix" value={actionFilter} onChange={(v) => { setPage(1); setActionFilter(v); }} placeholder="e.g. apikey." />
          <Filter label="Actor (email or tag)" value={actorFilter}  onChange={(v) => { setPage(1); setActorFilter(v); }}  placeholder="e.g. admin@" />
          {isPlatformAdmin && (
            <label style={{ display: 'grid', gap: 4, minWidth: 240 }}>
              <span className="muted" style={{ fontSize: 12 }}>Tenant</span>
              <select value={tenantFilter} onChange={(e) => { setPage(1); setTenantFilter(e.target.value); }}>
                <option value="">all tenants</option>
                {tenants.data?.map(t => (
                  <option key={t.sysTenantUuid} value={t.sysTenantUuid}>{t.tenantSlug} - {t.tenantName}</option>
                ))}
              </select>
            </label>
          )}
          {(actionFilter || actorFilter || tenantFilter) && (
            <button className="secondary" onClick={() => { setActionFilter(''); setActorFilter(''); setTenantFilter(''); setPage(1); }}>
              Clear filters
            </button>
          )}
        </div>
      </div>

      <div className="card" style={{ padding: 0 }}>
        {log.isLoading ? <Loading /> :
         log.error    ? <ErrorBlock error={log.error} /> :
         (log.data?.items.length ?? 0) === 0 ? <Empty label="No audit entries match the current filters." /> : (
          <table>
            <thead>
              <tr>
                <th>Time (UTC)</th>
                {isPlatformAdmin && <th>Tenant</th>}
                <th>Actor</th>
                <th>Action</th>
                <th>Target</th>
                <th>IP</th>
              </tr>
            </thead>
            <tbody>
              {log.data!.items.map(e => (
                <tr key={e.sysAuditTransId}>
                  <td className="mono faint">{fmt(e.postedAtUtc)}</td>
                  {isPlatformAdmin && <td className="mono faint">{e.sysTenantUuid.slice(0, 8)}…</td>}
                  <td>{e.actorEmail ?? e.postedBy ?? '-'}</td>
                  <td><span className="badge muted mono">{e.action}</span></td>
                  <td className="mono faint" title={e.targetUuid ?? ''}>
                    {e.targetKind ?? '-'}{e.targetUuid ? ` · ${e.targetUuid.slice(0, 8)}…` : ''}
                  </td>
                  <td className="mono faint">{e.clientIp ?? '-'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      {log.data && log.data.total > pageSize && (
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginTop: 12, fontSize: 13 }}>
          <span className="faint">{log.data.total.toLocaleString()} entries · page {page} of {totalPages}</span>
          <span style={{ display: 'flex', gap: 8 }}>
            <button className="secondary" disabled={page <= 1}        onClick={() => setPage(p => Math.max(1, p - 1))}>← Prev</button>
            <button className="secondary" disabled={page >= totalPages} onClick={() => setPage(p => p + 1)}>Next →</button>
          </span>
        </div>
      )}
    </>
  );
}

function Filter({ label, value, onChange, placeholder }: {
  label: string; value: string; onChange: (v: string) => void; placeholder?: string;
}) {
  return (
    <label style={{ display: 'grid', gap: 4, minWidth: 200 }}>
      <span className="muted" style={{ fontSize: 12 }}>{label}</span>
      <input value={value} onChange={e => onChange(e.target.value)} placeholder={placeholder} style={{ padding: '6px 10px' }} />
    </label>
  );
}

function fmt(iso: string): string {
  try { return new Date(iso).toISOString().slice(0, 19).replace('T', ' '); } catch { return iso; }
}
