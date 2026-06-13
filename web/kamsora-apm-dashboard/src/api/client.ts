// Lightweight fetch wrapper that:
// 1. Attaches the JWT from localStorage to every request.
// 2. Throws on non-2xx with a parsed error body.
// 3. Auto-prefixes /api so callers pass paths like "/v1/overview".

const TOKEN_KEY = 'kamsora.auth.token';

export function getToken(): string | null {
  return localStorage.getItem(TOKEN_KEY);
}

export function setToken(token: string): void {
  localStorage.setItem(TOKEN_KEY, token);
}

export function clearToken(): void {
  localStorage.removeItem(TOKEN_KEY);
}

export class ApiError extends Error {
  constructor(public status: number, public body: unknown, message: string) {
    super(message);
    this.name = 'ApiError';
  }
}

export async function api<T>(path: string, init: RequestInit = {}): Promise<T> {
  const token = getToken();
  const headers = new Headers(init.headers);
  headers.set('Content-Type', 'application/json');
  if (token) headers.set('Authorization', `Bearer ${token}`);

  const url = path.startsWith('/api') ? path : `/api${path}`;
  const resp = await fetch(url, { ...init, headers });

  if (resp.status === 401) {
    clearToken();
    // Hard redirect — drop any cached state.
    window.location.href = '/login';
    throw new ApiError(401, null, 'Unauthorized');
  }

  if (!resp.ok) {
    let body: unknown = null;
    try { body = await resp.json(); } catch { /* not JSON */ }
    throw new ApiError(resp.status, body, `Request failed: ${resp.status}`);
  }

  if (resp.status === 204) return undefined as T;
  return (await resp.json()) as T;
}

// Convenience helpers ---------------------------------------------------

export function buildRangeQuery(fromUtc: Date, toUtc: Date, extra: Record<string, string | number | undefined> = {}): string {
  const params = new URLSearchParams();
  params.set('fromUtc', fromUtc.toISOString());
  params.set('toUtc', toUtc.toISOString());
  for (const [k, v] of Object.entries(extra)) {
    if (v !== undefined && v !== '') params.set(k, String(v));
  }
  return params.toString();
}
