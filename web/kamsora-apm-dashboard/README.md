# KamsoraAPM Dashboard (web)

React + Vite + TypeScript single-page application.
Powers the trace explorer, service inventory and live overview charts.

## Run locally

```bash
cd web/kamsora-apm-dashboard
npm install
npm run dev
```

Opens at <http://localhost:3000>. All `/api/*` requests are proxied to
the Dashboard.Api at `http://localhost:5000` by default. Override with
`KAMSORA_API_URL` env var if you run the API on a different port.

## Tech

- **React 18**, **TypeScript**, **Vite**
- **React Router v6** for routing
- **TanStack Query v5** for data fetching + caching + auto-refresh
- **Apache ECharts** for charts
- Plain CSS (single `styles.css`) - no Tailwind / CSS-in-JS dependency

## Pages

- `/login` - username/password auth against the Dashboard.Api
- `/` - Overview (counts, error rate, latency percentiles, top routes)
- `/traces` - Filterable trace list
- `/traces/:traceId` - Span waterfall + every attribute for a single trace
- `/services` - Service inventory with p50/p90/p99 + error rate

The JWT is stored in `localStorage` (`kamsora.auth.token`).
On any 401 from the API, the SPA hard-redirects to `/login`.
