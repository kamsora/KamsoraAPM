import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../api/client';
import type { AlertChannelDto, ChannelType, CreateAlertChannelRequest } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Empty, ErrorBlock, Loading } from '../components/Loading';
import { ModalShell } from './PlatformPage';

export default function AlertChannelsPage() {
  const { role } = useAuth();
  const [creating, setCreating] = useState(false);

  const channels = useQuery({
    queryKey: ['alert-channels'],
    queryFn:  () => api<AlertChannelDto[]>('/v1/alerts/channels'),
    placeholderData: keepPreviousData,
    refetchInterval: 30_000,
  });

  if (role !== 'owner') {
    return (
      <>
        <h1 className="page-title">Alert channels</h1>
        <div className="card"><ErrorBlock error={new Error('Only tenant owners can manage alert channels.')} /></div>
      </>
    );
  }

  return (
    <>
      <h1 className="page-title">
        Alert channels
        <button onClick={() => setCreating(true)} style={{ marginLeft: 12 }}>+ New channel</button>
      </h1>

      <div className="card" style={{ padding: 0 }}>
        {channels.isLoading ? <Loading /> :
         channels.error    ? <ErrorBlock error={channels.error} /> :
         (channels.data?.length ?? 0) === 0 ? <Empty label="No channels yet. Create one to receive alerts via webhook or the in-dashboard banner." /> : (
          <table>
            <thead>
              <tr>
                <th>Name</th>
                <th>Type</th>
                <th>Config preview</th>
                <th>Status</th>
                <th style={{ textAlign: 'right' }}>Actions</th>
              </tr>
            </thead>
            <tbody>
              {channels.data!.map(c => <ChannelRow key={c.sysChannelUuid} channel={c} />)}
            </tbody>
          </table>
        )}
      </div>

      {creating && <CreateChannelModal onClose={() => setCreating(false)} />}
    </>
  );
}

function ChannelRow({ channel }: { channel: AlertChannelDto }) {
  const [confirming, setConfirming] = useState(false);
  const [testMsg, setTestMsg] = useState<string | null>(null);
  const qc = useQueryClient();

  const del = useMutation({
    mutationFn: () => api<void>(`/v1/alerts/channels/${encodeURIComponent(channel.sysChannelUuid)}`, { method: 'DELETE' }),
    onSuccess:  () => qc.invalidateQueries({ queryKey: ['alert-channels'] }),
  });

  const test = useMutation({
    mutationFn: () => api<{ delivered: boolean }>(`/v1/alerts/channels/${encodeURIComponent(channel.sysChannelUuid)}/test`, { method: 'POST' }),
    onSuccess:  (r) => setTestMsg(r.delivered ? '✓ Sent test notification' : '⚠ Channel returned non-200'),
    onError:    (err: any) => setTestMsg(`Failed: ${err?.body?.error ?? err?.message ?? 'unknown'}`),
  });

  return (
    <tr style={{ opacity: channel.enabled ? 1 : 0.55 }}>
      <td><strong>{channel.channelName}</strong></td>
      <td><span className="badge muted">{channel.channelType}</span></td>
      <td className="mono faint" style={{ fontSize: 11, maxWidth: 360, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        {channel.channelType === 'webhook' ? extractUrl(channel.configJson) : '(no config - in-dashboard banner)'}
      </td>
      <td>
        {testMsg
          ? <span className={`badge ${testMsg.startsWith('✓') ? 'ok' : 'err'}`} style={{ fontSize: 11 }}>{testMsg}</span>
          : <span className="badge ok" style={{ fontSize: 11 }}>active</span>}
      </td>
      <td style={{ textAlign: 'right' }}>
        <button className="secondary" disabled={test.isPending}
          onClick={() => { setTestMsg(null); test.mutate(); }}
          style={{ fontSize: 11, marginRight: 4 }}>
          {test.isPending ? '…' : 'Send test'}
        </button>
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

function CreateChannelModal({ onClose }: { onClose: () => void }) {
  const qc = useQueryClient();
  const [channelName, setChannelName] = useState('');
  const [channelType, setChannelType] = useState<ChannelType>('webhook');
  const [webhookUrl,  setWebhookUrl]  = useState('');
  const [secretName,  setSecretName]  = useState('X-Kamsora-Alert-Secret');
  const [secretValue, setSecretValue] = useState('');
  const [error,       setError]       = useState<string | null>(null);

  const mutation = useMutation({
    mutationFn: () => {
      const config = channelType === 'webhook'
        ? JSON.stringify({
            url: webhookUrl.trim(),
            ...(secretValue.trim() ? { secret_header_name: secretName.trim(), secret_header_value: secretValue.trim() } : {}),
          })
        : '{}';
      const payload: CreateAlertChannelRequest = {
        ChannelName: channelName.trim(),
        ChannelType: channelType,
        ConfigJson:  config,
      };
      return api<{ sysChannelUuid: string }>('/v1/alerts/channels', { method: 'POST', body: JSON.stringify(payload) });
    },
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['alert-channels'] }); onClose(); },
    onError:   (err: any) => setError(err?.body?.error ?? err?.message ?? 'Create failed'),
  });

  return (
    <ModalShell title="New alert channel" onClose={onClose}>
      <div style={{ display: 'grid', gap: 12 }}>
        <label style={{ display: 'grid', gap: 4 }}>
          <span className="muted" style={{ fontSize: 12 }}>Channel name</span>
          <input value={channelName} onChange={e => setChannelName(e.target.value)}
            placeholder="e.g. ops-slack-webhook" style={{ padding: '8px 10px' }} />
        </label>

        <label style={{ display: 'grid', gap: 4 }}>
          <span className="muted" style={{ fontSize: 12 }}>Type</span>
          <select value={channelType} onChange={e => setChannelType(e.target.value as ChannelType)}>
            <option value="webhook">Webhook - POST JSON to a URL</option>
            <option value="inapp">In-dashboard banner - no external delivery</option>
          </select>
        </label>

        {channelType === 'webhook' && (
          <>
            <label style={{ display: 'grid', gap: 4 }}>
              <span className="muted" style={{ fontSize: 12 }}>Webhook URL</span>
              <input value={webhookUrl} onChange={e => setWebhookUrl(e.target.value)}
                placeholder="https://hooks.example.com/services/T0000/B0000/xxxxx" style={{ padding: '8px 10px' }} />
            </label>
            <details>
              <summary className="muted" style={{ fontSize: 12, cursor: 'pointer' }}>Optional shared secret header</summary>
              <div style={{ display: 'grid', gap: 8, marginTop: 8 }}>
                <input value={secretName} onChange={e => setSecretName(e.target.value)} placeholder="Header name" style={{ padding: '6px 10px' }} />
                <input value={secretValue} onChange={e => setSecretValue(e.target.value)} placeholder="Header value (empty = no header)" style={{ padding: '6px 10px' }} />
              </div>
            </details>
          </>
        )}

        {error && <div className="badge err" style={{ padding: 8 }}>{error}</div>}

        <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end', marginTop: 8 }}>
          <button className="secondary" onClick={onClose}>Cancel</button>
          <button onClick={() => {
            setError(null);
            if (!channelName.trim()) { setError('Channel name is required'); return; }
            if (channelType === 'webhook' && !webhookUrl.trim().startsWith('http')) {
              setError('Webhook URL must start with http:// or https://'); return;
            }
            mutation.mutate();
          }} disabled={mutation.isPending}>
            {mutation.isPending ? 'Creating…' : 'Create channel'}
          </button>
        </div>
      </div>
    </ModalShell>
  );
}

function extractUrl(cfg: string): string {
  try {
    const parsed = JSON.parse(cfg);
    return parsed?.url ?? '';
  } catch { return cfg; }
}
