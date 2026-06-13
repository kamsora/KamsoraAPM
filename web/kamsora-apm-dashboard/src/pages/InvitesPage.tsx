import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../api/client';
import type {
  CreateInviteRequest,
  CreateInviteResponse,
  InviteSummary,
} from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { CopyField } from '../components/InstallWizard';
import { Empty, ErrorBlock, Loading } from '../components/Loading';
import { ModalShell } from './PlatformPage';

/**
 * Tenant owner: mint, list, and revoke invites. The cleartext token is shown
 * exactly once at mint time inside an "InviteCreatedModal" (with a copyable
 * accept-link). After dismissing, only the prefix is visible.
 */
export default function InvitesPage() {
  const { role, tenantSlug } = useAuth();
  const [showMint, setShowMint] = useState(false);
  const [created, setCreated]   = useState<CreateInviteResponse | null>(null);

  const invites = useQuery({
    queryKey: ['tenant-invites'],
    queryFn: () => api<InviteSummary[]>('/v1/tenant/invites'),
    placeholderData: keepPreviousData,
    refetchInterval: 30_000,
  });

  if (role !== 'owner') {
    return (
      <>
        <h1 className="page-title">Invites</h1>
        <div className="card">
          <ErrorBlock error={new Error('Only tenant owners can manage invites.')} />
        </div>
      </>
    );
  }

  return (
    <>
      <h1 className="page-title">
        Invites
        <span className="badge muted" style={{ marginLeft: 8 }}>{tenantSlug}</span>
        <button onClick={() => setShowMint(true)} style={{ marginLeft: 12 }}>
          + Invite teammate
        </button>
      </h1>

      <div className="card" style={{ padding: 0 }}>
        {invites.isLoading ? <Loading /> :
         invites.error    ? <ErrorBlock error={invites.error} /> :
         (invites.data?.length ?? 0) === 0 ? <Empty label="No invites yet. Click + Invite teammate to send one." /> : (
          <table>
            <thead>
              <tr>
                <th>Email</th>
                <th>Role</th>
                <th>Token</th>
                <th>Status</th>
                <th>Created</th>
                <th>Expires</th>
                <th style={{ textAlign: 'right' }}>Actions</th>
              </tr>
            </thead>
            <tbody>
              {invites.data!.map(i => <InviteRow key={i.sysInviteUuid} invite={i} />)}
            </tbody>
          </table>
        )}
      </div>

      {showMint && (
        <MintInviteModal
          onClose={() => setShowMint(false)}
          onMinted={(r) => { setShowMint(false); setCreated(r); }}
        />
      )}
      {created && (
        <InviteCreatedModal invite={created} onClose={() => setCreated(null)} />
      )}
    </>
  );
}

function InviteRow({ invite }: { invite: InviteSummary }) {
  const [confirming, setConfirming] = useState(false);
  const qc = useQueryClient();
  const revoke = useMutation({
    mutationFn: () => api<void>(`/v1/tenant/invites/${encodeURIComponent(invite.sysInviteUuid)}`, { method: 'DELETE' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['tenant-invites'] }),
  });

  const canRevoke = invite.status === 'pending';
  return (
    <tr>
      <td>{invite.email}</td>
      <td><span className="badge muted">{invite.role}</span></td>
      <td className="mono faint">{invite.tokenPrefix}…</td>
      <td>
        <span className={`badge ${
          invite.status === 'accepted' ? 'ok' :
          invite.status === 'revoked' || invite.status === 'expired' ? 'err' : 'warn'
        }`}>{invite.status}</span>
      </td>
      <td className="mono faint">{fmt(invite.createdAtUtc)}</td>
      <td className="mono faint">{fmt(invite.expiresAtUtc)}</td>
      <td style={{ textAlign: 'right' }}>
        {canRevoke && (confirming ? (
          <>
            <button
              className="secondary"
              onClick={() => setConfirming(false)}
              style={{ fontSize: 11, marginRight: 4 }}
              disabled={revoke.isPending}
            >Cancel</button>
            <button
              onClick={() => revoke.mutate()}
              style={{ fontSize: 11, background: '#ef4444' }}
              disabled={revoke.isPending}
            >{revoke.isPending ? 'Revoking…' : 'Confirm revoke'}</button>
          </>
        ) : (
          <button className="secondary" onClick={() => setConfirming(true)} style={{ fontSize: 11 }}>
            Revoke
          </button>
        ))}
      </td>
    </tr>
  );
}

function MintInviteModal({ onClose, onMinted }: {
  onClose: () => void;
  onMinted: (r: CreateInviteResponse) => void;
}) {
  const [email, setEmail] = useState('');
  const [role, setRole]   = useState<'owner' | 'admin' | 'editor' | 'viewer'>('viewer');
  const [error, setError] = useState<string | null>(null);
  const qc = useQueryClient();

  const mutation = useMutation({
    mutationFn: (req: CreateInviteRequest) => api<CreateInviteResponse>('/v1/tenant/invites', {
      method: 'POST',
      body: JSON.stringify(req),
    }),
    onSuccess: (r) => {
      qc.invalidateQueries({ queryKey: ['tenant-invites'] });
      onMinted(r);
    },
    onError: (err: any) => setError(err?.body?.error ?? err?.message ?? 'Invite failed'),
  });

  return (
    <ModalShell title="Invite teammate" onClose={onClose}>
      <div style={{ display: 'grid', gap: 12 }}>
        <label style={{ display: 'grid', gap: 4 }}>
          <span className="muted" style={{ fontSize: 12 }}>Teammate email</span>
          <input type="email" value={email} onChange={e => setEmail(e.target.value)}
            placeholder="teammate@yourcompany.com" style={{ padding: '8px 10px' }} />
        </label>
        <label style={{ display: 'grid', gap: 4 }}>
          <span className="muted" style={{ fontSize: 12 }}>Role</span>
          <select value={role} onChange={e => setRole(e.target.value as any)}>
            <option value="viewer">viewer - read-only</option>
            <option value="editor">editor - manage dashboards</option>
            <option value="admin">admin - manage alerting</option>
            <option value="owner">owner - manage tenant, keys, invites</option>
          </select>
        </label>
        {error && <div className="badge err" style={{ padding: 8 }}>{error}</div>}
        <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end', marginTop: 8 }}>
          <button className="secondary" onClick={onClose}>Cancel</button>
          <button
            onClick={() => {
              setError(null);
              if (!email.includes('@')) { setError('Enter a valid email'); return; }
              mutation.mutate({ Email: email.trim(), Role: role });
            }}
            disabled={mutation.isPending}
          >{mutation.isPending ? 'Sending…' : 'Mint invite'}</button>
        </div>
      </div>
    </ModalShell>
  );
}

function InviteCreatedModal({ invite, onClose }: { invite: CreateInviteResponse; onClose: () => void }) {
  const acceptLink = `${window.location.origin}/accept-invite?token=${encodeURIComponent(invite.token)}`;
  return (
    <ModalShell title="Invite minted" onClose={onClose} wide>
      <p className="muted" style={{ marginTop: 0 }}>
        Send this link to <strong>{invite.email}</strong>. The token is shown <strong>once</strong>;
        after closing this dialog the cleartext can no longer be retrieved.
      </p>
      <div style={{ display: 'grid', gap: 12 }}>
        <CopyField label="Accept link"  value={acceptLink}        mono secret />
        <CopyField label="Raw token"    value={invite.token}      mono secret />
        <div className="faint" style={{ fontSize: 13 }}>
          Expires {new Date(invite.expiresAtUtc).toLocaleString()} · role <strong>{invite.role}</strong>
        </div>
      </div>
      <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end', marginTop: 16 }}>
        <button onClick={onClose}>Done</button>
      </div>
    </ModalShell>
  );
}

function fmt(iso: string | null): string {
  if (!iso) return '';
  try { return new Date(iso).toISOString().slice(0, 16).replace('T', ' '); } catch { return iso; }
}
