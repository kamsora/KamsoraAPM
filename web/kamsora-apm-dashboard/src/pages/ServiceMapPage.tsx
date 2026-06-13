import { keepPreviousData, useQuery } from '@tanstack/react-query';
import ReactECharts from 'echarts-for-react';
import { useMemo } from 'react';
import { useNavigate } from 'react-router-dom';
import { api, buildRangeQuery } from '../api/client';
import type { ServiceMapNode, ServiceMapResult } from '../api/types';
import { Empty, ErrorBlock, Loading } from '../components/Loading';
import { TimeRangePicker, useTimeRange } from '../components/TimeRangePicker';

const KIND_SYMBOL: Record<string, string> = {
  service:  'circle',
  database: 'rect',
  external: 'diamond',
};

export default function ServiceMapPage() {
  const range = useTimeRange();
  const navigate = useNavigate();

  const map = useQuery({
    queryKey: ['service-map', range.presetKey],
    queryFn:  () => api<ServiceMapResult>(`/v1/services/map?${buildRangeQuery(range.from, range.to)}`),
    placeholderData: keepPreviousData,
    refetchInterval: 30_000,
  });

  const totals = useMemo(() => {
    const nodes = map.data?.nodes ?? [];
    return {
      services:  nodes.filter(n => n.kind === 'service').length,
      databases: nodes.filter(n => n.kind === 'database').length,
      external:  nodes.filter(n => n.kind === 'external').length,
      edges:     map.data?.edges.length ?? 0,
    };
  }, [map.data]);

  return (
    <>
      <h1 className="page-title">
        Service Map
        <TimeRangePicker />
      </h1>

      <div className="stat-grid" style={{ marginBottom: 12 }}>
        <Stat label="Services"  value={totals.services} />
        <Stat label="Databases" value={totals.databases} />
        <Stat label="External"  value={totals.external} />
        <Stat label="Edges"     value={totals.edges} />
      </div>

      <div className="card" style={{ padding: 8 }}>
        {map.isLoading ? <Loading /> :
         map.error    ? <ErrorBlock error={map.error} /> :
         (map.data?.nodes.length ?? 0) === 0
           ? <Empty label="No spans in this window - the map draws itself from trace data." />
           : <MapGraph data={map.data!} onServiceClick={() => navigate('/services')} />}
      </div>

      <p className="faint" style={{ fontSize: 12, marginTop: 8 }}>
        Circles are services, squares are databases, diamonds are external HTTP hosts.
        Node size tracks call volume; red tint tracks error rate. Drag to rearrange, scroll to zoom.
      </p>
    </>
  );
}

function MapGraph({ data, onServiceClick }: { data: ServiceMapResult; onServiceClick: (name: string) => void }) {
  const maxCalls = Math.max(1, ...data.nodes.map(n => n.callCount));

  const option = {
    tooltip: {
      formatter: (params: { dataType: string; data: Record<string, unknown> }) => {
        if (params.dataType === 'edge') {
          const e = params.data;
          return `${e.sourceLabel} → ${e.targetLabel}<br/>` +
                 `calls: ${(e.calls as number).toLocaleString()}<br/>` +
                 `errors: ${(e.errors as number).toLocaleString()}<br/>` +
                 `avg latency: ${(e.avgMs as number).toFixed(1)} ms`;
        }
        const n = params.data;
        return `<b>${n.name}</b> (${n.kind})<br/>` +
               `calls: ${(n.calls as number).toLocaleString()}<br/>` +
               `errors: ${(n.errors as number).toLocaleString()}<br/>` +
               `p50: ${(n.p50 as number).toFixed(1)} ms`;
      },
    },
    series: [{
      type: 'graph',
      layout: 'force',
      roam: true,
      draggable: true,
      force: { repulsion: 420, edgeLength: [120, 220], gravity: 0.08 },
      emphasis: { focus: 'adjacency', lineStyle: { width: 4 } },
      label: { show: true, position: 'bottom', color: '#E7ECF6', fontSize: 12 },
      edgeSymbol: ['none', 'arrow'],
      edgeSymbolSize: 8,
      data: data.nodes.map(n => ({
        id: n.id,
        name: n.label,
        kind: n.kind,
        calls: n.callCount,
        errors: n.errorCount,
        p50: n.latencyP50Ms,
        symbol: KIND_SYMBOL[n.kind] ?? 'circle',
        symbolSize: 26 + Math.sqrt(n.callCount / maxCalls) * 38,
        itemStyle: { color: nodeColor(n) },
      })),
      edges: data.edges.map(e => {
        const src = data.nodes.find(n => n.id === e.sourceId);
        const tgt = data.nodes.find(n => n.id === e.targetId);
        const errRate = e.callCount > 0 ? e.errorCount / e.callCount : 0;
        return {
          source: e.sourceId,
          target: e.targetId,
          sourceLabel: src?.label ?? e.sourceId,
          targetLabel: tgt?.label ?? e.targetId,
          calls: e.callCount,
          errors: e.errorCount,
          avgMs: e.avgLatencyMs,
          lineStyle: {
            width: 1 + Math.min(5, Math.log10(1 + e.callCount)),
            color: errRate > 0.05 ? '#EF4444' : '#5B7199',
            curveness: 0.12,
          },
        };
      }),
    }],
  };

  return (
    <ReactECharts
      option={option}
      style={{ height: 560 }}
      theme="dark"
      onEvents={{
        click: (params: { dataType?: string; data?: { kind?: string; name?: string } }) => {
          if (params.dataType === 'node' && params.data?.kind === 'service' && params.data.name) {
            onServiceClick(params.data.name);
          }
        },
      }}
    />
  );
}

function nodeColor(n: ServiceMapNode): string {
  if (n.kind === 'database') return '#5B89FF';
  if (n.kind === 'external') return '#A855F7';
  const errRate = n.callCount > 0 ? n.errorCount / n.callCount : 0;
  if (errRate > 0.05) return '#EF4444';
  if (errRate > 0.01) return '#F59E0B';
  return '#34D399';
}

function Stat({ label, value }: { label: string; value: number }) {
  return (
    <div className="card">
      <h3 className="card-title">{label}</h3>
      <div className="stat-value">{value.toLocaleString()}</div>
    </div>
  );
}
