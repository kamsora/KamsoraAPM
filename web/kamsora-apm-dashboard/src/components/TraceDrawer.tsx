import { useEffect } from 'react';
import { Link } from 'react-router-dom';
import { TraceDetailView } from './TraceDetailView';

/**
 * Slide-in panel showing one trace's full detail without leaving the Traces
 * list. Lifecycle:
 *   - Closes on Esc, backdrop click, or the X button.
 *   - Locks page scroll while open (`body.drawer-open`).
 *   - Offers an "Open full page" link for bookmarking / sharing.
 */
export function TraceDrawer({ traceId, onClose }: { traceId: string | null; onClose: () => void }) {
  // Esc-to-close + body-scroll-lock side-effects.
  useEffect(() => {
    if (!traceId) return;

    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose();
    }
    document.addEventListener('keydown', onKey);
    document.body.classList.add('drawer-open');

    return () => {
      document.removeEventListener('keydown', onKey);
      document.body.classList.remove('drawer-open');
    };
  }, [traceId, onClose]);

  if (!traceId) return null;

  return (
    <>
      <div className="drawer-backdrop" onClick={onClose} aria-hidden />
      <aside className="drawer" role="dialog" aria-modal="true" aria-label="Trace detail">
        <header className="drawer-header">
          <h2>Trace detail</h2>
          <div className="actions">
            <Link
              to={`/traces/${traceId}`}
              className="secondary"
              style={{
                fontSize: 12,
                padding: '6px 10px',
                border: '1px solid var(--border)',
                borderRadius: 'var(--radius-sm)',
                background: 'var(--bg-surface-2)',
                color: 'var(--text)',
                textDecoration: 'none',
              }}
              title="Open this trace in a full page (bookmarkable)"
              onClick={onClose}
            >
              Open full page ↗
            </Link>
            <button className="drawer-close" onClick={onClose} title="Close (Esc)">×</button>
          </div>
        </header>
        <div className="drawer-body">
          <TraceDetailView traceId={traceId} />
        </div>
      </aside>
    </>
  );
}
