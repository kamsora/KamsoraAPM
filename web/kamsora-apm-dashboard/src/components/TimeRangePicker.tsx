import { Pause, RefreshCw } from 'lucide-react';
import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';

const PRESETS = [
  { label: '15m', title: 'Last 15 min',   minutes: 15 },
  { label: '1h',  title: 'Last 1 hour',   minutes: 60 },
  { label: '6h',  title: 'Last 6 hours',  minutes: 6 * 60 },
  { label: '24h', title: 'Last 24 hours', minutes: 24 * 60 },
  { label: '7d',  title: 'Last 7 days',   minutes: 7 * 24 * 60 },
] as const;

export interface TimeRange {
  from: Date;
  to:   Date;
  /** Stable key for cache invalidation — changes only when the preset changes. */
  presetKey: string;
}

interface TimeRangeContextValue extends TimeRange {
  presetMinutes: number;
  setPreset(minutes: number, label: string): void;
  /** Re-run the current preset against "now" without changing it. */
  refreshNow(): void;
  autoRefresh: boolean;
  toggleAutoRefresh(): void;
  refreshIntervalSec: number;
  setRefreshIntervalSec(sec: number): void;
}

const REFRESH_OPTIONS_SEC = [5, 10, 30, 60] as const;

const TimeRangeContext = createContext<TimeRangeContextValue | undefined>(undefined);

export function TimeRangeProvider({ children }: { children: ReactNode }) {
  const [presetMinutes, setPresetMinutes] = useState(60);
  const [presetLabel, setPresetLabel]     = useState('Last 1 hour');

  // `nonce` bumps on every refresh — preset change, manual ↻, or auto-tick.
  // The presetKey embeds nonce so TanStack Query sees a new key and refetches,
  // and the new `from`/`to` slide forward to "now - preset" each time.
  const [nonce, setNonce] = useState(0);
  const [autoRefresh, setAutoRefresh] = useState(true);
  const [refreshIntervalSec, setRefreshIntervalSec] = useState(10);

  useEffect(() => {
    if (!autoRefresh) return;
    const id = window.setInterval(() => setNonce(n => n + 1), refreshIntervalSec * 1000);
    return () => window.clearInterval(id);
  }, [autoRefresh, refreshIntervalSec]);

  const value = useMemo<TimeRangeContextValue>(() => {
    const to   = new Date();
    const from = new Date(to.getTime() - presetMinutes * 60_000);
    const presetKey = `${presetMinutes}m-${nonce}`;
    return {
      from,
      to,
      presetKey,
      presetMinutes,
      autoRefresh,
      refreshIntervalSec,
      setPreset(minutes, label) {
        setPresetMinutes(minutes);
        setPresetLabel(label);
        setNonce(n => n + 1);
      },
      refreshNow() { setNonce(n => n + 1); },
      toggleAutoRefresh() { setAutoRefresh(a => !a); },
      setRefreshIntervalSec(sec) { setRefreshIntervalSec(sec); },
    };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [presetMinutes, presetLabel, nonce, autoRefresh, refreshIntervalSec]);

  return <TimeRangeContext.Provider value={value}>{children}</TimeRangeContext.Provider>;
}

export function useTimeRange(): TimeRangeContextValue {
  const ctx = useContext(TimeRangeContext);
  if (!ctx) throw new Error('useTimeRange must be used within a TimeRangeProvider');
  return ctx;
}

export function TimeRangePicker() {
  const range = useTimeRange();
  return (
    <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
      <div className="segmented" role="group" aria-label="Time range">
        {PRESETS.map(p => (
          <button
            key={p.minutes}
            type="button"
            title={p.title}
            className={p.minutes === range.presetMinutes ? 'active' : ''}
            onClick={() => range.setPreset(p.minutes, p.title)}
          >
            {p.label}
          </button>
        ))}
      </div>

      <button
        className="secondary live-toggle"
        onClick={range.toggleAutoRefresh}
        title={range.autoRefresh ? `Auto-refreshing every ${range.refreshIntervalSec}s. Click to pause.` : 'Auto-refresh paused. Click to resume.'}
        style={{ fontSize: 12, display: 'inline-flex', alignItems: 'center', gap: 6 }}
      >
        {range.autoRefresh
          ? <><span className="live-dot" />Live</>
          : <><Pause size={12} />Paused</>}
      </button>
      <select
        value={range.refreshIntervalSec}
        onChange={e => range.setRefreshIntervalSec(Number(e.target.value))}
        disabled={!range.autoRefresh}
        title="Refresh interval"
        style={{ fontSize: 12, padding: '5px 6px' }}
      >
        {REFRESH_OPTIONS_SEC.map(s => (
          <option key={s} value={s}>{s}s</option>
        ))}
      </select>
      <button
        className="secondary"
        onClick={() => range.refreshNow()}
        title="Refresh now"
        style={{ fontSize: 12, display: 'inline-flex', alignItems: 'center', padding: '7px 9px' }}
      >
        <RefreshCw size={13} />
      </button>
    </div>
  );
}
