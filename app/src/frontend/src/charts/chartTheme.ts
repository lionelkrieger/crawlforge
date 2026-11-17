/**
 * ECharts theme derived from the accepted dark-first Mantine palette
 * (theme/theme.ts). Charts borrow the forge identity: night surfaces,
 * ember/signal accents, steel hairlines. Eight separable hues; luminance
 * and direct labels carry meaning alongside color (§8.4).
 *
 * Pure data — no renderer imports, no DOM access.
 *
 * @module charts/chartTheme
 */

import { chartPalette, semantic, steel, night } from '../theme/theme';

/** Status-class colors (wire keys are lowercase-no-hyphen; status classes are '2xx' style). */
export const statusColors: Readonly<Record<string, string>> = {
  '1xx': steel[4],
  '2xx': semantic.success,
  '3xx': semantic.info,
  '4xx': semantic.warning,
  '5xx': semantic.danger
};

/** Severity colors (wire values lowercase: error/warning/notice/pass). */
export const severityColors: Readonly<Record<string, string>> = {
  error: semantic.danger,
  warning: semantic.warning,
  notice: semantic.info,
  pass: semantic.success
};

/** Severity display order (most severe first). */
export const severityOrder: readonly string[] = ['error', 'warning', 'notice', 'pass'];

/** Depth ramp for graph coloring (cool → hot with crawl depth). */
export const depthRamp: readonly string[] = [
  '#22b8cf', '#3bc9db', '#66d9e8', '#a9e34b', '#ffa94d', '#ff8a3d', '#f76707', '#e8590c'
];

/** The chart palette re-exported for transforms (eight hues). */
export const palette: readonly string[] = chartPalette;

/** Categorical color for a sorted key index (stable across renders). */
export function categoryColor(index: number): string {
  return palette[index % palette.length];
}

/** Depth color clamped into the ramp. */
export function depthColor(depth: number): string {
  const clamped = Math.max(0, Math.min(depthRamp.length - 1, Math.floor(depth)));
  return depthRamp[clamped];
}

/** Shared axis/tooltip/text styling for the dark-first surfaces. */
export const chartBase = {
  backgroundColor: 'transparent',
  textStyle: { fontFamily: '"Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif', color: night[1] },
  title: {
    textStyle: { color: night[0], fontSize: 13, fontWeight: 600 },
    subtextStyle: { color: steel[4], fontSize: 11 },
    left: 8,
    top: 6
  },
  tooltip: {
    backgroundColor: night[6],
    borderColor: night[4],
    borderWidth: 1,
    textStyle: { color: night[0], fontSize: 12 },
    confine: true
  },
  grid: { left: 48, right: 20, top: 44, bottom: 32, containLabel: true },
  categoryAxis: {
    axisLine: { lineStyle: { color: night[4] } },
    axisTick: { show: false },
    axisLabel: { color: steel[3], fontSize: 11 },
    splitLine: { show: false }
  },
  valueAxis: {
    axisLine: { show: false },
    axisTick: { show: false },
    axisLabel: { color: steel[3], fontSize: 11 },
    splitLine: { lineStyle: { color: night[5], type: 'dashed' as const } }
  },
  legend: {
    textStyle: { color: steel[3], fontSize: 11 },
    inactiveColor: night[4],
    top: 6,
    right: 8
  }
} as const;

/** Registers the CrawlForge dark theme object with an echarts instance (renderer-side only). */
export const crawlForgeChartThemeName = 'crawlforge-dark';

/** The theme object passed to echarts.registerTheme (plain data). */
export const crawlForgeChartTheme = {
  color: [...palette],
  backgroundColor: chartBase.backgroundColor,
  textStyle: chartBase.textStyle,
  title: chartBase.title,
  tooltip: chartBase.tooltip,
  grid: chartBase.grid,
  categoryAxis: chartBase.categoryAxis,
  valueAxis: chartBase.valueAxis,
  legend: chartBase.legend
};
