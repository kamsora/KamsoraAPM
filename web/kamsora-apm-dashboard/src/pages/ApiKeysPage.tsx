import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../api/client';
import type { ApiKeySummary, MintApiKeyRequest, MintApiKeyResponse } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ErrorBlock, Loading, Empty } from '../components/Loading';
import { CopyField, InstallSnippets } from '../components/InstallWizard';
import { ModalShell } from './PlatformPage';

/**
 * Per-tenant API key management. Owner only (enforced server-side by the
 * <c>TenantOwner</c> policy). Lists active keys, allows minting + revoking,
 * and shows install snippets for hand-off.
 */
export default function ApiKeysPage() {
  const { tenantId, tenantSlug, role } = useAuth();
  const [showMint, setShowMint] = useState(false);
  const [showInstall, setShowInstall] = useState<MintApiKeyResponse | null>(null);

  const keys = useQuery({
    queryKey: ['tenant-api-keys', tenantId],
    queryFn: () => api<ApiKeySummary[]>('/v1/tenant/api-keys'),
    placeholderData: keepPreviousData,
    refetchInterval: 30_000,
  });

  if (role !== 'owner') {
    return (
      <>
        <h1 className="page-title">API Keys</h1>
        <div className="card">
          <ErrorBlock error={new Error('Only tenant owners can manage API keys.')} />
        </div>
      </>
    );
  }

  return (
    <>
      <h1 className="page-title">
        API Keys
        <span className="badge muted" style={{ marginLeft: 8 }}>{tenantSlug}</span>
        <button onClick={() => setShowMint(true)} style={{ marginLeft: 12 }}>
          + Mint new key
        </button>
      </h1>

      <div className="card" style={{ padding: 0 }}>
        {keys.isLoading ? <Loading /> :
         keys.error    ? <ErrorBlock error={keys.error} /> :
         (keys.data?.length ?? 0) === 0 ? <Empty label="No active API keys. Click + Mint new key to create one." /> : (
          <table>
            <thead>
              <tr>
                <th>Name</th>
                <th>Prefix</th>
                <th>Scopes</th>
                <th>Created</th>
                <th>Last used</th>
                <th>Expires</th>
                <th style={{ textAlign: 'right' }}>Actions</th>
              </tr>
            </thead>
            <tbody>
              {keys.data!.map(k => (
                <ApiKeyRow key={k.sysApiKeyUuid} k={k} />
              ))}
            </tbody>
          </table>
        )}
      </div>

      {showMint && (
        <MintKeyModal
          onClose={() => setShowMint(false)}
          onMinted={(r) => { setShowMint(false); setShowInstall(r); }}
        />
      )}
      {showInstall && tenantId && (
        <InstallModal
          tenantUuid={tenantId}
          minted={showInstall}
          onClose={() => setShowInstall(null)}
        />
      )}
    </>
  );
}

function ApiKeyRow({ k }: { k: ApiKeySummary }) {
  const [confirming, setConfirming] = useState(false);
  const qc = useQueryClient();
  const revoke = useMutation({
    mutationFn: () => api<void>(`/v1/tenant/api-keys/${encodeURIComponent(k.sysApiKeyUuid)}`, { method: 'DELETE' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['tenant-api-keys'] }),
  });

  return (
    <tr>
      <td>{k.keyName}</td>
      <td className="mono">{k.keyPrefix}…</td>
      <td className="faint">{k.scopes}</td>
      <td className="mono faint">{fmt(k.createdAtUtc)}</td>
      <td className="mono faint">{fmt(k.lastUsedAtUtc) || '-'}</td>
      <td className="mono faint">{fmt(k.expiresAtUtc) || 'never'}</td>
      <td style={{ textAlign: 'right' }}>
        {confirming ? (
          <>
            <button
              className="secondary"
              onClick={() => setConfirming(false)}
              style={{ fontSize: 11, marginRight: 4 }}
              disabled={revoke.isPending}
            >
              Cancel
            </button>
            <button
              onClick={() => revoke.mutate()}
              style={{ fontSize: 11, background: '#ef4444' }}
              disabled={revoke.isPending}
              title="Revoke this key - agents using it will start getting Unauthenticated errors immediately."
            >
              {revoke.isPending ? 'Revoking…' : 'Confirm revoke'}
            </button>
          </>
        ) : (
          <button className="secondary" onClick={() => setConfirming(true)} style={{ fontSize: 11 }}>
            Revoke
          </button>
        )}
      </td>
    </tr>
  );
}

function MintKeyModal({ onClose, onMinted }: { onClose: () => void; onMinted: (r: MintApiKeyResponse) => void }) {
  const [keyName, setKeyName] = useState('');
  const [scopes, setScopes]   = useState('ingest');
  const [error, setError]     = useState<string | null>(null);
  const qc = useQueryClient();

  const mutation = useMutation({
    mutationFn: (req: MintApiKeyRequest) => api<MintApiKeyResponse>('/v1/tenant/api-keys', {
      method: 'POST',
      body: JSON.stringify(req),
    }),
    onSuccess: (r) => {
      qc.invalidateQueries({ queryKey: ['tenant-api-keys'] });
      onMinted(r);
    },
    onError: (err: any) => setError(err?.body?.error ?? err?.message ?? 'Mint failed'),
  });

  return (
    <ModalShell title="Mint API key" onClose={onClose}>
      <div style={{ display: 'grid', gap: 12 }}>
        <label style={{ display: 'grid', gap: 4 }}>
          <span className="muted" style={{ fontSize: 12 }}>Key name</span>
          <input value={keyName} onChange={e => setKeyName(e.target.value)} placeholder="e.g. prod-host-monitor-eu" style={{ padding: '8px 10px' }} />
        </label>
        <label style={{ display: 'grid', gap: 4 }}>
          <span className="muted" style={{ fontSize: 12 }}>Scopes</span>
          <input value={scopes} onChange={e => setScopes(e.target.value)} placeholder="ingest" style={{ padding: '8px 10px' }} />
        </label>

        {error && <div className="badge err" style={{ padding: 8 }}>{error}</div>}

        <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end', marginTop: 8 }}>
          <button className="secondary" onClick={onClose}>Cancel</button>
          <button
            onClick={() => {
              setError(null);
              if (!keyName.trim()) { setError('Key name is required'); return; }
              mutation.mutate({ KeyName: keyName.trim(), Scopes: scopes.trim() || 'ingest' });
            }}
            disabled={mutation.isPending}
          >
            {mutation.isPending ? 'Minting…' : 'Mint key'}
          </button>
        </div>
      </div>
    </ModalShell>
  );
}

function InstallModal({ tenantUuid, minted, onClose }: {
  tenantUuid: string; minted: MintApiKeyResponse; onClose: () => void;
}) {
  return (
    <ModalShell title="Key minted - copy install commands now" onClose={onClose} wide>
      <p className="muted" style={{ marginTop: 0 }}>
        The cleartext key is shown <strong>once</strong>. After closing this dialog only the prefix remains visible.
      </p>
      <CopyField label="API key" value={minted.cleartext} mono secret />
      <h4 style={{ marginTop: 24 }}>Install snippets</h4>
      <InstallSnippets tenantUuid={tenantUuid} apiKey={minted.cleartext} />
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
