import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useMemo } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api, buildRangeQuery } from '../api/client';
import type { EntitySparkline, ServiceSummary } from '../api/types';
import { ErrorBlock, Loading, Empty } from '../components/Loading';
import { TimeRangePicker, useTimeRange } from '../components/TimeRangePicker';
import { DataBar, formatMs, Sparkline, TimeAgo } from '../components/viz';

export default function ServicesPage() {
  const range = useTimeRange();
  const [params] = useSearchParams();
  const filter = params.get('name')?.toLowerCase() ?? '';

  const services = useQuery({
    queryKey: ['services', range.presetKey],
    queryFn: () => api<ServiceSummary[]>(`/v1/services?${buildRangeQuery(range.from, range.to)}`),
    placeholderData: keepPreviousData,
  });

  const sparklines = useQuery({
    queryKey: ['services-sparklines', range.presetKey],
    queryFn: () => api<EntitySparkline[]>(`/v1/services/sparklines?${buildRangeQuery(range.from, range.to, { buckets: 30 })}`),
    placeholderData: keepPreviousData,
  });

  const sparkByService = useMemo(() => {
    const m = new Map<string, EntitySparkline>();
    sparklines.data?.forEach(s => m.set(s.key, s));
    return m;
  }, [sparklines.data]);

  const filtered = services.data?.filter(s => !filter || s.serviceName.toLowerCase().includes(filter)) ?? [];
  const maxRequests = Math.max(1, ...filtered.map(s => s.spanCount));

  return (
    <>
      <h1 className="page-title">
        Services
        <TimeRangePicker />
      </h1>

      <div className="card" style={{ padding: 0 }}>
        {services.isLoading ? <Loading /> :
         services.error    ? <ErrorBlock error={services.error} /> :
         filtered.length === 0 ? <Empty label="No services seen in this time range." /> : (
          <table>
            <thead>
              <tr>
                <th>Service</th>
                <th>Activity</th>
                <th>Version</th>
                <th style={{ textAlign: 'right', width: 160 }}>Requests</th>
                <th style={{ textAlign: 'right' }}>Errors</th>
                <th style={{ textAlign: 'right' }}>Error rate</th>
                <th style={{ textAlign: 'right' }}>p50</th>
                <th style={{ textAlign: 'right' }}>p90</th>
                <th style={{ textAlign: 'right' }}>p99</th>
                <th>Last seen</th>
              </tr>
            </thead>
            <tbody>
              {filtered.map(s => {
                const errBadge = s.errorRate >= 0.05 ? 'err' : s.errorRate >= 0.01 ? 'warn' : 'ok';
                const spark = sparkByService.get(s.serviceName);
                return (
                  <tr key={s.serviceName}>
                    <td style={{ fontWeight: 500 }}>
                      <Link to={`/traces?service=${encodeURIComponent(s.serviceName)}`}>{s.serviceName}</Link>
                    </td>
                    <td>
                      {spark
                        ? <Sparkline counts={spark.counts} errors={spark.errors} />
                        : <span className="faint">—</span>}
                    </td>
                    <td className="muted">{s.serviceVersion || '—'}</td>
                    <td style={{ textAlign: 'right' }}>
                      <DataBar value={s.spanCount} max={maxRequests} />
                    </td>
                    <td style={{ textAlign: 'right', color: s.errorCount > 0 ? 'var(--err)' : undefined }}>
                      {s.errorCount.toLocaleString()}
                    </td>
                    <td style={{ textAlign: 'right' }}>
                      <span className={`badge ${errBadge}`}>{(s.errorRate * 100).toFixed(2)}%</span>
                    </td>
                    <td style={{ textAlign: 'right' }}>{formatMs(s.latencyP50Ms)}</td>
                    <td style={{ textAlign: 'right' }}>{formatMs(s.latencyP90Ms)}</td>
                    <td style={{ textAlign: 'right' }}>{formatMs(s.latencyP99Ms)}</td>
                    <td><TimeAgo iso={s.lastSeenUtc} /></td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </div>
    </>
  );
}
