import { useState } from 'react';

/**
 * Renders a tab strip of copy-pasteable install snippets pre-filled with the
 * caller's tenant UUID + cleartext API key. Used in two places:
 *   1. PlatformPage's "tenant created" modal (initial key handoff).
 *   2. ApiKeysPage's "Add host / agent" flow (existing tenant minting a fresh key).
 */
export function InstallSnippets({ tenantUuid, apiKey }: { tenantUuid: string; apiKey: string }) {
  const [tab, setTab] = useState<'host' | 'agent'>('host');

  const collectorEndpoint = `${window.location.protocol}//${window.location.hostname}:5080`;

  return (
    <div>
      <div style={{ display: 'flex', gap: 4, marginBottom: 8 }}>
        <TabButton active={tab === 'host'}  onClick={() => setTab('host')}>HostMonitor (daemon)</TabButton>
        <TabButton active={tab === 'agent'} onClick={() => setTab('agent')}>Agent (.NET NuGet)</TabButton>
      </div>

      {tab === 'host' ? (
        <HostSnippet tenantUuid={tenantUuid} apiKey={apiKey} collectorEndpoint={collectorEndpoint} />
      ) : (
        <AgentSnippet tenantUuid={tenantUuid} apiKey={apiKey} collectorEndpoint={collectorEndpoint} />
      )}
    </div>
  );
}

function HostSnippet({ tenantUuid, apiKey, collectorEndpoint }: {
  tenantUuid: string; apiKey: string; collectorEndpoint: string;
}) {
  const json = `{
  "KamsoraApm": {
    "HostMonitor": {
      "CollectorEndpoint": "${collectorEndpoint}",
      "TenantId":          "${tenantUuid}",
      "ApiKey":            "${apiKey}",
      "CpuMemoryInterval": "00:00:10",
      "MaxBatchSize":      6,
      "TopProcesses":      50
    }
  }
}`;

  const windowsService = `# 1. Drop the KamsoraAPM.HostMonitor.exe + appsettings.json into C:\\Kamsora\\HostMonitor\\
# 2. Edit appsettings.json with the JSON above.
# 3. Register as a Windows Service (run as Admin):
sc.exe create KamsoraAPM.HostMonitor binPath= "C:\\Kamsora\\HostMonitor\\KamsoraAPM.HostMonitor.exe" start= auto
sc.exe start  KamsoraAPM.HostMonitor`;

  const linuxSystemd = `# /etc/systemd/system/kamsora-host-monitor.service
[Unit]
Description=KamsoraAPM HostMonitor
After=network.target

[Service]
Type=notify
WorkingDirectory=/opt/kamsora-host-monitor
ExecStart=/opt/kamsora-host-monitor/KamsoraAPM.HostMonitor
Restart=always
User=kamsora

[Install]
WantedBy=multi-user.target

# Then:
sudo systemctl daemon-reload
sudo systemctl enable --now kamsora-host-monitor`;

  return (
    <div style={{ display: 'grid', gap: 12 }}>
      <CodeBlock label="appsettings.json" value={json} />
      <CodeBlock label="Windows Service registration" value={windowsService} />
      <CodeBlock label="Linux systemd unit" value={linuxSystemd} />
    </div>
  );
}

function AgentSnippet({ tenantUuid, apiKey, collectorEndpoint }: {
  tenantUuid: string; apiKey: string; collectorEndpoint: string;
}) {
  const dotnetAdd = `dotnet add package KamsoraAPM.Agent`;

  const programCs = `// Program.cs in your ASP.NET Core app
builder.Services.AddKamsoraApm(o =>
{
    o.CollectorEndpoint = "${collectorEndpoint}";
    o.TenantId          = "${tenantUuid}";
    o.ApiKey            = "${apiKey}";
    o.ServiceName       = "<your-service-name>";
});`;

  const appsettings = `// Or via appsettings.json — same shape:
{
  "KamsoraApm": {
    "Agent": {
      "CollectorEndpoint": "${collectorEndpoint}",
      "TenantId":          "${tenantUuid}",
      "ApiKey":            "${apiKey}",
      "ServiceName":       "<your-service-name>"
    }
  }
}`;

  return (
    <div style={{ display: 'grid', gap: 12 }}>
      <CodeBlock label="Install the NuGet package" value={dotnetAdd} />
      <CodeBlock label="Wire it in Program.cs" value={programCs} />
      <CodeBlock label="…or via appsettings.json" value={appsettings} />
    </div>
  );
}

function TabButton({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      onClick={onClick}
      className={active ? '' : 'secondary'}
      style={{
        fontSize: 12,
        padding: '6px 12px',
        borderRadius: 4,
        cursor: 'pointer',
      }}
    >
      {children}
    </button>
  );
}

export function CopyField({ label, value, mono, secret }: {
  label: string; value: string; mono?: boolean; secret?: boolean;
}) {
  const [revealed, setRevealed] = useState(!secret);
  const display = revealed ? value : value.replace(/./g, '•');

  return (
    <div>
      <div className="muted" style={{ fontSize: 12, marginBottom: 4 }}>{label}</div>
      <div style={{ display: 'flex', gap: 8, alignItems: 'stretch' }}>
        <input
          readOnly
          value={display}
          className={mono ? 'mono' : undefined}
          style={{ flex: 1, padding: '6px 8px', background: 'var(--bg-surface-2, #1e293b)' }}
        />
        {secret && (
          <button className="secondary" onClick={() => setRevealed(r => !r)} style={{ fontSize: 12 }}>
            {revealed ? 'Hide' : 'Reveal'}
          </button>
        )}
        <button onClick={() => navigator.clipboard.writeText(value)} style={{ fontSize: 12 }}>Copy</button>
      </div>
    </div>
  );
}

function CodeBlock({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 4 }}>
        <span className="muted" style={{ fontSize: 12 }}>{label}</span>
        <button className="secondary" onClick={() => navigator.clipboard.writeText(value)} style={{ fontSize: 11, padding: '2px 8px' }}>Copy</button>
      </div>
      <pre className="mono" style={{
        background: 'var(--bg-surface-2, #1e293b)', padding: 12, borderRadius: 6,
        fontSize: 12, overflowX: 'auto', margin: 0,
      }}>{value}</pre>
    </div>
  );
}
