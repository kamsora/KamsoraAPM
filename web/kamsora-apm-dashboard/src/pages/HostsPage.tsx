import { keepPreviousData, useQuery } from '@tanstack/react-query';
import ReactECharts from 'echarts-for-react';
import { useState } from 'react';
import { api, buildRangeQuery } from '../api/client';
import type {
  HostDiskPoint, HostNetworkPoint, HostProcessSummary,
  HostSummary, HostUtilizationPoint,
} from '../api/types';
import { ErrorBlock, Loading, Empty } from '../components/Loading';
import { TimeRangePicker, useTimeRange } from '../components/TimeRangePicker';

export default function HostsPage() {
  const range = useTimeRange();
  const [selectedHostId, setSelectedHostId] = useState<string | null>(null);

  const hosts = useQuery({
    queryKey: ['hosts', range.presetKey],
    queryFn: () => api<HostSummary[]>(`/v1/hosts?${buildRangeQuery(range.from, range.to)}`),
    placeholderData: keepPreviousData,
  });

  // Default selection: the most-recently-seen host.
  const activeHostId = selectedHostId ?? hosts.data?.[0]?.hostId ?? null;
  const activeHost   = hosts.data?.find(h => h.hostId === activeHostId);

  const util = useQuery({
    enabled: Boolean(activeHostId),
    queryKey: ['host-util', activeHostId, range.presetKey],
    queryFn: () => api<HostUtilizationPoint[]>(
      `/v1/hosts/${encodeURIComponent(activeHostId!)}/utilization?${buildRangeQuery(range.from, range.to, { bucketSeconds: pickBucket(range.from, range.to) })}`),
    placeholderData: keepPreviousData,
  });

  const disks = useQuery({
    enabled: Boolean(activeHostId),
    queryKey: ['host-disks', activeHostId, range.presetKey],
    queryFn: () => api<HostDiskPoint[]>(
      `/v1/hosts/${encodeURIComponent(activeHostId!)}/disks?${buildRangeQuery(range.from, range.to, { bucketSeconds: pickBucket(range.from, range.to) })}`),
    placeholderData: keepPreviousData,
  });

  const networks = useQuery({
    enabled: Boolean(activeHostId),
    queryKey: ['host-networks', activeHostId, range.presetKey],
    queryFn: () => api<HostNetworkPoint[]>(
      `/v1/hosts/${encodeURIComponent(activeHostId!)}/networks?${buildRangeQuery(range.from, range.to, { bucketSeconds: pickBucket(range.from, range.to) })}`),
    placeholderData: keepPreviousData,
  });

  const processes = useQuery({
    enabled: Boolean(activeHostId),
    queryKey: ['host-processes', activeHostId],
    queryFn: () => api<HostProcessSummary[]>(
      `/v1/hosts/${encodeURIComponent(activeHostId!)}/processes?limit=50`),
    refetchInterval: 15_000,
    placeholderData: keepPreviousData,
  });

  return (
    <>
      <h1 className="page-title">
        Hosts
        <TimeRangePicker />
      </h1>

      <div className="card" style={{ padding: 0, marginBottom: 24 }}>
        {hosts.isLoading ? <Loading /> :
         hosts.error    ? <ErrorBlock error={hosts.error} /> :
         (hosts.data?.length ?? 0) === 0 ? <Empty label="No hosts have reported in this time range. Install KamsoraAPM.HostMonitor on a server and configure its tenant/api-key." /> : (
          <table>
            <thead>
              <tr>
                <th>Host</th>
                <th>OS</th>
                <th style={{ textAlign: 'right' }}>Cores</th>
                <th style={{ textAlign: 'right' }}>CPU</th>
                <th style={{ textAlign: 'right' }}>Memory</th>
                <th style={{ textAlign: 'right' }}>Samples</th>
                <th>Last seen</th>
              </tr>
            </thead>
            <tbody>
              {hosts.data!.map(h => {
                const cpuBadge = h.cpuUtilization >= 0.85 ? 'err' : h.cpuUtilization >= 0.7 ? 'warn' : 'ok';
                const memBadge = h.memUtilization >= 0.9  ? 'err' : h.memUtilization >= 0.75 ? 'warn' : 'ok';
                const isActive = h.hostId === activeHostId;
                return (
                  <tr
                    key={h.hostId}
                    className="clickable"
                    onClick={() => setSelectedHostId(h.hostId)}
                    style={isActive ? { background: 'var(--bg-surface-2)' } : undefined}
                    title="Click to see this host's utilization timeseries"
                  >
                    <td>
                      <div>{h.hostName || h.hostId}</div>
                      <div className="mono faint" style={{ fontSize: 11 }}>{h.hostId.slice(0, 12)}...</div>
                    </td>
                    <td>{h.osType || '-'} <span className="faint">{h.osVersion}</span></td>
                    <td style={{ textAlign: 'right' }}>{h.logicalCores || '-'}</td>
                    <td style={{ textAlign: 'right' }}>
                      <span className={`badge ${cpuBadge}`}>{(h.cpuUtilization * 100).toFixed(1)}%</span>
                    </td>
                    <td style={{ textAlign: 'right' }}>
                      <span className={`badge ${memBadge}`}>{(h.memUtilization * 100).toFixed(1)}%</span>
                      <span className="faint" style={{ marginLeft: 6, fontSize: 11 }}>
                        {formatBytes(h.memUsedBytes)} / {formatBytes(h.memTotalBytes)}
                      </span>
                    </td>
                    <td style={{ textAlign: 'right' }}>{h.sampleCount.toLocaleString()}</td>
                    <td className="mono faint">{new Date(h.lastSeenUtc).toISOString().replace('T', ' ').replace('Z', '')}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </div>

      {activeHost && (
        <>
          <div className="card" style={{ marginBottom: 24 }}>
            <h3 className="card-title">CPU utilization - {activeHost.hostName || activeHost.hostId}</h3>
            {util.isLoading ? <Loading /> :
             util.error    ? <ErrorBlock error={util.error} /> :
             !util.data || util.data.length === 0 ? <Empty /> :
             <CpuChart points={util.data} />}
          </div>

          <div className="card" style={{ marginBottom: 24 }}>
            <h3 className="card-title">Memory utilization - {activeHost.hostName || activeHost.hostId}</h3>
            {util.isLoading ? <Loading /> :
             util.error    ? <ErrorBlock error={util.error} /> :
             !util.data || util.data.length === 0 ? <Empty /> :
             <MemoryChart points={util.data} />}
          </div>

          <div className="card" style={{ marginBottom: 24 }}>
            <h3 className="card-title">Disk I/O throughput - {activeHost.hostName || activeHost.hostId}</h3>
            {disks.isLoading ? <Loading /> :
             disks.error    ? <ErrorBlock error={disks.error} /> :
             !disks.data || disks.data.length === 0 ? <Empty label="No disk samples in this window." /> :
             <DiskIoChart points={disks.data} />}
            {disks.data && disks.data.length > 0 && <DiskCapacityTable points={disks.data} />}
          </div>

          <div className="card" style={{ marginBottom: 24 }}>
            <h3 className="card-title">Network throughput - {activeHost.hostName || activeHost.hostId}</h3>
            {networks.isLoading ? <Loading /> :
             networks.error    ? <ErrorBlock error={networks.error} /> :
             !networks.data || networks.data.length === 0 ? <Empty label="No network samples in this window." /> :
             <NetworkChart points={networks.data} />}
          </div>

          <div className="card" style={{ padding: 0 }}>
            <h3 className="card-title" style={{ padding: '12px 16px 0' }}>
              Top processes - {activeHost.hostName || activeHost.hostId}
              <span className="muted" style={{ marginLeft: 8, fontSize: 12, fontWeight: 'normal' }}>
                live (15 s refresh)
              </span>
            </h3>
            {processes.isLoading ? <Loading /> :
             processes.error    ? <ErrorBlock error={processes.error} /> :
             !processes.data || processes.data.length === 0 ? <Empty label="No process samples yet." /> :
             <ProcessTable rows={processes.data} />}
          </div>
        </>
      )}
    </>
  );
}

// -------------- Disk I/O --------------

function DiskIoChart({ points }: { points: HostDiskPoint[] }) {
  // Pivot rows (bucket, device) into per-device series indexed by bucket.
  const buckets = Array.from(new Set(points.map(p => p.bucketStartUtc))).sort();
  const devices = Array.from(new Set(points.map(p => p.device))).sort();
  const x = buckets.map(b => new Date(b).toISOString().slice(11, 19));

  const series: any[] = [];
  const palette = ['#22d3ee', '#a78bfa', '#f59e0b', '#34d399', '#f43f5e', '#60a5fa'];
  devices.forEach((dev, idx) => {
    const color  = palette[idx % palette.length];
    const map    = new Map(points.filter(p => p.device === dev).map(p => [p.bucketStartUtc, p]));
    const reads  = buckets.map(b => +(((map.get(b)?.readBytesPerSecAvg  ?? 0) / 1024 / 1024).toFixed(3)));
    const writes = buckets.map(b => +(((map.get(b)?.writeBytesPerSecAvg ?? 0) / 1024 / 1024).toFixed(3)));
    series.push({ name: `${dev} read`,  type: 'line', smooth: true, data: reads,  lineStyle: { color, type: 'solid'  } });
    series.push({ name: `${dev} write`, type: 'line', smooth: true, data: writes, lineStyle: { color, type: 'dashed' } });
  });

  return (
    <ReactECharts
      theme="dark"
      style={{ height: 280 }}
      option={{
        animation: false,
        tooltip: { trigger: 'axis', valueFormatter: (v: number) => `${v.toFixed(2)} MiB/s` },
        legend:  { textStyle: { color: '#cbd5e1' } },
        grid:    { left: 56, right: 24, top: 32, bottom: 32 },
        xAxis:   { type: 'category', data: x, axisLabel: { color: '#94a3b8' } },
        yAxis:   { type: 'value', name: 'MiB/s', axisLabel: { color: '#94a3b8' } },
        series,
      }}
    />
  );
}

function DiskCapacityTable({ points }: { points: HostDiskPoint[] }) {
  // Latest bucket per device gives the freshest capacity figures.
  const latest = new Map<string, HostDiskPoint>();
  for (const p of points) {
    const cur = latest.get(p.device);
    if (!cur || cur.bucketStartUtc < p.bucketStartUtc) latest.set(p.device, p);
  }
  const rows = Array.from(latest.values()).sort((a, b) => a.device.localeCompare(b.device));

  return (
    <table style={{ marginTop: 12 }}>
      <thead>
        <tr>
          <th>Device</th>
          <th style={{ textAlign: 'right' }}>Used</th>
          <th style={{ textAlign: 'right' }}>Total</th>
          <th style={{ textAlign: 'right' }}>Used %</th>
          <th style={{ textAlign: 'right' }}>Read peak</th>
          <th style={{ textAlign: 'right' }}>Write peak</th>
          <th style={{ textAlign: 'right' }}>Read IOPS</th>
          <th style={{ textAlign: 'right' }}>Write IOPS</th>
        </tr>
      </thead>
      <tbody>
        {rows.map(r => {
          const usedPct = r.totalBytes > 0 ? r.usedBytes / r.totalBytes : 0;
          const badge   = usedPct >= 0.9 ? 'err' : usedPct >= 0.75 ? 'warn' : 'ok';
          return (
            <tr key={r.device}>
              <td className="mono">{r.device}</td>
              <td style={{ textAlign: 'right' }}>{formatBytes(r.usedBytes)}</td>
              <td style={{ textAlign: 'right' }}>{formatBytes(r.totalBytes)}</td>
              <td style={{ textAlign: 'right' }}>
                <span className={`badge ${badge}`}>{(usedPct * 100).toFixed(1)}%</span>
              </td>
              <td style={{ textAlign: 'right' }}>{formatRate(r.readBytesPerSecMax)}</td>
              <td style={{ textAlign: 'right' }}>{formatRate(r.writeBytesPerSecMax)}</td>
              <td style={{ textAlign: 'right' }}>{r.readsPerSecAvg.toLocaleString()}</td>
              <td style={{ textAlign: 'right' }}>{r.writesPerSecAvg.toLocaleString()}</td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}

// -------------- Network --------------

function NetworkChart({ points }: { points: HostNetworkPoint[] }) {
  const buckets = Array.from(new Set(points.map(p => p.bucketStartUtc))).sort();
  const ifaces  = Array.from(new Set(points.map(p => p.interfaceName))).sort();
  const x = buckets.map(b => new Date(b).toISOString().slice(11, 19));

  const series: any[] = [];
  const palette = ['#22d3ee', '#a78bfa', '#f59e0b', '#34d399', '#f43f5e', '#60a5fa'];
  ifaces.forEach((iface, idx) => {
    const color = palette[idx % palette.length];
    const map   = new Map(points.filter(p => p.interfaceName === iface).map(p => [p.bucketStartUtc, p]));
    const rx    = buckets.map(b => +(((map.get(b)?.rxBytesPerSecAvg ?? 0) / 1024 / 1024).toFixed(3)));
    const tx    = buckets.map(b => +(((map.get(b)?.txBytesPerSecAvg ?? 0) / 1024 / 1024).toFixed(3)));
    series.push({ name: `${shortenIface(iface)} rx`, type: 'line', smooth: true, data: rx, lineStyle: { color, type: 'solid'  } });
    series.push({ name: `${shortenIface(iface)} tx`, type: 'line', smooth: true, data: tx, lineStyle: { color, type: 'dashed' } });
  });

  return (
    <ReactECharts
      theme="dark"
      style={{ height: 280 }}
      option={{
        animation: false,
        tooltip: { trigger: 'axis', valueFormatter: (v: number) => `${v.toFixed(2)} MiB/s` },
        legend:  { textStyle: { color: '#cbd5e1' } },
        grid:    { left: 56, right: 24, top: 32, bottom: 32 },
        xAxis:   { type: 'category', data: x, axisLabel: { color: '#94a3b8' } },
        yAxis:   { type: 'value', name: 'MiB/s', axisLabel: { color: '#94a3b8' } },
        series,
      }}
    />
  );
}

// -------------- Processes --------------

function ProcessTable({ rows }: { rows: HostProcessSummary[] }) {
  return (
    <table>
      <thead>
        <tr>
          <th style={{ textAlign: 'right' }}>PID</th>
          <th>Process</th>
          <th>Service</th>
          <th style={{ textAlign: 'right' }}>CPU</th>
          <th style={{ textAlign: 'right' }}>RSS</th>
          <th style={{ textAlign: 'right' }}>Threads</th>
          <th style={{ textAlign: 'right' }}>Handles</th>
        </tr>
      </thead>
      <tbody>
        {rows.map(r => {
          const badge = r.cpuUtilization >= 0.5 ? 'err' : r.cpuUtilization >= 0.2 ? 'warn' : 'ok';
          return (
            <tr key={`${r.pid}-${r.command}`}>
              <td className="mono" style={{ textAlign: 'right' }}>{r.pid}</td>
              <td>
                <div>{r.command}</div>
                {r.runtimeVersion && <div className="faint" style={{ fontSize: 11 }}>.NET {r.runtimeVersion}</div>}
              </td>
              <td className="faint">{r.serviceName || '-'}</td>
              <td style={{ textAlign: 'right' }}>
                <span className={`badge ${badge}`}>{(r.cpuUtilization * 100).toFixed(1)}%</span>
              </td>
              <td style={{ textAlign: 'right' }}>{formatBytes(r.rssBytes)}</td>
              <td style={{ textAlign: 'right' }}>{r.threadCount.toLocaleString()}</td>
              <td style={{ textAlign: 'right' }}>{r.handleCount.toLocaleString()}</td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}

function shortenIface(name: string): string {
  // Windows NIC names can be very long (vendor + adapter index). Trim aggressively.
  return name.length > 28 ? `${name.slice(0, 26)}...` : name;
}

function formatRate(bytesPerSec: number): string {
  if (!Number.isFinite(bytesPerSec) || bytesPerSec <= 0) return '-';
  return `${formatBytes(bytesPerSec)}/s`;
}

function CpuChart({ points }: { points: HostUtilizationPoint[] }) {
  const x   = points.map(p => new Date(p.bucketStartUtc).toISOString().slice(11, 19));
  const avg = points.map(p => +(p.cpuUserAvg * 100).toFixed(2));
  const max = points.map(p => +(p.cpuUserMax * 100).toFixed(2));

  return (
    <ReactECharts
      theme="dark"
      style={{ height: 280 }}
      option={{
        animation: false,
        tooltip: { trigger: 'axis' },
        legend: { data: ['avg %', 'max %'], textStyle: { color: '#cbd5e1' } },
        grid:   { left: 48, right: 24, top: 32, bottom: 32 },
        xAxis:  { type: 'category', data: x, axisLabel: { color: '#94a3b8' } },
        yAxis:  { type: 'value', name: 'CPU %', max: 100, axisLabel: { color: '#94a3b8' } },
        series: [
          { name: 'avg %', type: 'line', smooth: true, data: avg, lineStyle: { color: '#22d3ee' }, areaStyle: { opacity: 0.15, color: '#22d3ee' } },
          { name: 'max %', type: 'line', smooth: true, data: max, lineStyle: { color: '#f97316' } },
        ],
      }}
    />
  );
}

function MemoryChart({ points }: { points: HostUtilizationPoint[] }) {
  const x        = points.map(p => new Date(p.bucketStartUtc).toISOString().slice(11, 19));
  const totalGb  = points.map(p => +(p.memTotalBytes  / 1024 / 1024 / 1024).toFixed(2));
  const usedAvg  = points.map(p => +(p.memUsedBytesAvg / 1024 / 1024 / 1024).toFixed(2));
  const usedMax  = points.map(p => +(p.memUsedBytesMax / 1024 / 1024 / 1024).toFixed(2));

  return (
    <ReactECharts
      theme="dark"
      style={{ height: 280 }}
      option={{
        animation: false,
        tooltip: { trigger: 'axis', valueFormatter: (v: number) => `${v.toFixed(2)} GiB` },
        legend: { data: ['used avg', 'used max', 'total'], textStyle: { color: '#cbd5e1' } },
        grid:   { left: 56, right: 24, top: 32, bottom: 32 },
        xAxis:  { type: 'category', data: x, axisLabel: { color: '#94a3b8' } },
        yAxis:  { type: 'value', name: 'GiB', axisLabel: { color: '#94a3b8' } },
        series: [
          { name: 'used avg', type: 'line', smooth: true, data: usedAvg, lineStyle: { color: '#a78bfa' }, areaStyle: { opacity: 0.18, color: '#a78bfa' } },
          { name: 'used max', type: 'line', smooth: true, data: usedMax, lineStyle: { color: '#f43f5e' } },
          { name: 'total',    type: 'line',                data: totalGb, lineStyle: { color: '#64748b', type: 'dashed' } },
        ],
      }}
    />
  );
}

function pickBucket(from: Date, to: Date): number {
  const spanSec = Math.max(60, (to.getTime() - from.getTime()) / 1000);
  if (spanSec <=    60 * 60)        return  30;   // <= 1 h  -> 30 s buckets
  if (spanSec <=  6  * 60 * 60)     return  60;   // <= 6 h  -> 1 m
  if (spanSec <= 24  * 60 * 60)     return 300;   // <= 24 h -> 5 m
  return 900;                                     // > 1 d   -> 15 m
}

function formatBytes(n: number): string {
  if (!Number.isFinite(n) || n <= 0) return '-';
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB'];
  let i = 0;
  let v = n;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return `${v.toFixed(v >= 100 ? 0 : v >= 10 ? 1 : 2)} ${units[i]}`;
}
