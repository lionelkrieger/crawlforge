/**
 * Analysis explorer definitions: Log File Analyzer, Semantic Linking, GEO,
 * Analytics & Tagging, Redirect Matrix, WAF blocks (FS-CRAWL-002 §8.3).
 *
 * @module surfaces/explorers/analysisExplorers
 */

import type { LogCorrelationRow, LogCorrelationSummary } from '../../contracts/api/logs';
import type { LinkOpportunity } from '../../contracts/api/semantics';
import type { BotGovernanceRow } from '../../contracts/api/geo';
import type { AnalyticsTag, AnalyticsConsentSummary } from '../../contracts/api/analytics';
import type { Finding } from '../../contracts/api/audits';
import type { RedirectMatrixResult } from '../../contracts/api/redirect-matrix';
import type { WafBlock } from '../../contracts/api/waf';
import type { ExplorerDefinition, DrilldownSpec, ExplorerView } from './explorerTypes';
import { makeColumn, makeField } from './columnUtils';
import {
  Badge, severityTone, UrlCell, BoolCell, TriStateCell, NumberCell,
  PercentCell, ConfidenceCell, TextCell, TimestampCell, HopChainCell
} from './cells';

// ── Log File Analyzer ────────────────────────────────────────────────────

const LOG_VIEWS: readonly ExplorerView[] = [
  { id: 'orphans', label: 'Orphan URLs', query: { view: 'orphans' } },
  { id: 'budget-waste', label: 'Budget waste', query: { view: 'budget-waste' } },
  { id: 'uncrawled-indexable', label: 'Uncrawled indexable', query: { view: 'uncrawled-indexable' } }
];

export const logExplorer: ExplorerDefinition<LogCorrelationRow> = {
  id: 'logs',
  title: 'Log File Analyzer',
  glyph: '📜',
  tagline: 'Bot-hit correlation: orphans, budget waste, and uncrawled indexable URLs.',
  route: 'logs.correlation',
  adapterKind: 'correlation',
  views: LOG_VIEWS,
  defaultSort: [{ field: 'botHits', order: 'desc' }],
  rowKey: (row, index) => `${row.url}:${index}`,
  columns: () => [
    // The log-correlation route binds no sort parameter — no sort affordance
    // (sort affordance audit, WP-06-03 reopen).
    { ...makeColumn<LogCorrelationRow, string>('url', 'Logged URL', (row) => row.url, {
      size: 360,
      cell: (value) => <UrlCell url={value} />
    }), enableSorting: false },
    { ...makeColumn<LogCorrelationRow, string | null>('canonicalUrl', 'Crawl canonical', (row) => row.canonicalUrl, {
      size: 320,
      cell: (value) => <UrlCell url={value} muted />
    }), enableSorting: false },
    { ...makeColumn<LogCorrelationRow, number>('hits', 'Hits', (row) => row.hits, {
      size: 90,
      cell: (value) => <NumberCell value={value} />,
      meta: { serverField: 'hits', numeric: true }
    }), enableSorting: false },
    { ...makeColumn<LogCorrelationRow, number>('botHits', 'Bot hits', (row) => row.botHits, {
      size: 100,
      cell: (value) => <NumberCell value={value} />,
      meta: { serverField: 'botHits', numeric: true }
    }), enableSorting: false },
    { ...makeColumn<LogCorrelationRow, string | null>('urlId', 'Crawl match', (row) => row.urlId, {
      size: 170,
      cell: (value) => (value ? <TextCell value={value} mono /> : <Badge tone="warning">no match</Badge>)
    }), enableSorting: false },
    { ...makeColumn<LogCorrelationRow, string | null>('lastSeen', 'Last seen', (row) => row.lastSeen, {
      size: 150,
      cell: (value) => <TimestampCell value={value} />
    }), enableSorting: false }
  ],
  queryFields: () => [
    makeField('view', 'Correlation view', 'enum', {
      serverFilter: 'view',
      enumValues: ['orphans', 'budget-waste', 'uncrawled-indexable']
    }),
    makeField('importId', 'Log import id', 'text', { serverFilter: 'importId' })
  ],
  drilldown: (row): DrilldownSpec => ({
    title: row.url,
    subtitle: 'Log correlation row',
    fields: [
      { label: 'Total hits', value: <NumberCell value={row.hits} /> },
      { label: 'Bot hits', value: <NumberCell value={row.botHits} /> },
      { label: 'Crawl canonical', value: <UrlCell url={row.canonicalUrl} /> },
      { label: 'Crawl match', value: row.urlId ? <TextCell value={row.urlId} mono /> : <Badge tone="warning">not discovered by crawl</Badge> },
      { label: 'Last seen', value: <TimestampCell value={row.lastSeen} /> }
    ]
  }),
  emptyHint: 'Import server logs from the project Log Imports surface, then correlate against this audit.'
};

/** Summary chips from the correlation response meta (host renders these). */
export function logSummaryChips(summary: LogCorrelationSummary): readonly { label: string; value: string; tone: 'warning' | 'danger' | 'info' }[] {
  return [
    { label: 'Orphans', value: summary.orphanCount.toLocaleString(), tone: 'warning' },
    { label: 'Budget waste', value: summary.budgetWasteCount.toLocaleString(), tone: 'danger' },
    { label: 'Uncrawled indexable', value: summary.uncrawledIndexableCount.toLocaleString(), tone: 'info' }
  ];
}

// ── Semantic Linking ─────────────────────────────────────────────────────

export const semanticExplorer: ExplorerDefinition<LinkOpportunity> = {
  id: 'semantics',
  title: 'Semantic Linking',
  glyph: '🧠',
  tagline: 'Internal-link opportunities from local embeddings — similarity-ranked.',
  route: 'semantics.linkOpportunities',
  adapterKind: 'array',
  defaultSort: [{ field: 'similarity', order: 'desc' }],
  rowKey: (row, index) => `${row.sourceUrlId}->${row.targetUrlId}:${index}`,
  columns: () => [
    // The link-opportunities route binds no sort parameter (similarity-ranked
    // server-side) — no sort affordance.
    { ...makeColumn<LinkOpportunity, string>('sourceCanonicalUrl', 'Source page', (row) => row.sourceCanonicalUrl, {
      size: 340,
      cell: (value) => <UrlCell url={value} />
    }), enableSorting: false },
    { ...makeColumn<LinkOpportunity, string>('targetCanonicalUrl', 'Link target', (row) => row.targetCanonicalUrl, {
      size: 340,
      cell: (value) => <UrlCell url={value} />
    }), enableSorting: false },
    { ...makeColumn<LinkOpportunity, number>('similarity', 'Similarity', (row) => row.similarity, {
      size: 170,
      cell: (value) => <PercentCell value={value} tone="info" />,
      meta: { serverField: 'similarity', numeric: true }
    }), enableSorting: false },
    { ...makeColumn<LinkOpportunity, string | null | undefined>('anchorSuggestion', 'Anchor suggestion', (row) => row.anchorSuggestion, {
      size: 240,
      cell: (value) => <TextCell value={value} />
    }), enableSorting: false }
  ],
  queryFields: () => [
    makeField('minSimilarity', 'Minimum similarity', 'number', { serverFilter: 'minSimilarity' }),
    makeField('urlId', 'Source URL id', 'text', { serverFilter: 'urlId' })
  ],
  drilldown: (row): DrilldownSpec => ({
    title: 'Link opportunity',
    subtitle: `cosine similarity ${(row.similarity * 100).toFixed(1)}%`,
    fields: [
      { label: 'Source', value: <UrlCell url={row.sourceCanonicalUrl} /> },
      { label: 'Target', value: <UrlCell url={row.targetCanonicalUrl} /> },
      { label: 'Similarity', value: <PercentCell value={row.similarity} tone="info" /> },
      { label: 'Anchor suggestion', value: <TextCell value={row.anchorSuggestion} /> }
    ]
  }),
  emptyHint: 'Run the local embedding job (Semantic analysis) to generate link opportunities.'
};

// ── GEO / AI-bot Governance ──────────────────────────────────────────────

export const geoExplorer: ExplorerDefinition<BotGovernanceRow> = {
  id: 'geo',
  title: 'GEO & Bot Governance',
  glyph: '🤖',
  tagline: 'AI-agent access verdicts from robots.txt + llms.txt evidence.',
  route: 'geo.governance',
  adapterKind: 'array',
  rowKey: (row, index) => `${row.botName}:${row.host ?? '*'}:${index}`,
  columns: () => [
    // The governance route binds no sort parameter — no sort affordance.
    { ...makeColumn<BotGovernanceRow, string>('botName', 'AI agent', (row) => row.botName, {
      size: 200,
      cell: (value) => <Badge tone="ember">{value}</Badge>,
      meta: { serverField: 'botName', queryable: true, valueKind: 'text' }
    }), enableSorting: false },
    { ...makeColumn<BotGovernanceRow, string | null>('host', 'Host', (row) => row.host, {
      size: 220,
      cell: (value) => <TextCell value={value} mono />,
      meta: { serverField: 'host', queryable: true, valueKind: 'text' }
    }), enableSorting: false },
    { ...makeColumn<BotGovernanceRow, boolean>('allowed', 'Effective access', (row) => row.allowed, {
      size: 140,
      cell: (value) => <BoolCell value={value} trueLabel="allowed" falseLabel="blocked" />
    }), enableSorting: false },
    { ...makeColumn<BotGovernanceRow, boolean | null>('robotsTxtAllowed', 'robots.txt', (row) => row.robotsTxtAllowed, {
      size: 120,
      cell: (value) => <TriStateCell value={value} />
    }), enableSorting: false },
    { ...makeColumn<BotGovernanceRow, boolean | null>('llmsTxtAllowed', 'llms.txt', (row) => row.llmsTxtAllowed, {
      size: 120,
      cell: (value) => <TriStateCell value={value} />
    }), enableSorting: false },
    { ...makeColumn<BotGovernanceRow, string | null>('notes', 'Notes', (row) => row.notes, {
      size: 300,
      cell: (value) => <TextCell value={value} />
    }), enableSorting: false }
  ],
  queryFields: () => [
    makeField('botName', 'Bot name', 'text', { serverFilter: 'botName' }),
    makeField('host', 'Host', 'text', { serverFilter: 'host' })
  ],
  fetchSummary: async (api, auditId) => {
    const envelope = await api.request('geo.llmsTxt', { params: { id: auditId }, query: { limit: 20 } });
    if (envelope.error || !envelope.data) return [];
    return envelope.data.slice(0, 6).map((validation) => ({
      label: `llms.txt · ${validation.host}`,
      value: validation.present ? (validation.valid ? 'valid' : `${validation.issues.length} issue${validation.issues.length === 1 ? '' : 's'}`) : 'missing',
      tone: !validation.present ? ('warning' as const) : validation.valid ? ('success' as const) : ('danger' as const)
    }));
  },
  drilldown: (row): DrilldownSpec => ({
    title: row.botName,
    subtitle: row.host ?? 'all hosts',
    fields: [
      { label: 'Effective access', value: <BoolCell value={row.allowed} trueLabel="allowed" falseLabel="blocked" /> },
      { label: 'robots.txt verdict', value: <TriStateCell value={row.robotsTxtAllowed} /> },
      { label: 'llms.txt verdict', value: <TriStateCell value={row.llmsTxtAllowed} /> },
      { label: 'Notes', value: <TextCell value={row.notes} /> }
    ]
  })
};

// ── Analytics & Tagging ──────────────────────────────────────────────────

type AnalyticsRow = AnalyticsTag | Finding;

const ANALYTICS_VIEWS: readonly ExplorerView[] = [
  { id: 'tags', label: 'Tag inventory', query: { view: 'tags' } },
  { id: 'findings', label: 'Analytics findings', query: { view: 'findings' } }
];

function isTag(row: AnalyticsRow): row is AnalyticsTag {
  return 'occurrences' in row;
}

export const analyticsExplorer: ExplorerDefinition<AnalyticsRow> = {
  id: 'analytics',
  title: 'Analytics & Tagging',
  glyph: '📊',
  tagline: 'MarTech container inventory, consent rollup, and tagging findings.',
  route: 'analytics.list',
  adapterKind: 'analytics',
  views: ANALYTICS_VIEWS,
  rowKey: (row, index) => (isTag(row) ? `${row.name}:${row.containerId ?? ''}:${row.urlId ?? ''}:${index}` : row.id),
  columns: (context) => {
    // The analytics route binds no sort parameter — no sort affordance in
    // either view.
    if (context.viewId === 'findings') {
      return [
        { ...makeColumn<AnalyticsRow, string>('severity', 'Severity', (row) => (isTag(row) ? '' : row.severity), {
          size: 110,
          cell: (value) => <Badge tone={severityTone(value)}>{value}</Badge>
        }), enableSorting: false },
        { ...makeColumn<AnalyticsRow, string>('category', 'Category', (row) => (isTag(row) ? '' : row.category), {
          size: 150,
          cell: (value) => <Badge tone="neutral">{value}</Badge>
        }), enableSorting: false },
        { ...makeColumn<AnalyticsRow, string>('ruleKey', 'Rule', (row) => (isTag(row) ? '' : row.ruleKey), {
          size: 200,
          cell: (value) => <TextCell value={value} mono />
        }), enableSorting: false },
        { ...makeColumn<AnalyticsRow, string>('message', 'Message', (row) => (isTag(row) ? '' : row.message), {
          size: 420,
          cell: (value) => <TextCell value={value} />
        }), enableSorting: false }
      ];
    }
    return [
      { ...makeColumn<AnalyticsRow, string>('name', 'Tag / container', (row) => (isTag(row) ? row.name : ''), {
        size: 240,
        cell: (value) => <Badge tone="ember">{value}</Badge>
      }), enableSorting: false },
      { ...makeColumn<AnalyticsRow, string | null>('containerId', 'Container id', (row) => (isTag(row) ? row.containerId : null), {
        size: 170,
        cell: (value) => <TextCell value={value} mono />,
        meta: { serverField: 'containerId', queryable: true, valueKind: 'text' }
      }), enableSorting: false },
      { ...makeColumn<AnalyticsRow, string>('kind', 'Kind', (row) => (isTag(row) ? row.kind : ''), {
        size: 150,
        cell: (value) => <Badge tone="info">{value}</Badge>
      }), enableSorting: false },
      { ...makeColumn<AnalyticsRow, number>('occurrences', 'Occurrences', (row) => (isTag(row) ? row.occurrences : 0), {
        size: 120,
        cell: (value) => <NumberCell value={value} />,
        meta: { numeric: true }
      }), enableSorting: false },
      { ...makeColumn<AnalyticsRow, string | null>('urlId', 'URL record', (row) => (isTag(row) ? row.urlId : row.urlId), {
        size: 180,
        cell: (value) => <TextCell value={value} mono />
      }), enableSorting: false }
    ];
  },
  queryFields: () => [
    makeField('containerId', 'Container id', 'text', { serverFilter: 'containerId' }),
    makeField('view', 'View', 'enum', { serverFilter: 'view', enumValues: ['tags', 'consent', 'findings'] })
  ],
  fetchSummary: async (api, auditId) => {
    const envelope = await api.request('analytics.coverage', { params: { id: auditId }, query: { limit: 20 } });
    if (envelope.error || !envelope.data) return [];
    return envelope.data.slice(0, 6).map((coverage) => ({
      label: coverage.containerName ?? coverage.containerId,
      value: `${(coverage.coverage * 100).toFixed(0)}% of ${coverage.indexablePages} indexable pages`,
      tone: coverage.coverage >= 0.9 ? ('success' as const) : coverage.coverage >= 0.5 ? ('warning' as const) : ('danger' as const)
    }));
  },
  drilldown: (row): DrilldownSpec => {
    if (isTag(row)) {
      return {
        title: row.name,
        subtitle: `${row.kind} · ${row.containerId ?? 'no container'}`,
        fields: [
          { label: 'Kind', value: <Badge tone="info">{row.kind}</Badge> },
          { label: 'Container', value: <TextCell value={row.containerId} mono /> },
          { label: 'Occurrences', value: <NumberCell value={row.occurrences} /> },
          { label: 'URL record', value: <TextCell value={row.urlId} mono /> }
        ]
      };
    }
    return {
      title: row.ruleKey,
      subtitle: `${row.severity} · ${row.category}`,
      fields: [
        { label: 'Severity', value: <Badge tone={severityTone(row.severity)}>{row.severity}</Badge> },
        { label: 'Message', value: <TextCell value={row.message} /> },
        { label: 'URL record', value: <TextCell value={row.urlId} mono /> }
      ]
    };
  }
};

/** Consent rollup chips from the analytics response meta (host renders). */
export function consentChips(consent: AnalyticsConsentSummary): readonly { label: string; value: string; tone: 'success' | 'warning' | 'danger' | 'info' }[] {
  return [
    { label: 'Consent Mode v2', value: consent.consentModeV2Present ? 'present' : 'absent', tone: consent.consentModeV2Present ? 'success' : 'danger' },
    { label: 'Consent beacon', value: consent.beaconPresent ? 'present' : 'absent', tone: consent.beaconPresent ? 'success' : 'warning' },
    { label: 'Pages with consent', value: consent.pagesWithConsent.toLocaleString(), tone: 'info' },
    { label: 'Pages without consent', value: consent.pagesWithoutConsent.toLocaleString(), tone: consent.pagesWithoutConsent > 0 ? 'warning' : 'success' }
  ];
}

// ── Redirect Matrix ──────────────────────────────────────────────────────

function verdictTone(verdict: string): 'success' | 'danger' | 'warning' | 'neutral' {
  const key = verdict.toLowerCase();
  if (key === 'pass' || key === 'ok' || key === 'valid') return 'success';
  if (key === 'fail' || key === 'broken' || key === 'invalid') return 'danger';
  if (key === 'pending' || key === 'warning') return 'warning';
  return 'neutral';
}

export const redirectMatrixExplorer: ExplorerDefinition<RedirectMatrixResult> = {
  id: 'redirect-matrix',
  title: 'Redirect Matrix',
  glyph: '⇄',
  tagline: 'Migration mapping validation — per-row verdicts, hop chains, canonical evidence.',
  route: 'matrix.results',
  adapterKind: 'array',
  rowKey: (row, index) => `${row.urlId}:${index}`,
  columns: () => [
    // The matrix-results route binds no sort parameter — no sort affordance.
    { ...makeColumn<RedirectMatrixResult, string>('sourceUrl', 'Old URL', (row) => row.sourceUrl, {
      size: 320,
      cell: (value) => <UrlCell url={value} />
    }), enableSorting: false },
    { ...makeColumn<RedirectMatrixResult, string>('expectedTargetUrl', 'Expected target', (row) => row.expectedTargetUrl, {
      size: 320,
      cell: (value) => <UrlCell url={value} />
    }), enableSorting: false },
    { ...makeColumn<RedirectMatrixResult, string>('verdict', 'Verdict', (row) => row.verdict, {
      size: 110,
      cell: (value) => <Badge tone={verdictTone(value)}>{value}</Badge>,
      meta: { serverField: 'verdict', queryable: true, valueKind: 'text' }
    }), enableSorting: false },
    { ...makeColumn<RedirectMatrixResult, readonly { url: string; statusCode: number }[]>('hopChain', 'Hop chain', (row) => row.hopChain, {
      size: 180,
      cell: (value) => <HopChainCell hops={value} />
    }), enableSorting: false },
    { ...makeColumn<RedirectMatrixResult, string | null>('canonicalUrl', 'Canonical', (row) => row.canonicalUrl, {
      size: 280,
      cell: (value) => <UrlCell url={value} muted />
    }), enableSorting: false },
    { ...makeColumn<RedirectMatrixResult, string | null>('evidence', 'Evidence', (row) => row.evidence, {
      size: 240,
      cell: (value) => <TextCell value={value} />
    }), enableSorting: false }
  ],
  queryFields: () => [
    makeField('verdict', 'Verdict', 'text', { serverFilter: 'verdict' })
  ],
  drilldown: (row): DrilldownSpec => ({
    title: row.sourceUrl,
    subtitle: `verdict: ${row.verdict}`,
    fields: [
      { label: 'Expected target', value: <UrlCell url={row.expectedTargetUrl} /> },
      { label: 'Verdict', value: <Badge tone={verdictTone(row.verdict)}>{row.verdict}</Badge> },
      { label: 'Hop chain', value: <HopChainCell hops={row.hopChain} /> },
      { label: 'Canonical', value: <UrlCell url={row.canonicalUrl} /> },
      { label: 'Evidence', value: <TextCell value={row.evidence} /> }
    ]
  }),
  emptyHint: 'Upload a migration mapping (old URL → expected target) to seed the matrix and auto-create a list-mode audit.'
};

// ── WAF Blocks ───────────────────────────────────────────────────────────

export const wafExplorer: ExplorerDefinition<WafBlock> = {
  id: 'waf',
  title: 'WAF Blocks',
  glyph: '🛡',
  tagline: 'WAF/challenge detections — provider, class, confidence, and evidence.',
  route: 'waf.listBlocks',
  adapterKind: 'array',
  defaultSort: [{ field: 'detectedAt', order: 'desc' }],
  rowKey: (row, index) => `${row.urlId}:${row.provider}:${index}`,
  columns: () => [
    // WAF route whitelist: [detectedAt, provider, confidence] — the other
    // columns carry no sort affordance.
    { ...makeColumn<WafBlock, string>('canonicalUrl', 'Blocked URL', (row) => row.canonicalUrl, {
      size: 360,
      cell: (value) => <UrlCell url={value} />
    }), enableSorting: false },
    makeColumn<WafBlock, string>('provider', 'Provider', (row) => row.provider, {
      size: 150,
      cell: (value) => <Badge tone="danger">{value}</Badge>,
      meta: { serverField: 'provider', queryable: true, valueKind: 'text' }
    }),
    { ...makeColumn<WafBlock, string>('challengeClass', 'Challenge class', (row) => row.challengeClass, {
      size: 160,
      cell: (value) => <Badge tone="warning">{value}</Badge>,
      meta: { serverField: 'challengeClass', queryable: true, valueKind: 'text' }
    }), enableSorting: false },
    makeColumn<WafBlock, number>('confidence', 'Confidence', (row) => row.confidence, {
      size: 170,
      cell: (value) => <ConfidenceCell value={value} />,
      meta: { serverField: 'confidence', numeric: true }
    }),
    { ...makeColumn<WafBlock, string | null | undefined>('signatureKey', 'Signature', (row) => row.signatureKey, {
      size: 180,
      cell: (value) => <TextCell value={value} mono />
    }), enableSorting: false },
    makeColumn<WafBlock, string>('detectedAt', 'Detected', (row) => row.detectedAt, {
      size: 150,
      cell: (value) => <TimestampCell value={value} />,
      meta: { serverField: 'detectedAt' }
    })
  ],
  queryFields: () => [
    makeField('provider', 'Provider', 'text', { serverFilter: 'provider' }),
    makeField('challengeClass', 'Challenge class', 'text', { serverFilter: 'challengeClass' }),
    makeField('urlId', 'URL record id', 'text', { serverFilter: 'urlId' })
  ],
  fetchSummary: async (api, auditId) => {
    const envelope = await api.request('waf.blocksSummary', { params: { id: auditId } });
    if (envelope.error || !envelope.data) return [];
    const summary = envelope.data;
    const topProviders = Object.entries(summary.byProvider)
      .sort((a, b) => b[1] - a[1])
      .slice(0, 3)
      .map(([provider, count]) => ({
        label: provider,
        value: `${count.toLocaleString()} blocks`,
        tone: 'danger' as const
      }));
    return [
      { label: 'Total blocked', value: summary.totalBlocked.toLocaleString(), tone: 'danger' },
      { label: 'Block rate', value: `${(summary.blockRate * 100).toFixed(1)}%`, tone: summary.blockRate > 0.1 ? 'danger' : 'warning' },
      ...topProviders
    ];
  },
  drilldown: (row): DrilldownSpec => ({
    title: row.canonicalUrl,
    subtitle: `${row.provider} · ${row.challengeClass}`,
    fields: [
      { label: 'Provider', value: <Badge tone="danger">{row.provider}</Badge> },
      { label: 'Challenge class', value: <Badge tone="warning">{row.challengeClass}</Badge> },
      { label: 'Confidence', value: <ConfidenceCell value={row.confidence} /> },
      { label: 'Signature', value: <TextCell value={row.signatureKey} mono /> },
      { label: 'Detected', value: <TimestampCell value={row.detectedAt} /> },
      ...(row.evidence?.snippet ? [{ label: 'Body evidence', value: <TextCell value={row.evidence.snippet} mono /> }] : []),
      ...(row.evidence?.headers
        ? Object.entries(row.evidence.headers).slice(0, 6).map(([header, value]) => ({
            label: header,
            value: <TextCell value={value} mono />
          }))
        : [])
    ]
  })
};
