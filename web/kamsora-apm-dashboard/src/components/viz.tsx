/**
 * Small presentation primitives shared by every analytics table:
 * sparklines, proportional data bars, colored HTTP method/status badges,
 * route-template highlighting, and relative timestamps.
 */

const METHOD_COLORS: Record<string, string> = {
  GET:     '#34D399',
  POST:    '#4FA3FF',
  PUT:     '#A855F7',
  PATCH:   '#F59E0B',
  DELETE:  '#EF4444',
  OPTIONS: '#5C698A',
  HEAD:    '#5C698A',
};

export function MethodBadge({ method }: { method: string }) {
  const m = (method || '').toUpperCase();
  if (!m) return <span className="badge muted">-</span>;
  const color = METHOD_COLORS[m] ?? '#8E9BB8';
  return (
    <span
      className="badge"
      style={{
        color,
        background: `${color}1A`,
        borderColor: `${color}59`,
        fontFamily: 'var(--font-mono)',
      }}
    >
      {m}
    </span>
  );
}

export function StatusPill({ httpStatus, statusCode }: { httpStatus: number; statusCode?: string }) {
  const cls = statusCode === 'ERROR' || httpStatus >= 500 ? 'err'
    : httpStatus >= 400 ? 'warn'
    : httpStatus >= 300 ? 'muted' : 'ok';
  return <span className={`badge ${cls}`}>{httpStatus > 0 ? httpStatus : (statusCode ?? '-')}</span>;
}

/** Route template with `{params}` tinted so the shape pops: /orders/{id} */
export function RouteLabel({ route, title }: { route: string; title?: string }) {
  if (!route) return <span className="faint">-</span>;
  const parts = route.split(/(\{[^}]*\})/g);
  return (
    <span className="mono" style={{ wordBreak: 'break-all' }} title={title ?? route}>
      {parts.map((p, i) =>
        p.startsWith('{')
          ? <span key={i} style={{ color: 'var(--accent)' }}>{p}</span>
          : <span key={i}>{p}</span>)}
    </span>
  );
}

/**
 * Fixed-size SVG sparkline: smooth-ish area of request counts, with red
 * baseline ticks under buckets that contained errors. Pure SVG - cheap
 * enough for hundreds of table rows.
 */
export function Sparkline({
  counts, errors, width = 120, height = 28, color = '#8B7CFF',
}: {
  counts: number[]; errors?: number[]; width?: number; height?: number; color?: string;
}) {
  const n = counts.length;
  if (n === 0) return null;
  const max = Math.max(1, ...counts);
  const padTop = 3;
  const usable = height - padTop - 3;
  const step = n > 1 ? width / (n - 1) : width;

  const pts = counts.map((c, i) => {
    const x = n > 1 ? i * step : width / 2;
    const y = padTop + usable * (1 - c / max);
    return [x, y] as const;
  });
  const line = pts.map(([x, y], i) => `${i === 0 ? 'M' : 'L'}${x.toFixed(1)},${y.toFixed(1)}`).join(' ');
  const area = `${line} L${width},${height} L0,${height} Z`;

  return (
    <svg width={width} height={height} style={{ display: 'block', overflow: 'visible' }} aria-hidden="true">
      <path d={area} fill={color} opacity={0.14} />
      <path d={line} fill="none" stroke={color} strokeWidth={1.5}
        strokeLinejoin="round" strokeLinecap="round" />
      {errors?.map((e, i) => e > 0 && (
        <rect key={i}
          x={(n > 1 ? i * step : width / 2) - Math.max(1, step / 4)}
          y={height - 2.5}
          width={Math.max(2, step / 2)} height={2.5} rx={1}
          fill="var(--err)" />
      ))}
    </svg>
  );
}

/**
 * Right-aligned number with a proportional bar behind it - turns a numeric
 * column into something the eye can rank without reading.
 */
export function DataBar({
  value, max, color = 'rgba(139, 124, 255, 0.22)', format,
}: {
  value: number; max: number; color?: string; format?: (v: number) => string;
}) {
  const pct = max > 0 ? Math.min(100, (value / max) * 100) : 0;
  return (
    <div className="databar">
      <div className="databar-fill" style={{ width: `${pct}%`, background: color }} />
      <span className="databar-value">{format ? format(value) : value.toLocaleString()}</span>
    </div>
  );
}

/**
 * Duration with a log-scale heat bar behind it - slow requests literally
 * glow. Color encodes absolute severity (fast neutral, 100ms+ amber,
 * 1s+ red); width encodes rank within the loaded set.
 */
export function DurationBar({ ms, maxMs }: { ms: number; maxMs: number }) {
  const logVal = Math.log10(1 + ms);
  const logMax = Math.log10(1 + Math.max(maxMs, 1));
  const pct = logMax > 0 ? Math.min(100, (logVal / logMax) * 100) : 0;
  const color = ms >= 1000 ? 'rgba(239, 68, 68, 0.30)'
              : ms >= 100  ? 'rgba(245, 158, 11, 0.26)'
              : 'rgba(139, 124, 255, 0.18)';
  const textColor = ms >= 1000 ? 'var(--err)' : ms >= 100 ? 'var(--warn)' : undefined;
  return (
    <div className="databar" style={{ minWidth: 96 }}>
      <div className="databar-fill" style={{ width: `${pct}%`, background: color }} />
      <span className="databar-value" style={textColor ? { color: textColor } : undefined}>{formatMs(ms)}</span>
    </div>
  );
}

/** "2 min ago" with the exact UTC timestamp on hover. */
export function TimeAgo({ iso }: { iso: string }) {
  const exact = (() => {
    try { return new Date(iso).toISOString().replace('T', ' ').replace('Z', ' UTC'); } catch { return iso; }
  })();
  return <span className="faint" title={exact} style={{ whiteSpace: 'nowrap' }}>{timeAgo(iso)}</span>;
}

export function timeAgo(iso: string | number | Date): string {
  const then = new Date(iso).getTime();
  if (!Number.isFinite(then)) return String(iso);
  const sec = Math.max(0, (Date.now() - then) / 1000);
  if (sec < 5)        return 'just now';
  if (sec < 60)       return `${Math.floor(sec)}s ago`;
  if (sec < 3600)     return `${Math.floor(sec / 60)} min ago`;
  if (sec < 86_400)   return `${Math.floor(sec / 3600)} hr${sec >= 7200 ? 's' : ''} ago`;
  if (sec < 2_592_000) return `${Math.floor(sec / 86_400)} day${sec >= 172_800 ? 's' : ''} ago`;
  return new Date(then).toISOString().slice(0, 10);
}

export function formatMs(ms: number): string {
  if (!Number.isFinite(ms) || ms <= 0) return '-';
  if (ms < 1)     return `${(ms * 1000).toFixed(0)}µs`;
  if (ms < 10)    return `${ms.toFixed(2)}ms`;
  if (ms < 1000)  return `${Math.round(ms)}ms`;
  return `${(ms / 1000).toFixed(2)}s`;
}
