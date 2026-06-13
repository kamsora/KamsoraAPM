import type { ECharts } from 'echarts';

/**
 * Chart drill-through helpers. Two flavors:
 *  - bar charts: use echarts-for-react's onEvents { click } — bars are big
 *    targets and the event carries the series name.
 *  - line charts: symbols are hidden, so a series click never lands. Instead
 *    bind a raw canvas click (onChartReady → onTimeClick) and convert the
 *    pixel back to the time axis.
 */

/** Bind a plot-area click that yields the x-axis time in epoch ms. */
export function onTimeClick(chart: ECharts, handler: (timeMs: number) => void): void {
  chart.getZr().on('click', (e: { offsetX: number; offsetY: number }) => {
    const pixel: [number, number] = [e.offsetX, e.offsetY];
    if (!chart.containPixel({ gridIndex: 0 }, pixel)) return;
    const converted = chart.convertFromPixel({ gridIndex: 0 }, pixel) as number[] | null;
    const timeMs = converted?.[0];
    if (typeof timeMs === 'number' && Number.isFinite(timeMs)) handler(timeMs);
  });
}

/** Snap an arbitrary in-bucket timestamp to its bucket's [from, to) ISO window. */
export function bucketWindow(timeMs: number, bucketSeconds: number): { fromUtc: string; toUtc: string } {
  const widthMs = bucketSeconds * 1000;
  const startMs = Math.floor(timeMs / widthMs) * widthMs;
  return {
    fromUtc: new Date(startMs).toISOString(),
    toUtc:   new Date(startMs + widthMs).toISOString(),
  };
}

/** Build the /traces URL for a drill-through. */
export function tracesUrl(params: Record<string, string | undefined>): string {
  const sp = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v) sp.set(k, v);
  }
  return `/traces?${sp.toString()}`;
}
