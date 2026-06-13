import { useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import type { ChangePasswordRequest } from '../api/types';
import { useAuth } from '../auth/AuthContext';

export default function ChangePasswordPage() {
  const { email } = useAuth();
  const navigate = useNavigate();
  const [oldPw, setOldPw] = useState('');
  const [newPw, setNewPw] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [okMsg, setOkMsg] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null); setOkMsg(null);
    if (newPw.length < 8)       { setError('New password must be at least 8 characters.'); return; }
    if (newPw !== confirm)      { setError('New passwords do not match.');                 return; }
    if (newPw === oldPw)        { setError('New password must differ from the old one.');  return; }
    setBusy(true);
    try {
      const req: ChangePasswordRequest = { OldPassword: oldPw, NewPassword: newPw };
      await api<void>('/v1/me/change-password', { method: 'POST', body: JSON.stringify(req) });
      setOkMsg('Password changed.');
      setOldPw(''); setNewPw(''); setConfirm('');
      setTimeout(() => navigate('/'), 1500);
    } catch (err) {
      if (err instanceof ApiError) setError((err.body as any)?.error ?? err.message);
      else setError(err instanceof Error ? err.message : 'Change failed.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <h1 className="page-title">Change password</h1>
      <div className="card" style={{ maxWidth: 420 }}>
        <p className="muted" style={{ marginTop: 0 }}>
          Signed in as <strong>{email}</strong>. After saving you stay logged in on this tab - other sessions keep their existing token until it expires.
        </p>
        <form onSubmit={onSubmit} style={{ display: 'grid', gap: 12 }}>
          <label style={{ display: 'grid', gap: 4 }}>
            <span className="muted" style={{ fontSize: 12 }}>Current password</span>
            <input
              type="password"
              value={oldPw}
              onChange={(e) => setOldPw(e.target.value)}
              autoComplete="current-password"
              required
            />
          </label>
          <label style={{ display: 'grid', gap: 4 }}>
            <span className="muted" style={{ fontSize: 12 }}>New password (min 8 characters)</span>
            <input
              type="password"
              value={newPw}
              onChange={(e) => setNewPw(e.target.value)}
              autoComplete="new-password"
              minLength={8}
              required
            />
          </label>
          <label style={{ display: 'grid', gap: 4 }}>
            <span className="muted" style={{ fontSize: 12 }}>Confirm new password</span>
            <input
              type="password"
              value={confirm}
              onChange={(e) => setConfirm(e.target.value)}
              autoComplete="new-password"
              minLength={8}
              required
            />
          </label>
          {error  && <div className="badge err" style={{ padding: 8 }}>{error}</div>}
          {okMsg && <div className="badge ok"  style={{ padding: 8 }}>{okMsg}</div>}
          <button type="submit" disabled={busy}>{busy ? 'Saving…' : 'Save new password'}</button>
        </form>
      </div>
    </>
  );
}
