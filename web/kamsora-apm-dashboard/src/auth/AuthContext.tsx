import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { api, clearToken, getToken, setToken } from '../api/client';
import type { LoginResponse } from '../api/types';

interface AuthState {
  token: string | null;
  tenantId: string | null;
  tenantSlug: string | null;
  role: string | null;
  email: string | null;
  isPlatformAdmin: boolean;
}

interface AuthValue extends AuthState {
  login: (email: string, password: string) => Promise<void>;
  /** Drop a pre-issued token + profile into the session — used by accept-invite. */
  adoptSession: (session: AuthState) => void;
  logout: () => void;
  isAuthenticated: boolean;
}

const AuthContext = createContext<AuthValue | undefined>(undefined);

const STORAGE_KEY = 'kamsora.auth.profile';

function readPersistedProfile(): AuthState {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return emptyState();
    const parsed = JSON.parse(raw) as Partial<AuthState>;
    return {
      token: getToken(),
      tenantId: parsed.tenantId ?? null,
      tenantSlug: parsed.tenantSlug ?? null,
      role: parsed.role ?? null,
      email: parsed.email ?? null,
      isPlatformAdmin: parsed.isPlatformAdmin ?? false,
    };
  } catch {
    return emptyState();
  }
}

function emptyState(): AuthState {
  return { token: null, tenantId: null, tenantSlug: null, role: null, email: null, isPlatformAdmin: false };
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<AuthState>(readPersistedProfile);

  useEffect(() => {
    if (!state.token) {
      localStorage.removeItem(STORAGE_KEY);
    } else {
      const { tenantId, tenantSlug, role, email, isPlatformAdmin } = state;
      localStorage.setItem(STORAGE_KEY, JSON.stringify({ tenantId, tenantSlug, role, email, isPlatformAdmin }));
    }
  }, [state]);

  const value = useMemo<AuthValue>(() => ({
    ...state,
    isAuthenticated: !!state.token,
    async login(email: string, password: string) {
      const resp = await api<LoginResponse>('/v1/auth/login', {
        method: 'POST',
        body: JSON.stringify({ Email: email, Password: password }),
      });
      setToken(resp.accessToken);
      setState({
        token: resp.accessToken,
        tenantId: resp.tenantId,
        tenantSlug: resp.tenantSlug,
        role: resp.role,
        email,
        isPlatformAdmin: resp.isPlatformAdmin,
      });
    },
    adoptSession(session: AuthState) {
      setState(session);
    },
    logout() {
      clearToken();
      setState(emptyState());
    },
  }), [state]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within an AuthProvider');
  return ctx;
}
