import { useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { ApiError } from '../api/client';

export default function LoginPage() {
  const { login } = useAuth();
  const navigate = useNavigate();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      await login(email, password);
      navigate('/');
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        setError('Invalid email or password.');
      } else {
        setError(err instanceof Error ? err.message : 'Login failed.');
      }
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="login-shell">
      <form className="card login-card" onSubmit={onSubmit}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 24 }}>
          <img src="/kamsora-icon.png" alt="KamsoraAPM" width={36} height={36} style={{ borderRadius: 8 }} />
          <div>
            <h1>KamsoraAPM</h1>
            <p className="muted" style={{ margin: 0, fontSize: 13 }}>Application Performance Monitoring</p>
          </div>
        </div>

        {error && <div className="error">{error}</div>}

        <div className="form-row">
          <label htmlFor="email">Email</label>
          <input
            id="email"
            type="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            placeholder="you@example.com"
            required
            autoFocus
            autoComplete="username"
          />
        </div>

        <div className="form-row">
          <label htmlFor="password">Password</label>
          <input
            id="password"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            required
            autoComplete="current-password"
          />
        </div>

        <button type="submit" disabled={busy} style={{ width: '100%' }}>
          {busy ? 'Signing in…' : 'Sign in'}
        </button>

        <p className="muted" style={{ marginTop: 24, fontSize: 12, textAlign: 'center', lineHeight: 1.6 }}>
          <a href="https://kamsora.com" target="_blank" rel="noopener noreferrer" style={{ fontWeight: 600 }}>
            Kamsora Technologies Pvt. Ltd.
          </a>
          <br />
          Self-hosted KamsoraAPM · Apache 2.0
        </p>
      </form>
    </div>
  );
}
