import { AlertTriangle, Inbox } from 'lucide-react';

/**
 * Loading state. Renders shimmer skeletons shaped like the content that will
 * replace them ("chart", "table", "stats"), so pages never flash plain text.
 */
export function Loading({ shape = 'table', label }: { shape?: 'table' | 'chart' | 'stats'; label?: string }) {
  if (shape === 'chart') {
    return (
      <div className="skeleton-row" aria-busy="true" aria-label={label ?? 'Loading'}>
        <div className="skeleton" style={{ height: 220 }} />
      </div>
    );
  }
  if (shape === 'stats') {
    return (
      <div className="stat-grid" aria-busy="true" aria-label={label ?? 'Loading'}>
        {Array.from({ length: 6 }, (_, i) => (
          <div className="card" key={i}>
            <div className="skeleton" style={{ height: 12, width: '55%', marginBottom: 12 }} />
            <div className="skeleton" style={{ height: 30, width: '70%' }} />
          </div>
        ))}
      </div>
    );
  }
  return (
    <div className="skeleton-row" aria-busy="true" aria-label={label ?? 'Loading'} style={{ padding: '8px 0' }}>
      <div className="skeleton" style={{ height: 14, width: '40%' }} />
      <div className="skeleton" style={{ height: 14, width: '85%' }} />
      <div className="skeleton" style={{ height: 14, width: '72%' }} />
      <div className="skeleton" style={{ height: 14, width: '90%' }} />
      <div className="skeleton" style={{ height: 14, width: '60%' }} />
    </div>
  );
}

export function ErrorBlock({ error }: { error: unknown }) {
  const msg = error instanceof Error ? error.message : 'Unknown error';
  return (
    <div className="empty">
      <AlertTriangle size={28} style={{ color: 'var(--err)' }} />
      <div style={{ color: 'var(--err)', fontWeight: 500 }}>Something went wrong</div>
      <div className="faint" style={{ fontSize: 12, marginTop: 4 }}>{msg}</div>
    </div>
  );
}

export function Empty({ label = 'No data in the selected time range yet.' }: { label?: string }) {
  return (
    <div className="empty">
      <Inbox size={28} />
      <div>{label}</div>
    </div>
  );
}
