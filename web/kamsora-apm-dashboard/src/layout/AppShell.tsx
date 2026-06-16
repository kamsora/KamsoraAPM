import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Activity, AlertCircle, Bell, BellRing, Check, FileText, Gauge, GitBranch, KeyRound,
  LayoutDashboard, Network, Pencil, ScrollText, Server, Shield, UserPlus, Users, Waypoints, X,
} from 'lucide-react';
import { NavLink, Outlet, useNavigate, Link } from 'react-router-dom';
import { api } from '../api/client';
import type { InAppNotificationDto, TenantProfile } from '../api/types';
import { useAuth } from '../auth/AuthContext';

export default function AppShell() {
  const { tenantSlug, tenantId, email, role, isPlatformAdmin, isAuthenticated, logout } = useAuth();
  const navigate = useNavigate();
  const qc = useQueryClient();

  const notifications = useQuery({
    queryKey: ['inapp-notifications-unread'],
    enabled:  isAuthenticated,
    queryFn:  () => api<InAppNotificationDto[]>('/v1/alerts/notifications?unreadOnly=true&limit=10'),
    refetchInterval: 30_000,
    placeholderData: (prev) => prev,
  });

  const ack = useMutation({
    mutationFn: (uuid: string) => api<void>(`/v1/alerts/notifications/${encodeURIComponent(uuid)}/ack`, { method: 'POST' }),
    onSuccess:  () => qc.invalidateQueries({ queryKey: ['inapp-notifications-unread'] }),
  });

  const tenantProfile = useQuery({
    queryKey: ['me-tenant'],
    enabled:  isAuthenticated,
    queryFn:  () => api<TenantProfile>('/v1/me/tenant'),
  });

  function onLogout() {
    logout();
    navigate('/login');
  }

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand">
          <span className="logo" />
          <span>KamsoraAPM</span>
        </div>

        <nav>
          <div className="nav-section">Observe</div>
          <NavLink to="/" end><LayoutDashboard />Overview</NavLink>
          <NavLink to="/traces"><GitBranch />Traces</NavLink>
          <NavLink to="/services"><Waypoints />Services</NavLink>
          <NavLink to="/service-map"><Network />Service Map</NavLink>
          <NavLink to="/consumers"><Users />Consumers</NavLink>
          <NavLink to="/errors"><AlertCircle />Errors</NavLink>
          <NavLink to="/logs"><ScrollText />Logs</NavLink>
          <NavLink to="/metrics"><Gauge />Metrics</NavLink>
          <NavLink to="/hosts"><Server />Hosts</NavLink>

          <div className="nav-section">Alerting</div>
          <NavLink to="/alerts/history"><Bell />Alerts</NavLink>
          {role === 'owner' && <NavLink to="/alerts/rules"><BellRing />Alert rules</NavLink>}
          {role === 'owner' && <NavLink to="/alerts/channels"><Activity />Alert channels</NavLink>}

          {(role === 'owner' || isPlatformAdmin) && <div className="nav-section">Admin</div>}
          {role === 'owner' && <NavLink to="/api-keys"><KeyRound />API Keys</NavLink>}
          {role === 'owner' && <NavLink to="/invites"><UserPlus />Invites</NavLink>}
          {(role === 'owner' || isPlatformAdmin) && <NavLink to="/audit-log"><FileText />Audit log</NavLink>}
          {isPlatformAdmin && <NavLink to="/platform"><Shield />Platform</NavLink>}
        </nav>

        <div className="spacer" />

        <div className="tenant">
          <div>
            <div className="label">Tenant</div>
            <TenantNameEditor
              name={tenantProfile.data?.tenantName ?? tenantSlug ?? '-'}
              canEdit={role === 'owner' && tenantProfile.isSuccess}
            />
          </div>
          <div style={{ marginTop: 8 }}>
            <div className="label">Tenant id</div>
            <div className="value faint" title={tenantId ?? ''}>
              {tenantId ? `${tenantId.slice(0, 8)}…` : '-'}
            </div>
          </div>
          <div style={{ marginTop: 8 }}>
            <div className="label">Signed in</div>
            <div className="value">{email ?? '-'}</div>
          </div>
          <div style={{ display: 'flex', gap: 6, marginTop: 8 }}>
            <NavLink to="/account/password" className="secondary" style={{ flex: 1, textAlign: 'center', padding: '6px 8px', fontSize: 12 }}>
              Password
            </NavLink>
            <button className="secondary" onClick={onLogout} style={{ flex: 1 }}>Sign out</button>
          </div>
          <a
            href="https://kamsora.com"
            target="_blank"
            rel="noopener noreferrer"
            className="muted"
            style={{ display: 'block', marginTop: 12, fontSize: 11, textAlign: 'center', textDecoration: 'none' }}
            title="Open kamsora.com"
          >
            © Kamsora Technologies Pvt. Ltd.
          </a>
        </div>
      </aside>

      <main className="main">
        {(notifications.data?.length ?? 0) > 0 && (
          <div style={{ display: 'grid', gap: 8, marginBottom: 16 }}>
            {notifications.data!.map(n => (
              <div key={n.sysNotificationTransId}
                className="card"
                style={{
                  borderLeft: `4px solid ${n.severity === 'critical' ? '#ef4444' : n.severity === 'warning' ? '#f59e0b' : '#7c5cff'}`,
                  padding: '10px 14px',
                  display: 'flex', gap: 12, alignItems: 'center', flexWrap: 'wrap',
                }}>
                <span className={`badge ${n.severity === 'critical' ? 'err' : n.severity === 'warning' ? 'warn' : 'muted'}`}>{n.severity}</span>
                <div style={{ flex: 1, minWidth: 240 }}>
                  <div style={{ fontWeight: 500 }}>{n.title}</div>
                  <div className="faint" style={{ fontSize: 12 }}>{n.body}</div>
                </div>
                <Link to="/alerts/history" style={{ fontSize: 12 }}>View history</Link>
                <button className="secondary" disabled={ack.isPending}
                  onClick={() => ack.mutate(n.sysNotificationTransId)}
                  style={{ fontSize: 11 }}>
                  Dismiss
                </button>
              </div>
            ))}
          </div>
        )}
        <Outlet />
      </main>
    </div>
  );
}

/**
 * Inline editor for the tenant display name shown in the sidebar. Owners get a
 * pencil that swaps the label for an input; everyone else sees read-only text.
 * Only the display name is editable - the tenant slug stays fixed server-side.
 */
function TenantNameEditor({ name, canEdit }: { name: string; canEdit: boolean }) {
  const qc = useQueryClient();
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(name);

  const rename = useMutation({
    mutationFn: (newName: string) =>
      api<void>('/v1/tenant/name', { method: 'PUT', body: JSON.stringify({ Name: newName }) }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['me-tenant'] });
      setEditing(false);
    },
  });

  if (!editing) {
    return (
      <div className="value" style={{ display: 'flex', alignItems: 'center', gap: 6, minWidth: 0 }}>
        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }} title={name}>{name}</span>
        {canEdit && (
          <button
            type="button"
            title="Rename tenant"
            onClick={() => { setDraft(name); setEditing(true); }}
            style={{ background: 'none', border: 'none', cursor: 'pointer', padding: 2, color: 'inherit', opacity: 0.55, flexShrink: 0 }}
          >
            <Pencil size={13} />
          </button>
        )}
      </div>
    );
  }

  const submit = () => {
    const trimmed = draft.trim();
    if (trimmed && trimmed !== name) rename.mutate(trimmed);
    else setEditing(false);
  };

  return (
    <div style={{ marginTop: 2 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
        <input
          autoFocus
          value={draft}
          maxLength={120}
          disabled={rename.isPending}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => { if (e.key === 'Enter') submit(); if (e.key === 'Escape') setEditing(false); }}
          style={{ flex: 1, minWidth: 0, fontSize: 13, padding: '2px 6px' }}
        />
        <button type="button" title="Save" onClick={submit} disabled={rename.isPending}
          style={{ background: 'none', border: 'none', cursor: 'pointer', padding: 2, color: '#34d399', flexShrink: 0 }}>
          <Check size={15} />
        </button>
        <button type="button" title="Cancel" onClick={() => setEditing(false)} disabled={rename.isPending}
          style={{ background: 'none', border: 'none', cursor: 'pointer', padding: 2, color: '#f87171', flexShrink: 0 }}>
          <X size={15} />
        </button>
      </div>
      {rename.isError && <small className="error">Rename failed. Try again.</small>}
    </div>
  );
}
