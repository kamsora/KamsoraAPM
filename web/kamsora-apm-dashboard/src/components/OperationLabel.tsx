import type { SpanRowDto } from '../api/types';
import { MethodBadge } from './viz';

/**
 * Pick a useful display label for a span based on its kind + attributes.
 *  - DB span (db.system set): db.system badge + truncated db.statement
 *  - HTTP span: method + route or target URL
 *  - other: span_name verbatim
 */
export function OperationLabel({ span }: { span: SpanRowDto }) {
  if (span.dbSystem) {
    return (
      <>
        <span className="badge muted" style={{ marginRight: 6 }}>{span.dbSystem.toUpperCase()}</span>
        <span title={span.dbStatement}>{summarize(span.dbStatement, 140) || span.spanName}</span>
      </>
    );
  }

  const method = span.httpMethod || span.attributes?.['http.request.method'] || span.attributes?.['http.method'] || '';
  // Prefer route template (e.g. /api/v1/orders/{id}). Fall back to actual path,
  // then full URL host+path, then last-resort span name.
  const path = span.httpRoute
            || span.attributes?.['http.route']
            || span.attributes?.['url.path']
            || tryParseUrl(span.httpUrl || span.attributes?.['url.full'] || '')
            || friendlySpanName(span.spanName);

  if (method) {
    return (
      <>
        <span style={{ marginRight: 6 }}><MethodBadge method={method} /></span>
        <span>{path}</span>
      </>
    );
  }
  if (path && path !== span.spanName) return <span>{path}</span>;
  return <span>{friendlySpanName(span.spanName)}</span>;
}

/**
 * Map noisy framework activity names to something a human reads. Currently
 * collapses ASP.NET Core's default "Microsoft.AspNetCore.Hosting.HttpRequestIn"
 * to the more obvious "HTTP request (no route)".
 */
export function friendlySpanName(name: string): string {
  if (!name) return '(unknown)';
  if (name === 'Microsoft.AspNetCore.Hosting.HttpRequestIn') return 'HTTP request (no route)';
  if (name === 'System.Net.Http.HttpRequestOut')             return 'HttpClient call';
  return name;
}

function tryParseUrl(raw: string): string {
  if (!raw) return '';
  try {
    const u = new URL(raw);
    return `${u.host}${u.pathname}`;
  } catch {
    return raw;
  }
}

function summarize(s: string, max: number): string {
  if (!s) return '';
  const c = s.replace(/\s+/g, ' ').trim();
  return c.length > max ? `${c.slice(0, max - 1)}…` : c;
}
