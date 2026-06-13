import { useEffect, useState, type FormEvent } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { api, ApiError, setToken } from '../api/client';
import type { AcceptInviteRequest, AcceptInviteResponse, InvitePreview } from '../api/types';
import { useAuth } from '../auth/AuthContext';

/**
 * Public landing page for invite tokens. The token can be provided either as
 * a query param (?token=…, the form delivered when copying the link from
 * the owner UI) or pasted into the form. On success the new user is logged
 * in directly via the JWT returned from /invites/accept.
 */
export default function AcceptInvitePage() {
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const { adoptSession } = useAuth();

  const [token, setTokenInput] = useState(params.get('token') ?? '');
  const [preview, setPreview] = useState<InvitePreview | null>(null);
  const [previewError, setPreviewError] = useState<string | null>(null);
  const [previewBusy, setPreviewBusy] = useState(false);

  const [displayName, setDisplayName] = useState('');
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (token.trim().length < 12) {
      setPreview(null);
      setPreviewError(null);
      return;
    }
    let cancelled = false;
    setPreviewBusy(true);
    setPreviewError(null);
    api<InvitePreview>(`/v1/invites/preview/${encodeURIComponent(token.trim())}`)
      .then(p => { if (!cancelled) { setPreview(p); setDisplayName(p.email.split('@')[0] ?? ''); } })
      .catch(err => {
        if (cancelled) return;
        if (err instanceof ApiError && err.status === 404) {
          setPreviewError('This invite is invalid, revoked or expired.');
        } else {
          setPreviewError(err instanceof Error ? err.message : 'Failed to load invite.');
        }
        setPreview(null);
      })
      .finally(() => { if (!cancelled) setPreviewBusy(false); });
    return () => { cancelled = true; };
  }, [token]);

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    if (password.length < 8) { setError('Password must be at least 8 characters.'); return; }
    if (password !== confirm) { setError('Passwords do not match.'); return; }
    setBusy(true);
    try {
      const req: AcceptInviteRequest = {
        Token: token.trim(),
        Password: password,
        DisplayName: displayName.trim() || undefined,
      };
      const resp = await api<AcceptInviteResponse>('/v1/invites/accept', {
        method: 'POST',
        body: JSON.stringify(req),
      });
      setToken(resp.accessToken);
      adoptSession({
        token: resp.accessToken,
        tenantId: resp.tenantId,
        tenantSlug: resp.tenantSlug,
        role: resp.role,
        email: preview?.email ?? '',
        isPlatformAdmin: resp.isPlatformAdmin,
      });
      navigate('/');
    } catch (err) {
      if (err instanceof ApiError) setError((err.body as any)?.error ?? err.message);
      else setError(err instanceof Error ? err.message : 'Accept failed.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="login-shell">
      <form className="card login-card" onSubmit={onSubmit}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 16 }}>
          <img src="/kamsora-icon.png" alt="KamsoraAPM" width={36} height={36} style={{ borderRadius: 8 }} />
          <div>
            <h1>Accept invite</h1>
            <p className="muted" style={{ margin: 0, fontSize: 13 }}>Join a KamsoraAPM tenant</p>
          </div>
        </div>

        <div className="form-row">
          <label htmlFor="token">Invite token</label>
          <input
            id="token"
            type="text"
            value={token}
            onChange={(e) => setTokenInput(e.target.value)}
            placeholder="kinv_…"
            autoFocus
            spellCheck={false}
            className="mono"
          />
          {previewBusy && <small className="muted">Looking up…</small>}
          {previewError && <small className="error">{previewError}</small>}
        </div>

        {preview && (
          <div className="card" style={{ background: 'rgba(124,92,255,0.06)', padding: 12, marginBottom: 12, fontSize: 13 }}>
            <div><strong>Tenant:</strong> {preview.tenantName} <span className="faint mono">({preview.tenantSlug})</span></div>
            <div><strong>Email:</strong> {preview.email}</div>
            <div><strong>Role:</strong> {preview.role}</div>
            <div className="faint">Expires {new Date(preview.expiresAtUtc).toLocaleString()}</div>
          </div>
        )}

        <div className="form-row">
          <label htmlFor="displayName">Display name</label>
          <input
            id="displayName"
            type="text"
            value={displayName}
            onChange={(e) => setDisplayName(e.target.value)}
            disabled={!preview}
            autoComplete="name"
          />
        </div>

        <div className="form-row">
          <label htmlFor="password">Set password</label>
          <input
            id="password"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            disabled={!preview}
            autoComplete="new-password"
            required
            minLength={8}
          />
        </div>

        <div className="form-row">
          <label htmlFor="confirm">Confirm password</label>
          <input
            id="confirm"
            type="password"
            value={confirm}
            onChange={(e) => setConfirm(e.target.value)}
            disabled={!preview}
            autoComplete="new-password"
            required
            minLength={8}
          />
        </div>

        {error && <div className="error">{error}</div>}

        <button type="submit" disabled={busy || !preview} style={{ width: '100%' }}>
          {busy ? 'Joining…' : 'Accept invite & sign in'}
        </button>

        <p className="muted" style={{ marginTop: 24, fontSize: 12, textAlign: 'center' }}>
          Already have an account? <a href="/login">Sign in</a>
        </p>
      </form>
    </div>
  );
}
