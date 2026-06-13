import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../api/client';
import type {
  AlertChannelDto, AlertOperator, AlertRuleDto, AlertSeverity, AlertSignalType,
  CreateAlertRuleRequest, UpdateAlertRuleRequest,
} from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Empty, ErrorBlock, Loading } from '../components/Loading';
import { ModalShell } from './PlatformPage';

const SIGNAL_LABELS: Record<AlertSignalType, string> = {
  latency_p50:    'p50 latency (ms)',
  latency_p90:    'p90 latency (ms)',
  latency_p99:    'p99 latency (ms)',
  error_rate:     'Error rate (0..1)',
  request_volume: 'Request count (per window)',
  log_count:      'Log count at/above severity (per window)',
  metric_avg:     'Metric average (custom metric)',
  metric_max:     'Metric max (custom metric)',
};

const LOG_SEVERITIES = ['TRACE', 'DEBUG', 'INFO', 'WARN', 'ERROR', 'FATAL'] as const;
const isMetricSignal = (s: AlertSignalType) => s === 'metric_avg' || s === 'metric_max';

export default function AlertRulesPage() {
  const { role } = useAuth();
  const [editing, setEditing] = useState<AlertRuleDto | 'new' | null>(null);

  const rules = useQuery({
    queryKey: ['alert-rules'],
    queryFn:  () => api<AlertRuleDto[]>('/v1/alerts/rules'),
    placeholderData: keepPreviousData,
    refetchInterval: 15_000,
  });

  if (role !== 'owner') {
    return (
      <>
        <h1 className="page-title">Alert rules</h1>
        <div className="card"><ErrorBlock error={new Error('Only tenant owners can manage alert rules.')} /></div>
      </>
    );
  }

  return (
    <>
      <h1 className="page-title">
        Alert rules
        <button onClick={() => setEditing('new')} style={{ marginLeft: 12 }}>+ New rule</button>
      </h1>

      <div className="card" style={{ padding: 0 }}>
        {rules.isLoading ? <Loading /> :
         rules.error    ? <ErrorBlock error={rules.error} /> :
         (rules.data?.length ?? 0) === 0 ? <Empty label="No alert rules yet. Click + New rule to create one." /> : (
          <table>
            <thead>
              <tr>
                <th>Rule</th>
                <th>Signal</th>
                <th>Condition</th>
                <th>Window</th>
                <th>State</th>
                <th>Severity</th>
                <th>Last value</th>
                <th style={{ textAlign: 'right' }}>Actions</th>
              </tr>
            </thead>
            <tbody>
              {rules.data!.map(r => <RuleRow key={r.sysRuleTransId} rule={r} onEdit={() => setEditing(r)} />)}
            </tbody>
          </table>
        )}
      </div>

      {editing && (
        <RuleEditorModal
          mode={editing === 'new' ? 'create' : 'edit'}
          initial={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
        />
      )}
    </>
  );
}

function RuleRow({ rule, onEdit }: { rule: AlertRuleDto; onEdit: () => void }) {
  const [confirming, setConfirming] = useState(false);
  const qc = useQueryClient();
  const del = useMutation({
    mutationFn: () => api<void>(`/v1/alerts/rules/${encodeURIComponent(rule.sysRuleTransId)}`, { method: 'DELETE' }),
    onSuccess:  () => qc.invalidateQueries({ queryKey: ['alert-rules'] }),
  });

  return (
    <tr style={{ opacity: rule.enabled ? 1 : 0.55 }}>
      <td>
        <div style={{ fontWeight: 500 }}>{rule.ruleName}</div>
        {rule.description && <div className="faint" style={{ fontSize: 12 }}>{rule.description}</div>}
        {rule.serviceFilter && <span className="badge muted mono" style={{ fontSize: 11, marginTop: 4 }}>svc = {rule.serviceFilter}</span>}
      </td>
      <td>
        <span className="badge muted mono">{rule.signalType}</span>
        {rule.signalParam && (
          <div className="faint mono" style={{ fontSize: 11, marginTop: 4, wordBreak: 'break-all' }}>
            {rule.signalType === 'log_count' ? `≥ ${rule.signalParam}` : rule.signalParam}
          </div>
        )}
      </td>
      <td className="mono">{humanOperator(rule.operator)} {formatValue(rule.signalType, rule.threshold)}</td>
      <td className="mono faint">{formatSec(rule.windowSeconds)} · sustained {formatSec(rule.forSeconds)}</td>
      <td><span className={`badge ${stateClass(rule.lastState)}`}>{rule.lastState}</span></td>
      <td><span className={`badge ${severityClass(rule.severity)}`}>{rule.severity}</span></td>
      <td className="mono faint">{rule.lastValue !== null ? formatValue(rule.signalType, rule.lastValue) : '—'}</td>
      <td style={{ textAlign: 'right' }}>
        <button className="secondary" onClick={onEdit} style={{ fontSize: 11, marginRight: 4 }}>Edit</button>
        {confirming ? (
          <>
            <button className="secondary" onClick={() => setConfirming(false)} disabled={del.isPending} style={{ fontSize: 11, marginRight: 4 }}>Cancel</button>
            <button onClick={() => del.mutate()} disabled={del.isPending} style={{ fontSize: 11, background: '#ef4444' }}>
              {del.isPending ? '…' : 'Confirm delete'}
            </button>
          </>
        ) : (
          <button className="secondary" onClick={() => setConfirming(true)} style={{ fontSize: 11 }}>Delete</button>
        )}
      </td>
    </tr>
  );
}

function RuleEditorModal({ mode, initial, onClose }: {
  mode: 'create' | 'edit';
  initial: AlertRuleDto | null;
  onClose: () => void;
}) {
  const qc = useQueryClient();
  const [ruleName,       setRuleName]      = useState(initial?.ruleName ?? '');
  const [description,    setDescription]   = useState(initial?.description ?? '');
  const [enabled,        setEnabled]       = useState(initial?.enabled ?? true);
  const [signalType,     setSignalType]    = useState<AlertSignalType>(initial?.signalType ?? 'latency_p99');
  const [signalParam,    setSignalParam]   = useState(initial?.signalParam ?? '');
  const [serviceFilter,  setServiceFilter] = useState(initial?.serviceFilter ?? '');
  const [operator,       setOperator]      = useState<AlertOperator>(initial?.operator ?? 'gt');
  const [threshold,      setThreshold]     = useState<number>(initial?.threshold ?? 500);
  const [windowSeconds,  setWindowSeconds] = useState<number>(initial?.windowSeconds ?? 300);
  const [forSeconds,     setForSeconds]    = useState<number>(initial?.forSeconds ?? 120);
  const [severity,       setSeverity]      = useState<AlertSeverity>(initial?.severity ?? 'warning');
  const [channelUuids,   setChannelUuids]  = useState<string[]>(initial?.channelUuids ?? []);
  const [error,          setError]         = useState<string | null>(null);

  const channels = useQuery({
    queryKey: ['alert-channels'],
    queryFn:  () => api<AlertChannelDto[]>('/v1/alerts/channels'),
  });

  const mutation = useMutation({
    mutationFn: async () => {
      const payload: CreateAlertRuleRequest | UpdateAlertRuleRequest = {
        RuleName:      ruleName.trim(),
        Description:   description.trim() || undefined,
        SignalType:    signalType,
        SignalParam:   signalParam.trim() || undefined,
        ServiceFilter: serviceFilter.trim() || undefined,
        Operator:      operator,
        Threshold:     Number(threshold),
        WindowSeconds: Number(windowSeconds),
        ForSeconds:    Number(forSeconds),
        Severity:      severity,
        ChannelUuids:  channelUuids,
        ...(mode === 'edit' ? { Enabled: enabled } : {}),
      } as any;

      if (mode === 'edit' && initial) {
        await api<void>(`/v1/alerts/rules/${encodeURIComponent(initial.sysRuleTransId)}`, {
          method: 'PUT', body: JSON.stringify(payload),
        });
      } else {
        await api<{ sysRuleTransId: string }>('/v1/alerts/rules', {
          method: 'POST', body: JSON.stringify(payload),
        });
      }
    },
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['alert-rules'] }); onClose(); },
    onError:   (err: any) => setError(err?.body?.error ?? err?.message ?? 'Save failed'),
  });

  return (
    <ModalShell title={mode === 'create' ? 'New alert rule' : `Edit rule "${initial?.ruleName}"`} onClose={onClose} wide>
      <div style={{ display: 'grid', gap: 12 }}>
        <Field label="Rule name" value={ruleName} onChange={setRuleName} placeholder="e.g. p99 latency > 1s on checkout-api" />
        <Field label="Description (optional)" value={description} onChange={setDescription} placeholder="What does this rule mean?" />

        <div style={{ display: 'flex', gap: 12 }}>
          <label style={{ display: 'grid', gap: 4, flex: 1 }}>
            <span className="muted" style={{ fontSize: 12 }}>Signal</span>
            <select value={signalType} onChange={e => {
              const next = e.target.value as AlertSignalType;
              setSignalType(next);
              // Reasonable defaults per signal.
              if (next === 'error_rate')          setThreshold(0.05);
              else if (next === 'request_volume') setThreshold(10000);
              else if (next === 'log_count')      setThreshold(100);
              else if (isMetricSignal(next))      setThreshold(0);
              else                                setThreshold(500);
              // Param resets when the signal family changes.
              if (next === 'log_count')           setSignalParam('ERROR');
              else if (!isMetricSignal(next))     setSignalParam('');
            }}>
              {Object.entries(SIGNAL_LABELS).map(([v, label]) => (
                <option key={v} value={v}>{label}</option>
              ))}
            </select>
          </label>
          {signalType === 'log_count' && (
            <label style={{ display: 'grid', gap: 4, flex: 0.8 }}>
              <span className="muted" style={{ fontSize: 12 }}>Severity floor</span>
              <select value={signalParam || 'ERROR'} onChange={e => setSignalParam(e.target.value)}>
                {LOG_SEVERITIES.map(s => <option key={s} value={s}>≥ {s}</option>)}
              </select>
            </label>
          )}
          {isMetricSignal(signalType) && (
            <label style={{ display: 'grid', gap: 4, flex: 1.2 }}>
              <span className="muted" style={{ fontSize: 12 }}>Metric name (required)</span>
              <input value={signalParam} onChange={e => setSignalParam(e.target.value)}
                placeholder="e.g. process.runtime.dotnet.gc.heap.size" className="mono" />
            </label>
          )}
          <label style={{ display: 'grid', gap: 4, flex: 0.6 }}>
            <span className="muted" style={{ fontSize: 12 }}>Operator</span>
            <select value={operator} onChange={e => setOperator(e.target.value as AlertOperator)}>
              <option value="gt">&gt;</option>
              <option value="gte">≥</option>
              <option value="lt">&lt;</option>
              <option value="lte">≤</option>
              <option value="eq">=</option>
            </select>
          </label>
          <label style={{ display: 'grid', gap: 4, flex: 1 }}>
            <span className="muted" style={{ fontSize: 12 }}>Threshold</span>
            <input type="number" value={threshold} step="any"
              onChange={e => setThreshold(Number(e.target.value))} />
          </label>
        </div>

        <Field label="Service filter (optional)" value={serviceFilter} onChange={setServiceFilter} placeholder="e.g. checkout-api · empty = all services" />

        <div style={{ display: 'flex', gap: 12 }}>
          <label style={{ display: 'grid', gap: 4, flex: 1 }}>
            <span className="muted" style={{ fontSize: 12 }}>Window (seconds)</span>
            <input type="number" min={30} max={86400} value={windowSeconds}
              onChange={e => setWindowSeconds(Number(e.target.value))} />
          </label>
          <label style={{ display: 'grid', gap: 4, flex: 1 }}>
            <span className="muted" style={{ fontSize: 12 }}>Sustained for (seconds)</span>
            <input type="number" min={0} max={86400} value={forSeconds}
              onChange={e => setForSeconds(Number(e.target.value))} />
          </label>
          <label style={{ display: 'grid', gap: 4, flex: 1 }}>
            <span className="muted" style={{ fontSize: 12 }}>Severity</span>
            <select value={severity} onChange={e => setSeverity(e.target.value as AlertSeverity)}>
              <option value="info">info</option>
              <option value="warning">warning</option>
              <option value="critical">critical</option>
            </select>
          </label>
        </div>

        <div>
          <span className="muted" style={{ fontSize: 12 }}>Notification channels</span>
          <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', marginTop: 6 }}>
            {channels.data?.length ? channels.data.map(c => {
              const sel = channelUuids.includes(c.sysChannelUuid);
              return (
                <button key={c.sysChannelUuid}
                  className={sel ? '' : 'secondary'}
                  type="button"
                  onClick={() => setChannelUuids(curr =>
                    sel ? curr.filter(u => u !== c.sysChannelUuid) : [...curr, c.sysChannelUuid])}
                  style={{ fontSize: 12 }}>
                  {sel ? '✓ ' : ''}{c.channelName}
                  <span className="muted" style={{ marginLeft: 4, fontSize: 10 }}>{c.channelType}</span>
                </button>
              );
            }) : <span className="faint" style={{ fontSize: 12 }}>No channels yet — create one on Alert channels page.</span>}
          </div>
        </div>

        {mode === 'edit' && (
          <label style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
            <input type="checkbox" checked={enabled} onChange={e => setEnabled(e.target.checked)} />
            <span>Enabled</span>
          </label>
        )}

        {error && <div className="badge err" style={{ padding: 8 }}>{error}</div>}

        <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end', marginTop: 8 }}>
          <button className="secondary" onClick={onClose}>Cancel</button>
          <button onClick={() => mutation.mutate()} disabled={mutation.isPending}>
            {mutation.isPending ? 'Saving…' : (mode === 'create' ? 'Create rule' : 'Save changes')}
          </button>
        </div>
      </div>
    </ModalShell>
  );
}

function Field({ label, value, onChange, placeholder }: {
  label: string; value: string; onChange: (v: string) => void; placeholder?: string;
}) {
  return (
    <label style={{ display: 'grid', gap: 4 }}>
      <span className="muted" style={{ fontSize: 12 }}>{label}</span>
      <input value={value} onChange={e => onChange(e.target.value)} placeholder={placeholder} style={{ padding: '8px 10px' }} />
    </label>
  );
}

function humanOperator(op: AlertOperator): string {
  return op === 'gt' ? '>' : op === 'gte' ? '≥' : op === 'lt' ? '<' : op === 'lte' ? '≤' : '=';
}

function formatSec(s: number): string {
  if (s < 60)      return `${s}s`;
  if (s < 3600)    return `${Math.round(s / 60)}m`;
  return `${(s / 3600).toFixed(1)}h`;
}

function formatValue(signal: AlertSignalType, v: number): string {
  if (signal === 'error_rate')     return `${(v * 100).toFixed(2)}%`;
  if (signal === 'request_volume' || signal === 'log_count') return v.toLocaleString();
  if (signal === 'metric_avg' || signal === 'metric_max') {
    return Math.abs(v) >= 1000 ? v.toLocaleString(undefined, { maximumFractionDigits: 0 }) : v.toLocaleString(undefined, { maximumFractionDigits: 3 });
  }
  return `${Math.round(v)}ms`;
}

function severityClass(sev: AlertSeverity): string {
  return sev === 'critical' ? 'err' : sev === 'warning' ? 'warn' : 'muted';
}

function stateClass(state: string): string {
  return state === 'firing' ? 'err' : state === 'pending' ? 'warn' : 'ok';
}
