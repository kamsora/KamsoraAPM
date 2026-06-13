import { Link, useParams } from 'react-router-dom';
import { TraceDetailView } from '../components/TraceDetailView';

/**
 * Full-page trace view at /traces/:traceId. Kept for direct linking and
 * bookmarking — the in-list drawer is the primary UX entry point.
 */
export default function TraceDetailPage() {
  const { traceId } = useParams<{ traceId: string }>();
  if (!traceId) return null;

  return (
    <>
      <h1 className="page-title">
        Trace detail
        <Link to="/traces" className="badge muted" style={{ textDecoration: 'none' }}>← Back to traces</Link>
      </h1>
      <TraceDetailView traceId={traceId} />
    </>
  );
}
