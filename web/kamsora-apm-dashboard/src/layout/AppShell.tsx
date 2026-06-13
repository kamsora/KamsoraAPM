import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Activity, AlertCircle, Bell, BellRing, FileText, Gauge, GitBranch, KeyRound,
  LayoutDashboard, Network, ScrollText, Server, Shield, UserPlus, Users, Waypoints,
} from 'lucide-react';
import { NavLink, Outlet, useNavigate, Link } from 'react-router-dom';
import { api } from '../api/client';
import type { InAppNotificationDto } from '../api/types';
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
            <div className="value">{tenantSlug ?? '—'}</div>
          </div>
          <div style={{ marginTop: 8 }}>
            <div className="label">Tenant id</div>
            <div className="value faint" title={tenantId ?? ''}>
              {tenantId ? `${tenantId.slice(0, 8)}…` : '—'}
            </div>
          </div>
          <div style={{ marginTop: 8 }}>
            <div className="label">Signed in</div>
            <div className="value">{email ?? '—'}</div>
          </div>
          <div style={{ display: 'flex', gap: 6, marginTop: 8 }}>
            <NavLink to="/account/password" className="secondary" style={{ flex: 1, textAlign: 'center', padding: '6px 8px', fontSize: 12 }}>
              Password
            </NavLink>
            <button className="secondary" onClick={onLogout} style={{ flex: 1 }}>Sign out</button>
          </div>
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
