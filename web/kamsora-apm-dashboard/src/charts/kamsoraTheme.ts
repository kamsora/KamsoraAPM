// KamsoraAPM ECharts theme.
//
// Registered under the name "dark" ON PURPOSE: every chart in the app already
// passes theme="dark", so overriding the built-in dark theme restyles all of
// them from one place - pages never need to know a custom theme exists.
import * as echarts from 'echarts';

export const CHART_COLORS = [
  '#8B7CFF', // violet (brand)
  '#4FA3FF', // blue
  '#34D399', // green
  '#F59E0B', // amber
  '#F8719D', // pink
  '#22D3EE', // cyan
  '#A3E635', // lime
  '#EF4444', // red
];

const text = '#E7ECF6';
const textMuted = '#8E9BB8';
const border = 'rgba(120, 140, 190, 0.16)';
const splitLine = 'rgba(120, 140, 190, 0.10)';

export function registerKamsoraChartTheme(): void {
  echarts.registerTheme('dark', {
    darkMode: true,
    color: CHART_COLORS,
    backgroundColor: 'transparent',

    textStyle: {
      color: textMuted,
      fontFamily:
        "'Inter Variable', system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif",
    },

    title: { textStyle: { color: text, fontWeight: 600 } },

    legend: {
      textStyle: { color: textMuted, fontSize: 11 },
      icon: 'circle',
      itemWidth: 8,
      itemHeight: 8,
      itemGap: 16,
    },

    tooltip: {
      backgroundColor: 'rgba(16, 22, 40, 0.96)',
      borderColor: border,
      borderWidth: 1,
      padding: [10, 14],
      textStyle: { color: text, fontSize: 12 },
      extraCssText:
        'border-radius: 10px; box-shadow: 0 12px 32px rgba(0,0,0,0.5); backdrop-filter: blur(8px);',
      axisPointer: {
        lineStyle: { color: 'rgba(139, 124, 255, 0.45)' },
        crossStyle: { color: 'rgba(139, 124, 255, 0.45)' },
      },
    },

    categoryAxis: {
      axisLine: { lineStyle: { color: border } },
      axisTick: { show: false },
      axisLabel: { color: textMuted, fontSize: 11 },
      splitLine: { show: false },
    },
    valueAxis: {
      axisLine: { show: false },
      axisTick: { show: false },
      axisLabel: { color: textMuted, fontSize: 11 },
      nameTextStyle: { color: textMuted },
      splitLine: { lineStyle: { color: splitLine, type: [4, 4] } },
    },
    timeAxis: {
      axisLine: { lineStyle: { color: border } },
      axisTick: { show: false },
      axisLabel: { color: textMuted, fontSize: 11 },
      splitLine: { show: false },
    },

    line: {
      smooth: true,
      symbol: 'none',
      lineStyle: { width: 2 },
      emphasis: { lineStyle: { width: 3 } },
    },
    bar: {
      barMaxWidth: 28,
      itemStyle: { borderRadius: [3, 3, 0, 0] },
    },
    graph: { color: CHART_COLORS },
  });
}

/** Soft vertical gradient fill for line-chart areas (hex color in, fade out down). */
export function areaGradient(hex: string, from = 0.28, to = 0.02): echarts.graphic.LinearGradient {
  return new echarts.graphic.LinearGradient(0, 0, 0, 1, [
    { offset: 0, color: hexToRgba(hex, from) },
    { offset: 1, color: hexToRgba(hex, to) },
  ]);
}

function hexToRgba(hex: string, alpha: number): string {
  const h = hex.replace('#', '');
  const r = parseInt(h.slice(0, 2), 16);
  const g = parseInt(h.slice(2, 4), 16);
  const b = parseInt(h.slice(4, 6), 16);
  return `rgba(${r}, ${g}, ${b}, ${alpha})`;
}
