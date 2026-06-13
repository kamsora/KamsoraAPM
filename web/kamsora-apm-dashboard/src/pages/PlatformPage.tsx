import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../api/client';
import type { CreateTenantRequest, CreateTenantResponse, TenantSummary } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ErrorBlock, Loading, Empty } from '../components/Loading';
import { CopyField, InstallSnippets } from '../components/InstallWizard';

function TenantRow({ tenant: t }: { tenant: TenantSummary }) {
  const [confirm, setConfirm] = useState<null | 'suspend' | 'resume' | 'delete'>(null);
  const qc = useQueryClient();

  const action = useMutation({
    mutationFn: async (kind: 'suspend' | 'resume' | 'delete') => {
      const path = `/v1/admin/tenants/${encodeURIComponent(t.sysTenantUuid)}${
        kind === 'delete' ? '' : '/' + kind}`;
      await api<void>(path, { method: kind === 'delete' ? 'DELETE' : 'POST' });
    },
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['admin-tenants'] }); setConfirm(null); },
    onError:   (err: any) => alert(err?.body?.error ?? err?.message ?? 'Action failed'),
  });

  const isActive    = t.status === 'active';
  const isSuspended = t.status === 'suspended';
  const isDeleted   = t.status === 'deleted';

  return (
    <tr style={{ opacity: isDeleted ? 0.5 : 1 }}>
      <td>
        <div>{t.tenantName}</div>
        <div className="mono faint" style={{ fontSize: 11 }}>{t.sysTenantUuid.slice(0, 13)}…</div>
      </td>
      <td className="mono">{t.tenantSlug}</td>
      <td>{t.planType}</td>
      <td style={{ textAlign: 'right' }}>{t.dataRetentionDays}d</td>
      <td>
        <span className={`badge ${isActive ? 'ok' : isSuspended ? 'warn' : 'err'}`}>{t.status}</span>
      </td>
      <td className="faint">{t.contactEmail || '-'}</td>
      <td style={{ textAlign: 'right' }}>{t.userCount}</td>
      <td style={{ textAlign: 'right' }}>{t.apiKeyCount}</td>
      <td className="mono faint">{new Date(t.createdAtUtc).toISOString().slice(0, 16).replace('T', ' ')}</td>
      <td style={{ textAlign: 'right' }}>
        {confirm ? (
          <>
            <button
              className="secondary"
              onClick={() => setConfirm(null)}
              style={{ fontSize: 11, marginRight: 4 }}
              disabled={action.isPending}
            >Cancel</button>
            <button
              onClick={() => action.mutate(confirm)}
              style={{ fontSize: 11, background: confirm === 'resume' ? undefined : '#ef4444' }}
              disabled={action.isPending}
              title={
                confirm === 'suspend' ? 'Blocks login & ingest. Reversible.' :
                confirm === 'delete'  ? 'Soft-delete - terminal. ClickHouse data retained per retention policy.' :
                'Re-enables login & ingest.'
              }
            >{action.isPending ? '…' : `Confirm ${confirm}`}</button>
          </>
        ) : (
          <span style={{ display: 'flex', gap: 4, justifyContent: 'flex-end' }}>
            {isActive    && <button className="secondary" onClick={() => setConfirm('suspend')} style={{ fontSize: 11 }}>Suspend</button>}
            {isSuspended && <button className="secondary" onClick={() => setConfirm('resume')}  style={{ fontSize: 11 }}>Resume</button>}
            {!isDeleted  && <button className="secondary" onClick={() => setConfirm('delete')}  style={{ fontSize: 11 }}>Delete</button>}
          </span>
        )}
      </td>
    </tr>
  );
}

/**
 * Platform-admin tenant management page. Visible only to users with the
 * `kamsora_platform_admin` claim. Allows creating new tenants - each create
 * mints a fresh ingest API key and a temporary owner password, both shown
 * once in a modal so the operator can hand them off out-of-band.
 */
export default function PlatformPage() {
  const { isPlatformAdmin } = useAuth();
  const [showCreate, setShowCreate] = useState(false);
  const [createdTenant, setCreatedTenant] = useState<CreateTenantResponse | null>(null);

  const tenants = useQuery({
    queryKey: ['admin-tenants'],
    enabled: isPlatformAdmin,
    queryFn: () => api<TenantSummary[]>('/v1/admin/tenants'),
    placeholderData: keepPreviousData,
    refetchInterval: 30_000,
  });

  if (!isPlatformAdmin) {
    return (
      <>
        <h1 className="page-title">Platform</h1>
        <div className="card">
          <ErrorBlock error={new Error('You do not have platform-admin permission.')} />
        </div>
      </>
    );
  }

  return (
    <>
      <h1 className="page-title">
        Platform
        <button onClick={() => setShowCreate(true)} style={{ marginLeft: 12 }}>
          + Create tenant
        </button>
      </h1>

      <div className="card" style={{ padding: 0 }}>
        {tenants.isLoading ? <Loading /> :
         tenants.error    ? <ErrorBlock error={tenants.error} /> :
         (tenants.data?.length ?? 0) === 0 ? <Empty label="No tenants yet. Click + Create tenant to onboard one." /> : (
          <table>
            <thead>
              <tr>
                <th>Tenant</th>
                <th>Slug</th>
                <th>Plan</th>
                <th style={{ textAlign: 'right' }}>Retention</th>
                <th>Status</th>
                <th>Contact</th>
                <th style={{ textAlign: 'right' }}>Users</th>
                <th style={{ textAlign: 'right' }}>Keys</th>
                <th>Created</th>
                <th style={{ textAlign: 'right' }}>Actions</th>
              </tr>
            </thead>
            <tbody>
              {tenants.data!.map(t => (
                <TenantRow key={t.sysTenantUuid} tenant={t} />
              ))}
            </tbody>
          </table>
        )}
      </div>

      {showCreate && (
        <CreateTenantModal
          onClose={() => setShowCreate(false)}
          onCreated={(r) => { setShowCreate(false); setCreatedTenant(r); }}
        />
      )}
      {createdTenant && (
        <TenantCreatedModal tenant={createdTenant} onClose={() => setCreatedTenant(null)} />
      )}
    </>
  );
}

function CreateTenantModal({
  onClose,
  onCreated,
}: {
  onClose: () => void;
  onCreated: (r: CreateTenantResponse) => void;
}) {
  const [tenantName, setTenantName] = useState('');
  const [tenantSlug, setTenantSlug] = useState('');
  const [ownerEmail, setOwnerEmail] = useState('');
  const [planType, setPlanType] = useState<'free' | 'pro' | 'enterprise'>('free');
  const [retentionDays, setRetentionDays] = useState<number>(14);
  const [contactEmail, setContactEmail] = useState('');
  const [error, setError] = useState<string | null>(null);
  const qc = useQueryClient();

  const mutation = useMutation({
    mutationFn: (req: CreateTenantRequest) => api<CreateTenantResponse>('/v1/admin/tenants', {
      method: 'POST',
      body: JSON.stringify(req),
    }),
    onSuccess: (r) => {
      qc.invalidateQueries({ queryKey: ['admin-tenants'] });
      onCreated(r);
    },
    onError: (err: any) => setError(err?.body?.error ?? err?.message ?? 'Create failed'),
  });

  function submit() {
    setError(null);
    if (!tenantName || !tenantSlug || !ownerEmail) {
      setError('tenantName, tenantSlug and ownerEmail are required');
      return;
    }
    mutation.mutate({
      TenantName: tenantName,
      TenantSlug: tenantSlug,
      OwnerEmail: ownerEmail,
      PlanType: planType,
      RetentionDays: retentionDays,
      ContactEmail: contactEmail || undefined,
    });
  }

  return (
    <ModalShell title="Create tenant" onClose={onClose} wide={false}>
      <div style={{ display: 'grid', gap: 12 }}>
        <Field label="Tenant name" value={tenantName} onChange={setTenantName} placeholder="Acme Inc." />
        <Field label="Tenant slug" value={tenantSlug} onChange={setTenantSlug} placeholder="acme  (a-z, 0-9, -, _)" />
        <Field label="Owner email" value={ownerEmail} onChange={setOwnerEmail} placeholder="owner@acme.com" type="email" />
        <Field label="Contact email (optional)" value={contactEmail} onChange={setContactEmail} placeholder="billing@acme.com" type="email" />
        <div style={{ display: 'flex', gap: 12 }}>
          <label style={{ display: 'grid', gap: 4, flex: 1 }}>
            <span className="muted" style={{ fontSize: 12 }}>Plan</span>
            <select value={planType} onChange={e => setPlanType(e.target.value as any)}>
              <option value="free">free</option>
              <option value="pro">pro</option>
              <option value="enterprise">enterprise</option>
            </select>
          </label>
          <label style={{ display: 'grid', gap: 4, flex: 1 }}>
            <span className="muted" style={{ fontSize: 12 }}>Retention (days)</span>
            <input type="number" value={retentionDays} onChange={e => setRetentionDays(Number(e.target.value))} min={1} max={365} />
          </label>
        </div>

        {error && <div className="badge err" style={{ padding: 8 }}>{error}</div>}

        <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end', marginTop: 8 }}>
          <button className="secondary" onClick={onClose}>Cancel</button>
          <button onClick={submit} disabled={mutation.isPending}>
            {mutation.isPending ? 'Creating…' : 'Create tenant'}
          </button>
        </div>
      </div>
    </ModalShell>
  );
}

function TenantCreatedModal({ tenant, onClose }: { tenant: CreateTenantResponse; onClose: () => void }) {
  return (
    <ModalShell title={`Tenant "${tenant.tenantSlug}" created`} onClose={onClose} wide>
      <p className="muted" style={{ marginTop: 0 }}>
        Save these credentials now - they will <strong>not</strong> be shown again.
      </p>
      <div style={{ display: 'grid', gap: 12 }}>
        <CopyField label="Tenant UUID" value={tenant.tenantUuid} mono />
        <CopyField label="Owner email" value={tenant.ownerEmail} />
        <CopyField label="Owner temporary password" value={tenant.ownerTempPassword} mono secret />
        <CopyField label="Ingest API key" value={tenant.ingestApiKey} mono secret />
      </div>

      <h4 style={{ marginTop: 24 }}>Hand off install commands</h4>
      <InstallSnippets tenantUuid={tenant.tenantUuid} apiKey={tenant.ingestApiKey} />

      <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end', marginTop: 16 }}>
        <button onClick={onClose}>Done</button>
      </div>
    </ModalShell>
  );
}

// ---- shared modal + field shells (lightweight, no library) ----

export function ModalShell({
  title, onClose, children, wide,
}: { title: string; onClose: () => void; children: React.ReactNode; wide?: boolean }) {
  return (
    <>
      <div
        onClick={onClose}
        style={{
          position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.6)', zIndex: 100,
        }}
      />
      <div
        role="dialog"
        aria-modal="true"
        style={{
          position: 'fixed', top: '50%', left: '50%', transform: 'translate(-50%, -50%)',
          background: 'var(--bg-surface-1, #0f172a)', border: '1px solid var(--border, #1e293b)',
          borderRadius: 8, padding: 20, width: wide ? 720 : 480, maxHeight: '85vh',
          overflowY: 'auto', zIndex: 101, boxShadow: '0 24px 48px rgba(0,0,0,0.6)',
        }}
      >
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 12 }}>
          <h2 style={{ margin: 0, fontSize: 18 }}>{title}</h2>
          <button className="secondary" onClick={onClose} title="Close" style={{ padding: '4px 10px' }}>×</button>
        </div>
        {children}
      </div>
    </>
  );
}

function Field({ label, value, onChange, placeholder, type = 'text' }: {
  label: string; value: string; onChange: (v: string) => void; placeholder?: string; type?: string;
}) {
  return (
    <label style={{ display: 'grid', gap: 4 }}>
      <span className="muted" style={{ fontSize: 12 }}>{label}</span>
      <input
        type={type}
        value={value}
        onChange={e => onChange(e.target.value)}
        placeholder={placeholder}
        style={{ padding: '8px 10px' }}
      />
    </label>
  );
}
