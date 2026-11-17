/**
 * Pure data → ECharts option transforms (FS-CRAWL-001 §8.3, FS-CRAWL-002
 * §8.4). Every dashboard family is a deterministic function of locked DTO
 * aggregates — pre-bucketed chart points, dashboard summary counts, live
 * counters, bounded explorer windows. NO raw-row recomputation, NO DOM,
 * NO renderer imports (type-only echarts import keeps the module pure and
 * node-testable).
 *
 * @module charts/chartTransforms
 */

import type { EChartsOption } from 'echarts';
import type {
  AuditDashboardSummary,
  AuditScores,
  ChartPoint,
  CrawlHealth
} from '../contracts/api/audits';
import type { HostState } from '../contracts/api/hosts';
import type { WafBlocksSummary } from '../contracts/api/waf';
import type { NgramRow, TopicalGap } from '../contracts/api/ngrams';
import type { LogCorrelationSummary } from '../contracts/api/logs';
import type { LlmsTxtValidation, BotGovernanceRow } from '../contracts/api/geo';
import type { ContainerCoverage, AnalyticsConsentSummary } from '../contracts/api/analytics';
import {
  chartBase,
  categoryColor,
  severityColors,
  severityOrder,
  statusColors,
  palette
} from './chartTheme';

/** One summary card model (presentational; rendered by the dashboard). */
export interface SummaryCardModel {
  readonly key: string;
  readonly label: string;
  readonly value: string;
  readonly tone: 'neutral' | 'success' | 'warning' | 'danger' | 'info';
}

/** Sorted [key, value] entries of a count record (deterministic ordering). */
export function sortedCountEntries(counts: Readonly<Record<string, number>>): [string, number][] {
  return Object.entries(counts)
    .filter(([, value]) => Number.isFinite(value))
    .sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0));
}

/** Formats a ratio in [0, 1] as a percentage string (1 decimal). */
export function formatPercent(ratio: number): string {
  const clamped = Math.max(0, Math.min(1, ratio));
  return `${(clamped * 100).toFixed(1)}%`;
}

/** Base option skeleton shared by every transform. */
function base(title: string, subtext?: string): EChartsOption {
  return {
    backgroundColor: chartBase.backgroundColor,
    textStyle: { ...chartBase.textStyle },
    title: { text: title, subtext, ...chartBase.title },
    tooltip: { ...chartBase.tooltip, trigger: 'item' },
    grid: { ...chartBase.grid }
  };
}

/** Category axis fragment (dark-first styling). */
function categoryAxis(data: readonly string[], rotate = 0): EChartsOption['xAxis'] {
  return {
    type: 'category',
    data: [...data],
    axisLine: { lineStyle: { color: '#2b3348' } },
    axisTick: { show: false },
    axisLabel: { color: '#8892a8', fontSize: 11, rotate },
    splitLine: { show: false }
  };
}

/** Value axis fields shared by X and Y axes (dark-first styling). */
function valueAxisFields(name?: string) {
  return {
    type: 'value' as const,
    name,
    nameTextStyle: { color: '#8892a8', fontSize: 11 },
    axisLine: { show: false },
    axisTick: { show: false },
    axisLabel: { color: '#8892a8', fontSize: 11 },
    splitLine: { lineStyle: { color: '#1d2436', type: 'dashed' as const } }
  };
}

/** Value axis fragment for the Y axis. */
function valueAxis(name?: string): EChartsOption['yAxis'] {
  return valueAxisFields(name);
}

/** Value axis fragment for the X axis (horizontal bar charts). */
function valueXAxis(name?: string): EChartsOption['xAxis'] {
  return valueAxisFields(name);
}

// ── Dashboard summary cards ────────────────────────────────────────────────

/** Summary cards from the dashboard aggregate (counts + health + WAF + throttle). */
export function summaryCardModels(summary: AuditDashboardSummary): SummaryCardModel[] {
  const health = summary.crawlHealth;
  const cards: SummaryCardModel[] = [
    { key: 'total', label: 'Total URLs', value: String(health.totalUrls), tone: 'neutral' },
    { key: 'fetched', label: 'Fetched', value: String(health.fetched), tone: 'success' },
    { key: 'errors', label: 'Errors', value: String(health.errorCount), tone: health.errorCount > 0 ? 'danger' : 'success' },
    { key: 'blocked', label: 'Blocked', value: String(health.blockedCount), tone: health.blockedCount > 0 ? 'warning' : 'success' },
    {
      key: 'ttfb',
      label: 'Avg TTFB',
      value: health.averageTtfbMs === null ? '—' : `${Math.round(health.averageTtfbMs)} ms`,
      tone: 'info'
    }
  ];
  if (summary.waf) {
    cards.push({
      key: 'waf',
      label: 'WAF block rate',
      value: formatPercent(summary.waf.blockRate),
      tone: summary.waf.blockRate > 0 ? 'warning' : 'success'
    });
  }
  if (summary.throttle) {
    cards.push({
      key: 'throttle',
      label: 'Throttled hosts',
      value: String(summary.throttle.throttledHostCount),
      tone: summary.throttle.throttledHostCount > 0 ? 'warning' : 'neutral'
    });
  }
  return cards;
}

// ── Distributions ──────────────────────────────────────────────────────────

/** Status-class donut (wire keys '1xx'…'5xx'; stable class order). */
export function statusDistributionOption(counts: Readonly<Record<string, number>>): EChartsOption {
  const order = ['1xx', '2xx', '3xx', '4xx', '5xx'];
  const entries = sortedCountEntries(counts).sort(
    ([a], [b]) => order.indexOf(a) - order.indexOf(b)
  );
  const option = base('Status distribution');
  return {
    ...option,
    tooltip: { ...chartBase.tooltip, trigger: 'item', formatter: '{b}: {c} ({d}%)' },
    legend: { ...chartBase.legend, orient: 'vertical', right: 8, top: 'middle' },
    series: [
      {
        type: 'pie',
        radius: ['42%', '68%'],
        center: ['40%', '55%'],
        avoidLabelOverlap: true,
        itemStyle: { borderColor: '#141a29', borderWidth: 2 },
        label: { color: '#aab4c8', fontSize: 11 },
        data: entries.map(([name, value]) => ({
          name,
          value,
          itemStyle: { color: statusColors[name] ?? categoryColor(order.indexOf(name)) }
        }))
      }
    ]
  };
}

/** Severity bar (error → warning → notice → pass; wire values lowercase). */
export function severityDistributionOption(counts: Readonly<Record<string, number>>): EChartsOption {
  const entries = sortedCountEntries(counts).sort(([a], [b]) => {
    const ia = severityOrder.indexOf(a);
    const ib = severityOrder.indexOf(b);
    return (ia < 0 ? severityOrder.length : ia) - (ib < 0 ? severityOrder.length : ib);
  });
  const option = base('Findings by severity');
  return {
    ...option,
    tooltip: { ...chartBase.tooltip, trigger: 'axis' },
    xAxis: categoryAxis(entries.map(([name]) => name)),
    yAxis: valueAxis('findings'),
    series: [
      {
        type: 'bar',
        barMaxWidth: 42,
        data: entries.map(([name, value]) => ({
          value,
          itemStyle: { color: severityColors[name] ?? palette[3] }
        })),
        label: { show: true, position: 'top', color: '#aab4c8', fontSize: 11 }
      }
    ]
  };
}

/** Category horizontal bar (descending count; stable tie order by key). */
export function categoryDistributionOption(counts: Readonly<Record<string, number>>): EChartsOption {
  const entries = sortedCountEntries(counts).sort(([a, va], [b, vb]) => vb - va || (a < b ? -1 : 1));
  const option = base('Findings by category');
  return {
    ...option,
    grid: { left: 8, right: 32, top: 40, bottom: 16, containLabel: true },
    tooltip: { ...chartBase.tooltip, trigger: 'axis' },
    xAxis: valueXAxis('findings'),
    yAxis: {
      type: 'category',
      data: entries.map(([name]) => name).reverse(),
      axisLine: { show: false },
      axisTick: { show: false },
      axisLabel: { color: '#8892a8', fontSize: 11 }
    },
    series: [
      {
        type: 'bar',
        barMaxWidth: 18,
        data: entries.map(([, value], index) => ({
          value,
          itemStyle: { color: categoryColor(index) }
        })).reverse(),
        label: { show: true, position: 'right', color: '#aab4c8', fontSize: 11 }
      }
    ]
  };
}

/** Crawl-health bar: fetched / errors / blocked with TTFB subtitle. */
export function crawlHealthOption(health: CrawlHealth): EChartsOption {
  const option = base(
    'Crawl health',
    health.averageTtfbMs === null ? undefined : `avg TTFB ${Math.round(health.averageTtfbMs)} ms`
  );
  const rows: [string, number, string][] = [
    ['Fetched', health.fetched, statusColors['2xx']],
    ['Errors', health.errorCount, statusColors['5xx']],
    ['Blocked', health.blockedCount, statusColors['4xx']]
  ];
  return {
    ...option,
    tooltip: { ...chartBase.tooltip, trigger: 'axis' },
    xAxis: categoryAxis(rows.map(([name]) => name)),
    yAxis: valueAxis('URLs'),
    series: [
      {
        type: 'bar',
        barMaxWidth: 48,
        data: rows.map(([, value, color]) => ({ value, itemStyle: { color } })),
        label: { show: true, position: 'top', color: '#aab4c8', fontSize: 11 }
      }
    ]
  };
}

// ── Scores ─────────────────────────────────────────────────────────────────

/** Score trend line from pre-bucketed chart points (labels = audit markers). */
export function scoreTrendOption(points: readonly ChartPoint[]): EChartsOption {
  const option = base('Score trend', 'overall score per audit (0–100)');
  return {
    ...option,
    tooltip: { ...chartBase.tooltip, trigger: 'axis' },
    xAxis: categoryAxis(points.map((point) => point.label)),
    yAxis: { ...(valueAxis('score') as object), min: 0, max: 100 } as EChartsOption['yAxis'],
    series: [
      {
        type: 'line',
        smooth: false,
        symbol: 'circle',
        symbolSize: 7,
        lineStyle: { color: palette[0], width: 2 },
        itemStyle: { color: palette[0] },
        areaStyle: { color: 'rgba(255, 138, 61, 0.12)' },
        data: points.map((point) => point.value)
      }
    ]
  };
}

/** Per-category score bars with the overall score as a mark line. */
export function categoryScoresOption(scores: AuditScores): EChartsOption {
  const categories = [...scores.categories].sort((a, b) => b.score - a.score || (a.category < b.category ? -1 : 1));
  const option = base('Category scores', `overall ${scores.overall.toFixed(1)}`);
  return {
    ...option,
    grid: { left: 8, right: 32, top: 48, bottom: 16, containLabel: true },
    tooltip: { ...chartBase.tooltip, trigger: 'axis' },
    xAxis: { ...(valueAxis('score') as object), min: 0, max: 100 } as EChartsOption['xAxis'],
    yAxis: {
      type: 'category',
      data: categories.map((entry) => entry.category).reverse(),
      axisLine: { show: false },
      axisTick: { show: false },
      axisLabel: { color: '#8892a8', fontSize: 11 }
    },
    series: [
      {
        type: 'bar',
        barMaxWidth: 16,
        data: categories.map((entry, index) => ({
          value: entry.score,
          itemStyle: { color: categoryColor(index) }
        })).reverse(),
        label: { show: true, position: 'right', color: '#aab4c8', fontSize: 11, formatter: '{c}' },
        markLine: {
          symbol: 'none',
          lineStyle: { color: palette[1], type: 'dashed' },
          label: { color: palette[1], formatter: `overall ${scores.overall.toFixed(0)}` },
          data: [{ xAxis: scores.overall }]
        }
      }
    ]
  };
}

// ── Host throttle ──────────────────────────────────────────────────────────

/**
 * Host throttle timelines: one step-line per host with adjustment history
 * (rateAfter over time). Hosts without history are omitted — the current
 * rate bar covers them.
 */
export function hostThrottleTimelineOption(hosts: readonly HostState[]): EChartsOption {
  const withHistory = hosts
    .filter((host) => (host.history?.length ?? 0) > 0)
    .sort((a, b) => (a.host < b.host ? -1 : 1));
  const option = base('Host throttle timelines', 'effective rate after each AIMD adjustment');
  return {
    ...option,
    tooltip: { ...chartBase.tooltip, trigger: 'axis' },
    legend: { ...chartBase.legend, type: 'scroll' },
    xAxis: categoryAxis(
      [...new Set(withHistory.flatMap((host) => (host.history ?? []).map((entry) => entry.timestamp)))].sort()
    ),
    yAxis: valueAxis('req/s'),
    series: withHistory.map((host, index) => ({
      type: 'line' as const,
      name: host.host,
      step: 'end' as const,
      symbol: 'circle',
      symbolSize: 5,
      lineStyle: { color: categoryColor(index), width: 2 },
      itemStyle: { color: categoryColor(index) },
      data: (host.history ?? []).map((entry) => [entry.timestamp, entry.rateAfter] as [string, number])
    }))
  };
}

/** Current effective rate vs rolling baseline per host (bounded bar). */
export function hostRateBarOption(hosts: readonly HostState[]): EChartsOption {
  const sorted = [...hosts].sort((a, b) => b.effectiveRate - a.effectiveRate || (a.host < b.host ? -1 : 1));
  const option = base('Host rates', 'effective vs rolling baseline (req/s)');
  return {
    ...option,
    tooltip: { ...chartBase.tooltip, trigger: 'axis' },
    legend: { ...chartBase.legend },
    xAxis: categoryAxis(sorted.map((host) => host.host), 30),
    yAxis: valueAxis('req/s'),
    series: [
      {
        type: 'bar',
        name: 'Effective',
        barMaxWidth: 20,
        itemStyle: { color: palette[0] },
        data: sorted.map((host) => host.effectiveRate)
      },
      {
        type: 'bar',
        name: 'Baseline',
        barMaxWidth: 20,
        itemStyle: { color: palette[7] },
        data: sorted.map((host) => host.rollingBaselineRate)
      }
    ]
  };
}

// ── WAF ────────────────────────────────────────────────────────────────────

/** WAF blocks by provider (descending; wire keys lowercase). */
export function wafProviderOption(summary: WafBlocksSummary): EChartsOption {
  const entries = sortedCountEntries(summary.byProvider).sort(([, a], [, b]) => b - a);
  const option = base('WAF blocks by provider', `${summary.totalBlocked} blocked · ${formatPercent(summary.blockRate)} of fetched`);
  return {
    ...option,
    tooltip: { ...chartBase.tooltip, trigger: 'axis' },
    xAxis: categoryAxis(entries.map(([name]) => name), 20),
    yAxis: valueAxis('blocks'),
    series: [
      {
        type: 'bar',
        barMaxWidth: 36,
        data: entries.map(([, value], index) => ({ value, itemStyle: { color: categoryColor(index) } })),
        label: { show: true, position: 'top', color: '#aab4c8', fontSize: 11 }
      }
    ]
  };
}

/** WAF block-rate gauge (ratio in [0, 1]). */
export function wafBlockRateGaugeOption(blockRate: number): EChartsOption {
  const option = base('WAF block rate');
  const percent = Math.max(0, Math.min(1, blockRate)) * 100;
  return {
    ...option,
    series: [
      {
        type: 'gauge',
        startAngle: 210,
        endAngle: -30,
        min: 0,
        max: 100,
        radius: '85%',
        center: ['50%', '60%'],
        progress: { show: true, width: 12, itemStyle: { color: percent > 10 ? palette[4] : palette[1] } },
        axisLine: { lineStyle: { width: 12, color: [[1, '#1d2436']] } },
        axisTick: { show: false },
        splitLine: { show: false },
        axisLabel: { color: '#8892a8', fontSize: 10, distance: 18 },
        pointer: { show: false },
        anchor: { show: false },
        title: { show: false },
        detail: {
          valueAnimation: false,
          color: '#c9d1e0',
          fontSize: 22,
          offsetCenter: [0, '0%'],
          formatter: '{value}%'
        },
        data: [{ value: Number(percent.toFixed(1)) }]
      }
    ]
  };
}

// ── N-grams / topical ──────────────────────────────────────────────────────

/** Top n-grams horizontal bar (bounded; descending count). */
export function ngramTopOption(rows: readonly NgramRow[], topN = 20): EChartsOption {
  const top = [...rows]
    .sort((a, b) => b.count - a.count || (a.ngram < b.ngram ? -1 : 1))
    .slice(0, topN);
  const option = base('Top n-grams', 'site-level term frequency');
  return {
    ...option,
    grid: { left: 8, right: 40, top: 44, bottom: 16, containLabel: true },
    tooltip: { ...chartBase.tooltip, trigger: 'axis' },
    xAxis: valueXAxis('count'),
    yAxis: {
      type: 'category',
      data: top.map((row) => row.ngram).reverse(),
      axisLine: { show: false },
      axisTick: { show: false },
      axisLabel: { color: '#8892a8', fontSize: 11 }
    },
    series: [
      {
        type: 'bar',
        barMaxWidth: 14,
        data: top.map((row, index) => ({ value: row.count, itemStyle: { color: categoryColor(index) } })).reverse(),
        label: { show: true, position: 'right', color: '#aab4c8', fontSize: 10 }
      }
    ]
  };
}

/** N-gram zone treemap (4-zone model; area = summed count per zone/ngram). */
export function ngramZoneTreemapOption(rows: readonly NgramRow[]): EChartsOption {
  const byZone = new Map<string, Map<string, number>>();
  for (const row of rows) {
    const zone = byZone.get(row.zone) ?? new Map<string, number>();
    zone.set(row.ngram, (zone.get(row.ngram) ?? 0) + row.count);
    byZone.set(row.zone, zone);
  }
  const zones = [...byZone.keys()].sort();
  const option = base('Topical zones', 'n-gram mass by DOM zone');
  return {
    ...option,
    tooltip: { ...chartBase.tooltip, formatter: '{b}: {c}' },
    series: [
      {
        type: 'treemap',
        roam: false,
        nodeClick: false,
        breadcrumb: { show: false },
        width: '94%',
        height: '78%',
        top: 44,
        label: { color: '#0b0e14', fontSize: 11 },
        upperLabel: { show: true, height: 20, color: '#c9d1e0', fontSize: 11 },
        itemStyle: { borderColor: '#141a29', borderWidth: 1, gapWidth: 1 },
        levels: [
          { itemStyle: { borderColor: '#141a29', borderWidth: 2, gapWidth: 2 }, upperLabel: { show: false } },
          { colorSaturation: [0.3, 0.7], itemStyle: { borderColorSaturation: 0.6, gapWidth: 1, borderWidth: 1 } }
        ],
        data: zones.map((zone, index) => ({
          name: zone,
          itemStyle: { color: categoryColor(index) },
          children: [...(byZone.get(zone) ?? new Map()).entries()]
            .sort(([a], [b]) => (a < b ? -1 : 1))
            .map(([ngram, count]) => ({ name: ngram, value: count }))
        }))
      }
    ]
  };
}

/** Topical gaps bar (gapScore descending; higher = larger gap). */
export function topicalGapOption(gaps: readonly TopicalGap[], topN = 20): EChartsOption {
  const top = [...gaps]
    .sort((a, b) => b.gapScore - a.gapScore || (a.ngram < b.ngram ? -1 : 1))
    .slice(0, topN);
  const option = base('Topical gaps', 'expected vs observed coverage shortfall');
  return {
    ...option,
    grid: { left: 8, right: 40, top: 44, bottom: 16, containLabel: true },
    tooltip: { ...chartBase.tooltip, trigger: 'axis' },
    xAxis: { ...(valueAxis('gap score') as object), min: 0, max: 1 } as EChartsOption['xAxis'],
    yAxis: {
      type: 'category',
      data: top.map((gap) => gap.ngram).reverse(),
      axisLine: { show: false },
      axisTick: { show: false },
      axisLabel: { color: '#8892a8', fontSize: 11 }
    },
    series: [
      {
        type: 'bar',
        barMaxWidth: 14,
        itemStyle: { color: palette[4] },
        data: top.map((gap) => gap.gapScore).reverse(),
        label: { show: true, position: 'right', color: '#aab4c8', fontSize: 10, formatter: (params: unknown) => Number((params as { value?: unknown }).value ?? 0).toFixed(2) }
      }
    ]
  };
}

// ── Log intelligence ───────────────────────────────────────────────────────

/** Correlation summary bar (orphans / budget waste / uncrawled indexable). */
export function logCorrelationOption(summary: LogCorrelationSummary): EChartsOption {
  const option = base('Log correlation', 'bot hits vs crawl coverage');
  const rows: [string, number, string][] = [
    ['True orphans', summary.orphanCount, palette[4]],
    ['Budget waste', summary.budgetWasteCount, palette[5]],
    ['Uncrawled indexable', summary.uncrawledIndexableCount, palette[1]]
  ];
  return {
    ...option,
    tooltip: { ...chartBase.tooltip, trigger: 'axis' },
    xAxis: categoryAxis(rows.map(([name]) => name)),
    yAxis: valueAxis('URLs'),
    series: [
      {
        type: 'bar',
        barMaxWidth: 48,
        data: rows.map(([, value, color]) => ({ value, itemStyle: { color } })),
        label: { show: true, position: 'top', color: '#aab4c8', fontSize: 11 }
      }
    ]
  };
}

/** Bot-family breakdown donut (aggregated counts, never raw log rows). */
export function botFamilyOption(counts: Readonly<Record<string, number>>): EChartsOption {
  const entries = sortedCountEntries(counts).sort(([, a], [, b]) => b - a);
  const option = base('Bot families', 'share of bot hits');
  return {
    ...option,
    tooltip: { ...chartBase.tooltip, trigger: 'item', formatter: '{b}: {c} ({d}%)' },
    legend: { ...chartBase.legend, orient: 'vertical', right: 8, top: 'middle', type: 'scroll' },
    series: [
      {
        type: 'pie',
        radius: ['40%', '66%'],
        center: ['40%', '55%'],
        itemStyle: { borderColor: '#141a29', borderWidth: 2 },
        label: { color: '#aab4c8', fontSize: 11 },
        data: entries.map(([name, value], index) => ({ name, value, itemStyle: { color: categoryColor(index) } }))
      }
    ]
  };
}

/** Hit-timeline histogram from pre-bucketed points. */
export function logHitTimelineOption(points: readonly ChartPoint[]): EChartsOption {
  const option = base('Bot hit timeline', 'hits per time bucket');
  return {
    ...option,
    tooltip: { ...chartBase.tooltip, trigger: 'axis' },
    xAxis: categoryAxis(points.map((point) => point.label), 30),
    yAxis: valueAxis('hits'),
    series: [
      {
        type: 'bar',
        barMaxWidth: 24,
        itemStyle: { color: palette[1] },
        data: points.map((point) => point.value)
      }
    ]
  };
}

// ── GEO coverage ───────────────────────────────────────────────────────────

/** llms.txt coverage per host: valid / invalid / missing stacked bar. */
export function geoLlmsCoverageOption(rows: readonly LlmsTxtValidation[]): EChartsOption {
  const hosts = [...rows].sort((a, b) => (a.host < b.host ? -1 : 1));
  const option = base('llms.txt coverage', 'per-host structural status');
  return {
    ...option,
    tooltip: { ...chartBase.tooltip, trigger: 'axis' },
    legend: { ...chartBase.legend },
    xAxis: categoryAxis(hosts.map((row) => row.host), 20),
    yAxis: valueAxis('hosts'),
    series: [
      {
        type: 'bar',
        name: 'Valid',
        stack: 'llms',
        barMaxWidth: 32,
        itemStyle: { color: statusColors['2xx'] },
        data: hosts.map((row) => (row.present && row.valid ? 1 : 0))
      },
      {
        type: 'bar',
        name: 'Invalid',
        stack: 'llms',
        barMaxWidth: 32,
        itemStyle: { color: statusColors['4xx'] },
        data: hosts.map((row) => (row.present && !row.valid ? 1 : 0))
      },
      {
        type: 'bar',
        name: 'Missing',
        stack: 'llms',
        barMaxWidth: 32,
        itemStyle: { color: palette[7] },
        data: hosts.map((row) => (row.present ? 0 : 1))
      }
    ]
  };
}

/** Governance coverage: allowed vs denied rows per bot (advisory matrix). */
export function governanceCoverageOption(rows: readonly BotGovernanceRow[]): EChartsOption {
  const byBot = new Map<string, { allowed: number; denied: number }>();
  for (const row of rows) {
    const entry = byBot.get(row.botName) ?? { allowed: 0, denied: 0 };
    if (row.allowed) entry.allowed += 1;
    else entry.denied += 1;
    byBot.set(row.botName, entry);
  }
  const bots = [...byBot.keys()].sort();
  const option = base('AI-bot governance', 'advisory verdicts per agent (never modifies robots.txt)');
  return {
    ...option,
    tooltip: { ...chartBase.tooltip, trigger: 'axis' },
    legend: { ...chartBase.legend },
    xAxis: categoryAxis(bots, 30),
    yAxis: valueAxis('scopes'),
    series: [
      {
        type: 'bar',
        name: 'Allowed',
        stack: 'gov',
        barMaxWidth: 28,
        itemStyle: { color: statusColors['2xx'] },
        data: bots.map((bot) => byBot.get(bot)?.allowed ?? 0)
      },
      {
        type: 'bar',
        name: 'Denied',
        stack: 'gov',
        barMaxWidth: 28,
        itemStyle: { color: statusColors['5xx'] },
        data: bots.map((bot) => byBot.get(bot)?.denied ?? 0)
      }
    ]
  };
}

// ── Analytics coverage ─────────────────────────────────────────────────────

/** Container coverage treemap (area = pages with container; label = coverage %). */
export function analyticsCoverageOption(rows: readonly ContainerCoverage[]): EChartsOption {
  const sorted = [...rows].sort((a, b) => b.pagesWithContainer - a.pagesWithContainer || (a.containerId < b.containerId ? -1 : 1));
  const option = base('Analytics coverage', 'container reach across indexable pages');
  return {
    ...option,
    tooltip: {
      ...chartBase.tooltip,
      formatter: (params: unknown) => {
        const point = params as { name?: unknown; value?: unknown };
        return `${String(point.name ?? '')}: ${String(point.value ?? '')} pages`;
      }
    },
    series: [
      {
        type: 'treemap',
        roam: false,
        nodeClick: false,
        breadcrumb: { show: false },
        width: '94%',
        height: '78%',
        top: 44,
        label: {
          color: '#0b0e14',
          fontSize: 11,
          formatter: (params: unknown) => {
            const point = params as { name?: unknown };
            const row = sorted.find((entry) => entry.containerId === point.name);
            return row ? `${row.containerId}\n${formatPercent(row.coverage)}` : String(point.name ?? '');
          }
        },
        itemStyle: { borderColor: '#141a29', borderWidth: 1, gapWidth: 1 },
        data: sorted.map((row, index) => ({
          name: row.containerId,
          value: row.pagesWithContainer,
          itemStyle: { color: categoryColor(index) }
        }))
      }
    ]
  };
}

/** Consent coverage donut (pages with vs without consent declarations). */
export function consentCoverageOption(consent: AnalyticsConsentSummary): EChartsOption {
  const option = base(
    'Consent coverage',
    consent.consentModeV2Present ? 'Consent Mode v2 detected' : 'Consent Mode v2 not detected'
  );
  return {
    ...option,
    tooltip: { ...chartBase.tooltip, trigger: 'item', formatter: '{b}: {c} ({d}%)' },
    legend: { ...chartBase.legend, orient: 'vertical', right: 8, top: 'middle' },
    series: [
      {
        type: 'pie',
        radius: ['42%', '68%'],
        center: ['40%', '55%'],
        itemStyle: { borderColor: '#141a29', borderWidth: 2 },
        label: { color: '#aab4c8', fontSize: 11 },
        data: [
          { name: 'With consent', value: consent.pagesWithConsent, itemStyle: { color: statusColors['2xx'] } },
          { name: 'Without consent', value: consent.pagesWithoutConsent, itemStyle: { color: statusColors['5xx'] } }
        ]
      }
    ]
  };
}

// ── Generic histogram ──────────────────────────────────────────────────────

/** Generic histogram from pre-bucketed chart points (response time, CWV, word count). */
export function histogramOption(points: readonly ChartPoint[], title: string, valueName = 'count'): EChartsOption {
  const option = base(title, 'pre-bucketed distribution');
  return {
    ...option,
    tooltip: { ...chartBase.tooltip, trigger: 'axis' },
    xAxis: categoryAxis(points.map((point) => point.label), 30),
    yAxis: valueAxis(valueName),
    series: [
      {
        type: 'bar',
        barMaxWidth: 28,
        itemStyle: { color: palette[1] },
        data: points.map((point) => point.value)
      }
    ]
  };
}
