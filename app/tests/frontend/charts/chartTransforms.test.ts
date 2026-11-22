/**
 * Exhaustive pure-transform suite for every dashboard family (node env —
 * no DOM, no renderer). Proves determinism, ordering, wire-value mapping,
 * aggregate math, clamping, and empty-input safety of the data → ECharts
 * option transforms (WP-06-04).
 */

import { describe, expect, it } from 'vitest';
import type { EChartsOption } from 'echarts';
import {
  analyticsCoverageOption,
  botFamilyOption,
  categoryDisplayLabel,
  categoryDistributionOption,
  categoryScoresOption,
  consentCoverageOption,
  crawlHealthOption,
  formatPercent,
  geoLlmsCoverageOption,
  governanceCoverageOption,
  histogramOption,
  hostRateBarOption,
  hostThrottleTimelineOption,
  logCorrelationOption,
  logHitTimelineOption,
  ngramTopOption,
  ngramZoneTreemapOption,
  scoreTrendOption,
  severityDistributionOption,
  sortedCountEntries,
  statusDistributionOption,
  summaryCardModels,
  topicalGapOption,
  wafBlockRateGaugeOption,
  wafProviderOption
} from '../../../src/frontend/src/charts/chartTransforms';
import { severityColors, statusColors } from '../../../src/frontend/src/charts/chartTheme';
import { AUDIT_CATEGORY_LABELS } from '../../../src/frontend/src/contracts/api/vocabulary';
import {
  consentFixture,
  coverageFixture,
  gapsFixture,
  governanceFixture,
  hostsFixture,
  llmsFixture,
  logCorrelationFixture,
  ngramRowsFixture,
  pointsFixture,
  scoresFixture,
  summaryFixture,
  wafFixture
} from './helpers';

/** Structural series access for assertions (renderer-neutral). */
interface SeriesShape {
  type?: string;
  name?: string;
  stack?: string;
  step?: string;
  data?: unknown[];
  markLine?: { data?: { xAxis?: number }[] };
}
function seriesAt(option: EChartsOption, index: number): SeriesShape {
  const series = option.series as SeriesShape[];
  return series[index];
}
interface ItemShape {
  name?: string;
  value?: number;
  itemStyle?: { color?: string };
}
function items(series: SeriesShape): ItemShape[] {
  return (series.data ?? []) as ItemShape[];
}
/** Numeric values from either plain-number data or {value} items. */
function values(series: SeriesShape): (number | undefined)[] {
  return (series.data ?? []).map((entry) =>
    typeof entry === 'number' ? entry : (entry as ItemShape).value
  );
}
/** Category-axis labels (bar/line names live on the axis, not the items). */
function axisData(option: EChartsOption, axis: 'xAxis' | 'yAxis'): string[] {
  return (option[axis] as { data: string[] }).data;
}
/** Deterministic serialization (functions compared by source). */
function stable(option: EChartsOption): string {
  return JSON.stringify(option, (_key, value) => (typeof value === 'function' ? value.toString() : value));
}

describe('sortedCountEntries / formatPercent', () => {
  it('sorts keys deterministically and drops non-finite values', () => {
    expect(sortedCountEntries({ b: 2, a: 1, c: Number.NaN, d: Number.POSITIVE_INFINITY })).toEqual([
      ['a', 1],
      ['b', 2]
    ]);
  });

  it('formats ratios as clamped percentages', () => {
    expect(formatPercent(0.055)).toBe('5.5%');
    expect(formatPercent(0)).toBe('0.0%');
    expect(formatPercent(1)).toBe('100.0%');
    expect(formatPercent(-0.4)).toBe('0.0%');
    expect(formatPercent(1.9)).toBe('100.0%');
  });
});

describe('summaryCardModels', () => {
  it('builds the full card set with tones from the aggregate', () => {
    const cards = summaryCardModels(summaryFixture);
    expect(cards.map((card) => card.key)).toEqual(['total', 'fetched', 'errors', 'blocked', 'ttfb', 'waf', 'throttle']);
    expect(cards.find((card) => card.key === 'total')?.value).toBe('100');
    expect(cards.find((card) => card.key === 'errors')?.tone).toBe('danger');
    expect(cards.find((card) => card.key === 'blocked')?.tone).toBe('warning');
    expect(cards.find((card) => card.key === 'ttfb')?.value).toBe('246 ms');
    expect(cards.find((card) => card.key === 'waf')?.value).toBe('5.5%');
    expect(cards.find((card) => card.key === 'throttle')?.value).toBe('1');
  });

  it('degrades gracefully without additive WAF/throttle and with null TTFB', () => {
    const cards = summaryCardModels({
      ...summaryFixture,
      waf: undefined,
      throttle: undefined,
      crawlHealth: { ...summaryFixture.crawlHealth, averageTtfbMs: null, errorCount: 0, blockedCount: 0 }
    });
    expect(cards.map((card) => card.key)).toEqual(['total', 'fetched', 'errors', 'blocked', 'ttfb']);
    expect(cards.find((card) => card.key === 'ttfb')?.value).toBe('—');
    expect(cards.find((card) => card.key === 'errors')?.tone).toBe('success');
  });
});

describe('statusDistributionOption', () => {
  it('orders status classes 1xx→5xx with wire colors', () => {
    const option = statusDistributionOption({ '5xx': 3, '2xx': 80, '4xx': 7, '3xx': 10 });
    const data = items(seriesAt(option, 0));
    expect(data.map((item) => item.name)).toEqual(['2xx', '3xx', '4xx', '5xx']);
    expect(data.map((item) => item.value)).toEqual([80, 10, 7, 3]);
    expect(data[0].itemStyle?.color).toBe(statusColors['2xx']);
    expect(data[2].itemStyle?.color).toBe(statusColors['4xx']);
    expect(seriesAt(option, 0).type).toBe('pie');
  });

  it('is deterministic and safe on empty counts', () => {
    const a = statusDistributionOption(summaryFixture.countsByStatus);
    const b = statusDistributionOption({ ...summaryFixture.countsByStatus });
    expect(a).toEqual(b);
    expect(items(seriesAt(statusDistributionOption({}), 0))).toEqual([]);
  });
});

describe('severityDistributionOption', () => {
  it('orders error → warning → notice → pass with semantic colors', () => {
    const option = severityDistributionOption({ pass: 75, notice: 12, error: 4, warning: 9 });
    expect(axisData(option, 'xAxis')).toEqual(['error', 'warning', 'notice', 'pass']);
    const data = items(seriesAt(option, 0));
    expect(values(seriesAt(option, 0))).toEqual([4, 9, 12, 75]);
    expect(data[0].itemStyle?.color).toBe(severityColors.error);
    expect(data[3].itemStyle?.color).toBe(severityColors.pass);
    expect(seriesAt(option, 0).type).toBe('bar');
  });

  it('handles unknown severity keys without crashing', () => {
    const option = severityDistributionOption({ mystery: 2, error: 1 });
    expect(axisData(option, 'xAxis')).toEqual(['error', 'mystery']);
  });
});

describe('categoryDistributionOption', () => {
  it('sorts descending and reverses for the horizontal bar', () => {
    const option = categoryDistributionOption({ crawl: 8, content: 10, performance: 7 });
    const yAxis = option.yAxis as { data: string[] };
    expect(yAxis.data).toEqual(['performance', 'crawl', 'content']);
    expect(items(seriesAt(option, 0)).map((item) => item.value)).toEqual([7, 8, 10]);
  });

  it('breaks count ties by key for determinism', () => {
    const option = categoryDistributionOption({ zeta: 5, alpha: 5 });
    const yAxis = option.yAxis as { data: string[] };
    expect(yAxis.data).toEqual(['zeta', 'alpha']);
  });

  it('keeps wire tokens in yAxis data and maps display labels only in the formatter', () => {
    const option = categoryDistributionOption({ securityandprotocol: 3, internationalization: 2, images: 1 });
    const yAxis = option.yAxis as {
      data: string[];
      axisLabel: { formatter: (value: string) => string };
    };
    expect(yAxis.data).toEqual(['images', 'internationalization', 'securityandprotocol']);
    for (const token of ['images', 'internationalization', 'securityandprotocol'] as const) {
      expect(yAxis.axisLabel.formatter(token)).toBe(AUDIT_CATEGORY_LABELS[token]);
    }
    expect(categoryDisplayLabel('securityandprotocol')).toBe(AUDIT_CATEGORY_LABELS.securityandprotocol);
    expect(yAxis.axisLabel.formatter('securityandprotocol')).not.toBe('securityandprotocol');
    expect(yAxis.axisLabel.formatter('mystery')).toBe('mystery');
  });
});

describe('crawlHealthOption', () => {
  it('maps fetched/errors/blocked with status colors and TTFB subtitle', () => {
    const option = crawlHealthOption(summaryFixture.crawlHealth);
    const data = items(seriesAt(option, 0));
    expect(data.map((item) => item.value)).toEqual([90, 3, 7]);
    expect(data[0].itemStyle?.color).toBe(statusColors['2xx']);
    expect(data[1].itemStyle?.color).toBe(statusColors['5xx']);
    expect((option.title as { subtext?: string }).subtext).toContain('246 ms');
  });

  it('omits the subtitle when TTFB is null', () => {
    const option = crawlHealthOption({ ...summaryFixture.crawlHealth, averageTtfbMs: null });
    expect((option.title as { subtext?: string }).subtext).toBeUndefined();
  });
});

describe('scoreTrendOption', () => {
  it('plots pre-bucketed points on a 0–100 axis', () => {
    const option = scoreTrendOption(pointsFixture);
    expect(axisData(option, 'xAxis')).toEqual(['audit-1', 'audit-2', 'audit-3']);
    expect(values(seriesAt(option, 0))).toEqual([61, 70, 78]);
    const yAxis = option.yAxis as { min?: number; max?: number };
    expect(yAxis.min).toBe(0);
    expect(yAxis.max).toBe(100);
    expect(seriesAt(option, 0).type).toBe('line');
  });

  it('renders an empty series for no history', () => {
    expect(items(seriesAt(scoreTrendOption([]), 0))).toEqual([]);
  });
});

describe('categoryScoresOption', () => {
  it('sorts categories by score and marks the overall score', () => {
    const option = categoryScoresOption(scoresFixture);
    const yAxis = option.yAxis as { data: string[] };
    expect(yAxis.data).toEqual(['crawl', 'content', 'performance']);
    const series = seriesAt(option, 0);
    expect(items(series).map((item) => item.value)).toEqual([71, 82.5, 90.2]);
    expect(series.markLine?.data?.[0]?.xAxis).toBe(78.4);
    expect((option.title as { subtext?: string }).subtext).toContain('78.4');
  });
});

describe('hostThrottleTimelineOption', () => {
  it('emits one step-line per host with history, timestamps deduped+sorted', () => {
    const option = hostThrottleTimelineOption(hostsFixture);
    expect(seriesAt(option, 0).name).toBe('example.com');
    expect(seriesAt(option, 0).step).toBe('end');
    expect((option.xAxis as { data: string[] }).data).toEqual([
      '2026-11-14T10:00:00Z',
      '2026-11-14T10:05:00Z'
    ]);
    expect(seriesAt(option, 0).data).toEqual([
      ['2026-11-14T10:00:00Z', 4],
      ['2026-11-14T10:05:00Z', 6]
    ]);
    // cdn.example.com has no history → omitted from timelines.
    expect((option.series as SeriesShape[]).length).toBe(1);
  });

  it('is safe with no hosts', () => {
    expect((hostThrottleTimelineOption([]).series as SeriesShape[]).length).toBe(0);
  });
});

describe('hostRateBarOption', () => {
  it('sorts hosts by effective rate and plots effective vs baseline', () => {
    const option = hostRateBarOption(hostsFixture);
    expect(axisData(option, 'xAxis')).toEqual(['cdn.example.com', 'example.com']);
    expect(seriesAt(option, 0).name).toBe('Effective');
    expect(values(seriesAt(option, 0))).toEqual([12, 4]);
    expect(values(seriesAt(option, 1))).toEqual([12, 8]);
  });
});

describe('wafProviderOption / wafBlockRateGaugeOption', () => {
  it('sorts providers descending and summarizes the block rate', () => {
    const option = wafProviderOption(wafFixture);
    expect(axisData(option, 'xAxis')).toEqual(['cloudflare', 'akamai']);
    expect(values(seriesAt(option, 0))).toEqual([3, 2]);
    expect((option.title as { subtext?: string }).subtext).toContain('5 blocked');
    expect((option.title as { subtext?: string }).subtext).toContain('5.5%');
  });

  it('clamps the gauge to [0, 100] and escalates color above 10%', () => {
    const low = wafBlockRateGaugeOption(0.055);
    const high = wafBlockRateGaugeOption(0.4);
    const over = wafBlockRateGaugeOption(2);
    const negative = wafBlockRateGaugeOption(-1);
    expect(items(seriesAt(low, 0))[0].value).toBe(5.5);
    expect(items(seriesAt(high, 0))[0].value).toBe(40);
    expect(items(seriesAt(over, 0))[0].value).toBe(100);
    expect(items(seriesAt(negative, 0))[0].value).toBe(0);
    expect(seriesAt(low, 0).type).toBe('gauge');
  });
});

describe('ngramTopOption / ngramZoneTreemapOption / topicalGapOption', () => {
  it('caps and sorts top n-grams descending with name tie-break', () => {
    const option = ngramTopOption(ngramRowsFixture, 2);
    expect(axisData(option, 'yAxis')).toEqual(['hiking boots', 'running shoes']);
    expect(values(seriesAt(option, 0))).toEqual([60, 120]);
    const tied = ngramTopOption(
      [
        { ngram: 'zeta term', n: 2, zone: 'body', count: 9 },
        { ngram: 'alpha term', n: 2, zone: 'body', count: 9 }
      ],
      2
    );
    expect(axisData(tied, 'yAxis')).toEqual(['zeta term', 'alpha term']);
  });

  it('aggregates n-gram mass per zone for the treemap', () => {
    const option = ngramZoneTreemapOption(ngramRowsFixture);
    const data = items(seriesAt(option, 0)) as { name: string; children: ItemShape[] }[];
    expect(data.map((zone) => zone.name)).toEqual(['body', 'title']);
    const body = data[0].children;
    expect(body.find((item) => item.name === 'running shoes')?.value).toBe(120);
    const title = data[1].children;
    expect(title.find((item) => item.name === 'running shoes')?.value).toBe(40);
    expect(title.find((item) => item.name === 'trail running')?.value).toBe(25);
  });

  it('sorts topical gaps by gapScore descending and caps', () => {
    const option = topicalGapOption(gapsFixture, 1);
    expect(axisData(option, 'yAxis')).toEqual(['waterproof jackets']);
    expect(values(seriesAt(option, 0))).toEqual([0.9]);
  });
});

describe('logCorrelationOption / botFamilyOption / logHitTimelineOption', () => {
  it('maps the three correlation counters', () => {
    const option = logCorrelationOption(logCorrelationFixture);
    expect(items(seriesAt(option, 0)).map((item) => item.value)).toEqual([4, 11, 6]);
    expect((option.xAxis as { data: string[] }).data).toEqual([
      'True orphans',
      'Budget waste',
      'Uncrawled indexable'
    ]);
  });

  it('sorts bot families descending', () => {
    const option = botFamilyOption({ googlebot: 10, gptbot: 30, bingbot: 5 });
    expect(items(seriesAt(option, 0)).map((item) => item.name)).toEqual(['gptbot', 'googlebot', 'bingbot']);
  });

  it('plots pre-bucketed hit timeline points', () => {
    const option = logHitTimelineOption(pointsFixture);
    expect(values(seriesAt(option, 0))).toEqual([61, 70, 78]);
    expect(seriesAt(option, 0).type).toBe('bar');
  });
});

describe('geoLlmsCoverageOption / governanceCoverageOption', () => {
  it('stacks valid/invalid/missing per host in host order', () => {
    const option = geoLlmsCoverageOption(llmsFixture);
    expect(axisData(option, 'xAxis')).toEqual([
      'blog.example.com',
      'example.com',
      'shop.example.com'
    ]);
    expect(values(seriesAt(option, 0))).toEqual([0, 1, 0]);
    expect(values(seriesAt(option, 1))).toEqual([0, 0, 1]);
    expect(values(seriesAt(option, 2))).toEqual([1, 0, 0]);
    expect(seriesAt(option, 0).stack).toBe('llms');
  });

  it('aggregates allowed/denied verdicts per bot', () => {
    const option = governanceCoverageOption(governanceFixture);
    expect(axisData(option, 'xAxis')).toEqual(['claudebot', 'gptbot']);
    expect(values(seriesAt(option, 0))).toEqual([1, 1]);
    expect(values(seriesAt(option, 1))).toEqual([0, 1]);
  });

  it('is safe with empty GEO rows', () => {
    expect((geoLlmsCoverageOption([]).series as SeriesShape[])[0].data).toEqual([]);
    expect((governanceCoverageOption([]).xAxis as { data: string[] }).data).toEqual([]);
  });
});

describe('analyticsCoverageOption / consentCoverageOption', () => {
  it('sorts containers by reach and labels coverage percentages', () => {
    const option = analyticsCoverageOption(coverageFixture);
    const data = items(seriesAt(option, 0));
    expect(data.map((item) => item.name)).toEqual(['GTM-ABC123', 'AW-999']);
    expect(data.map((item) => item.value)).toEqual([92, 40]);
    const label = (seriesAt(option, 0) as { label?: { formatter?: (params: unknown) => string } }).label;
    expect(label?.formatter?.({ name: 'GTM-ABC123' })).toBe('GTM-ABC123\n92.0%');
  });

  it('splits consent coverage and notes Consent Mode v2 detection', () => {
    const option = consentCoverageOption(consentFixture);
    const data = items(seriesAt(option, 0));
    expect(data.map((item) => item.name)).toEqual(['With consent', 'Without consent']);
    expect(data.map((item) => item.value)).toEqual([85, 15]);
    expect((option.title as { subtext?: string }).subtext).toContain('Consent Mode v2 detected');
    const absent = consentCoverageOption({ ...consentFixture, consentModeV2Present: false });
    expect((absent.title as { subtext?: string }).subtext).toContain('not detected');
  });
});

describe('histogramOption', () => {
  it('renders pre-bucketed distributions generically', () => {
    const option = histogramOption(pointsFixture, 'Response time', 'URLs');
    expect((option.title as { text?: string }).text).toBe('Response time');
    expect(values(seriesAt(option, 0))).toEqual([61, 70, 78]);
    expect((option.yAxis as { name?: string }).name).toBe('URLs');
  });
});

describe('determinism across every transform', () => {
  it('produces deep-equal options for identical inputs', () => {
    const transforms: (() => EChartsOption)[] = [
      () => statusDistributionOption(summaryFixture.countsByStatus),
      () => severityDistributionOption(summaryFixture.countsBySeverity),
      () => categoryDistributionOption(summaryFixture.countsByCategory),
      () => crawlHealthOption(summaryFixture.crawlHealth),
      () => scoreTrendOption(pointsFixture),
      () => categoryScoresOption(scoresFixture),
      () => hostThrottleTimelineOption(hostsFixture),
      () => hostRateBarOption(hostsFixture),
      () => wafProviderOption(wafFixture),
      () => wafBlockRateGaugeOption(wafFixture.blockRate),
      () => ngramTopOption(ngramRowsFixture),
      () => ngramZoneTreemapOption(ngramRowsFixture),
      () => topicalGapOption(gapsFixture),
      () => logCorrelationOption(logCorrelationFixture),
      () => botFamilyOption(wafFixture.byProvider),
      () => logHitTimelineOption(pointsFixture),
      () => geoLlmsCoverageOption(llmsFixture),
      () => governanceCoverageOption(governanceFixture),
      () => analyticsCoverageOption(coverageFixture),
      () => consentCoverageOption(consentFixture),
      () => histogramOption(pointsFixture, 'TTFB')
    ];
    for (const transform of transforms) {
      expect(stable(transform())).toBe(stable(transform()));
    }
  });

  it('never mutates its input fixtures', () => {
    const rows = ngramRowsFixture.map((row) => ({ ...row }));
    ngramTopOption(rows);
    ngramZoneTreemapOption(rows);
    expect(rows).toEqual(ngramRowsFixture.map((row) => ({ ...row })));
    const hosts = hostsFixture.map((host) => ({ ...host }));
    hostRateBarOption(hosts);
    hostThrottleTimelineOption(hosts);
    expect(hosts).toEqual(hostsFixture.map((host) => ({ ...host })));
  });
});
