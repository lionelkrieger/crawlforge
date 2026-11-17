/**
 * Core explorer definitions: URL, Issue, Link, Custom Extraction, N-Gram
 * (FS-CRAWL-002 §8.3). Each binds one frozen route; columns and query
 * fields derive from the locked DTOs; drill-downs fetch real detail.
 *
 * @module surfaces/explorers/coreExplorers
 */

import type { UrlRecordSummary, Finding, LinkEdge } from '../../contracts/api/audits';
import type { ExtractedContentRow } from '../../contracts/api/content';
import type { NgramRow } from '../../contracts/api/ngrams';
import type { ExplorerDefinition, DrilldownSpec } from './explorerTypes';
import { makeColumn, makeField } from './columnUtils';
import {
  Badge, statusTone, severityTone, UrlCell, BoolCell, NumberCell,
  PercentCell, TextCell, ExtractionValueCell, HopChainCell
} from './cells';
import { extractionField } from '../../grid/queryBuilder';

// ── URL Explorer ─────────────────────────────────────────────────────────

export const urlExplorer: ExplorerDefinition<UrlRecordSummary> = {
  id: 'urls',
  title: 'URL Explorer',
  glyph: '🌐',
  tagline: 'Every crawled URL — status, indexability, depth, and block verdicts.',
  route: 'audits.listUrls',
  adapterKind: 'array',
  defaultSort: [{ field: 'depth', order: 'asc' }],
  rowKey: (row) => row.id,
  columns: () => [
    makeColumn<UrlRecordSummary, string>('canonicalUrl', 'URL', (row) => row.canonicalUrl, {
      size: 380,
      cell: (value) => <UrlCell url={value} />,
      meta: { serverField: 'canonicalUrl', queryable: true, valueKind: 'text' }
    }),
    makeColumn<UrlRecordSummary, number>('statusCode', 'Status', (row) => row.statusCode, {
      size: 90,
      cell: (value, row) => <Badge tone={statusTone(row.statusClass)}>{value}</Badge>,
      meta: { serverField: 'statusCode', numeric: true }
    }),
    // Sort affordance audit (WP-06-03 reopen): the urls route whitelist is
    // [id, canonicalUrl, depth, statusCode] — other columns set
    // enableSorting: false so the UI never offers a sort the server 400s.
    { ...makeColumn<UrlRecordSummary, string>('statusClass', 'Class', (row) => row.statusClass, {
      size: 80,
      cell: (value) => <Badge tone={statusTone(value)}>{value}</Badge>,
      meta: { serverField: 'statusClass', queryable: true, valueKind: 'enum', enumValues: ['1xx', '2xx', '3xx', '4xx', '5xx'] }
    }), enableSorting: false },
    { ...makeColumn<UrlRecordSummary, string | null>('contentType', 'Content type', (row) => row.contentType, {
      size: 160,
      cell: (value) => <TextCell value={value} mono />,
      meta: { serverField: 'contentType', queryable: true, valueKind: 'text' }
    }), enableSorting: false },
    makeColumn<UrlRecordSummary, number>('depth', 'Depth', (row) => row.depth, {
      size: 80,
      cell: (value) => <NumberCell value={value} />,
      meta: { serverField: 'depth', queryable: true, valueKind: 'number', numeric: true }
    }),
    { ...makeColumn<UrlRecordSummary, boolean>('indexable', 'Indexable', (row) => row.indexable, {
      size: 100,
      cell: (value) => <BoolCell value={value} />,
      meta: { serverField: 'indexable', queryable: true, valueKind: 'boolean' }
    }), enableSorting: false },
    { ...makeColumn<UrlRecordSummary, string | null>('title', 'Title', (row) => row.title, {
      size: 260,
      cell: (value) => <TextCell value={value} />
    }), enableSorting: false },
    { ...makeColumn<UrlRecordSummary, string | null | undefined>('blockReason', 'Block reason', (row) => row.blockReason, {
      size: 150,
      cell: (value) => (value ? <Badge tone="warning">{value}</Badge> : <span className="cf-cell-empty">—</span>),
      meta: { serverField: 'blockReason', queryable: true, valueKind: 'text' }
    }), enableSorting: false },
    { ...makeColumn<UrlRecordSummary, string | null | undefined>('wafProvider', 'WAF provider', (row) => row.wafProvider, {
      size: 140,
      cell: (value) => (value ? <Badge tone="danger">{value}</Badge> : <span className="cf-cell-empty">—</span>),
      meta: { serverField: 'wafProvider', queryable: true, valueKind: 'text' }
    }), enableSorting: false }
  ],
  queryFields: () => [
    makeField('canonicalUrl', 'URL', 'text'),
    makeField('statusClass', 'Status class', 'enum', { serverFilter: 'statusClass', enumValues: ['1xx', '2xx', '3xx', '4xx', '5xx'] }),
    makeField('contentType', 'Content type', 'text', { serverFilter: 'contentType' }),
    makeField('depth', 'Depth', 'number', { serverFilter: 'depth' }),
    makeField('indexable', 'Indexable', 'boolean', { serverFilter: 'indexable' }),
    makeField('blockReason', 'Block reason', 'text', { serverFilter: 'blockReason' }),
    makeField('wafProvider', 'WAF provider', 'text', { serverFilter: 'wafProvider' })
  ],
  drilldown: (row): DrilldownSpec => ({
    title: row.canonicalUrl,
    subtitle: `URL record ${row.id}`,
    fields: [
      { label: 'Status', value: <Badge tone={statusTone(row.statusClass)}>{row.statusCode} · {row.statusClass}</Badge> },
      { label: 'Content type', value: <TextCell value={row.contentType} mono /> },
      { label: 'Depth', value: <NumberCell value={row.depth} /> },
      { label: 'Indexable', value: <BoolCell value={row.indexable} /> },
      { label: 'Title', value: <TextCell value={row.title} /> },
      ...(row.blockReason ? [{ label: 'Block reason', value: <Badge tone="warning">{row.blockReason}</Badge> }] : []),
      ...(row.wafProvider ? [{ label: 'WAF provider', value: <Badge tone="danger">{row.wafProvider}</Badge> }] : [])
    ],
    fetchDetail: async (api, auditId) => {
      const envelope = await api.request('audits.getUrl', { params: { id: auditId, urlId: row.id } });
      if (envelope.error || !envelope.data) return null;
      const detail = envelope.data;
      return {
        sections: [
          {
            heading: `Findings (${detail.findings.length})`,
            fields: detail.findings.slice(0, 8).map((finding) => ({
              label: finding.ruleKey,
              value: <span><Badge tone={severityTone(finding.severity)}>{finding.severity}</Badge> {finding.message}</span>
            }))
          },
          {
            heading: `Redirect chain (${detail.redirectChain.length} hops)`,
            fields: [{ label: 'Chain', value: <HopChainCell hops={detail.redirectChain} /> }]
          },
          {
            heading: 'Element inventory',
            fields: [
              { label: 'Total elements', value: <NumberCell value={detail.elementInventory.totalElements} /> },
              ...Object.entries(detail.elementInventory.counts).slice(0, 6).map(([tag, count]) => ({
                label: tag,
                value: <NumberCell value={count} />
              }))
            ]
          },
          ...(detail.wafDetection
            ? [{
                heading: 'WAF detection',
                fields: [
                  { label: 'Provider', value: <Badge tone="danger">{detail.wafDetection.provider}</Badge> },
                  { label: 'Challenge class', value: <TextCell value={detail.wafDetection.challengeClass} /> },
                  { label: 'Confidence', value: <PercentCell value={detail.wafDetection.confidence} /> }
                ]
              }]
            : []),
          ...(detail.serpMetrics
            ? [{
                heading: 'SERP preview',
                fields: [
                  { label: 'Title width', value: <NumberCell value={detail.serpMetrics.titlePixelWidth} /> },
                  { label: 'Title truncated', value: <BoolCell value={detail.serpMetrics.titleTruncated} invert trueLabel="truncated" falseLabel="fits" /> },
                  { label: 'Description truncated', value: <BoolCell value={detail.serpMetrics.descriptionTruncated} invert trueLabel="truncated" falseLabel="fits" /> }
                ]
              }]
            : [])
        ]
      };
    }
  })
};

// ── Issue Explorer ───────────────────────────────────────────────────────

export const issueExplorer: ExplorerDefinition<Finding> = {
  id: 'issues',
  title: 'Issue Explorer',
  glyph: '⚠',
  tagline: 'Audit findings by severity, category, and rule — with evidence.',
  route: 'audits.listFindings',
  adapterKind: 'array',
  defaultSort: [{ field: 'severity', order: 'asc' }],
  rowKey: (row) => row.id,
  columns: () => [
    makeColumn<Finding, string>('severity', 'Severity', (row) => row.severity, {
      size: 110,
      cell: (value) => <Badge tone={severityTone(value)}>{value}</Badge>,
      meta: { serverField: 'severity', queryable: true, valueKind: 'text' }
    }),
    makeColumn<Finding, string>('category', 'Category', (row) => row.category, {
      size: 150,
      cell: (value) => <Badge tone="neutral">{value}</Badge>,
      meta: { serverField: 'category', queryable: true, valueKind: 'text' }
    }),
    makeColumn<Finding, string>('ruleKey', 'Rule', (row) => row.ruleKey, {
      size: 200,
      cell: (value) => <TextCell value={value} mono />,
      meta: { serverField: 'ruleKey', queryable: true, valueKind: 'text' }
    }),
    // Findings route whitelist: [id, ruleKey, severity, category].
    { ...makeColumn<Finding, string>('message', 'Message', (row) => row.message, {
      size: 420,
      cell: (value) => <TextCell value={value} />
    }), enableSorting: false },
    { ...makeColumn<Finding, string | null>('urlId', 'URL record', (row) => row.urlId, {
      size: 180,
      cell: (value) => <TextCell value={value} mono />,
      meta: { serverField: 'urlId', queryable: true, valueKind: 'text' }
    }), enableSorting: false }
  ],
  queryFields: () => [
    makeField('severity', 'Severity', 'text', { serverFilter: 'severity' }),
    makeField('category', 'Category', 'text', { serverFilter: 'category' }),
    makeField('ruleKey', 'Rule key', 'text', { serverFilter: 'ruleKey' }),
    makeField('urlId', 'URL record id', 'text', { serverFilter: 'urlId' })
  ],
  drilldown: (row): DrilldownSpec => ({
    title: row.ruleKey,
    subtitle: `${row.severity} · ${row.category}`,
    fields: [
      { label: 'Severity', value: <Badge tone={severityTone(row.severity)}>{row.severity}</Badge> },
      { label: 'Category', value: <Badge tone="neutral">{row.category}</Badge> },
      { label: 'Message', value: <TextCell value={row.message} /> },
      { label: 'URL record', value: <TextCell value={row.urlId} mono /> },
      ...(row.evidence?.snippet ? [{ label: 'Evidence snippet', value: <TextCell value={row.evidence.snippet} mono /> }] : []),
      ...(row.evidence?.selector ? [{ label: 'Selector', value: <TextCell value={row.evidence.selector} mono /> }] : []),
      ...(row.evidence?.details
        ? Object.entries(row.evidence.details).slice(0, 8).map(([key, value]) => ({
            label: key,
            value: <TextCell value={typeof value === 'string' ? value : JSON.stringify(value)} mono />
          }))
        : [])
    ]
  })
};

// ── Link Explorer ────────────────────────────────────────────────────────

export const linkExplorer: ExplorerDefinition<LinkEdge> = {
  id: 'links',
  title: 'Link Explorer',
  glyph: '🔗',
  tagline: 'Internal/external edges with DOM-zone equity weighting.',
  route: 'audits.listLinks',
  adapterKind: 'array',
  rowKey: (row, index) => `${row.sourceUrlId}->${row.targetUrlId}:${index}`,
  columns: () => [
    // serverField maps the UI sort to the backend whitelist (id/sourceUrlId/
    // targetUrlId — AuditExplorerController.ListLinksAsync). Without it the
    // engine sent sort=sourceUrl/targetUrl and every sort click 400'd
    // (WP-06-03 reopen, defect B found by the WP-07-03 real-browser bench).
    makeColumn<LinkEdge, string>('sourceUrl', 'Source', (row) => row.sourceUrl, {
      size: 320,
      cell: (value) => <UrlCell url={value} />,
      meta: { serverField: 'sourceUrlId' }
    }),
    makeColumn<LinkEdge, string>('targetUrl', 'Target', (row) => row.targetUrl, {
      size: 320,
      cell: (value) => <UrlCell url={value} />,
      meta: { serverField: 'targetUrlId' }
    }),
    // Links route whitelist: [id, sourceUrlId, targetUrlId] — only the two
    // mapped URL columns are sortable.
    { ...makeColumn<LinkEdge, boolean>('internal', 'Scope', (row) => row.internal, {
      size: 100,
      cell: (value) => <Badge tone={value ? 'success' : 'info'}>{value ? 'internal' : 'external'}</Badge>,
      meta: { serverField: 'internal', queryable: true, valueKind: 'boolean' }
    }), enableSorting: false },
    { ...makeColumn<LinkEdge, string | null>('anchorText', 'Anchor', (row) => row.anchorText, {
      size: 200,
      cell: (value) => <TextCell value={value} />
    }), enableSorting: false },
    { ...makeColumn<LinkEdge, string | undefined>('domZone', 'DOM zone', (row) => row.domZone, {
      size: 120,
      cell: (value) => (value ? <Badge tone="ember">{value}</Badge> : <span className="cf-cell-empty">—</span>),
      meta: { serverField: 'domZone', queryable: true, valueKind: 'text' }
    }), enableSorting: false },
    { ...makeColumn<LinkEdge, number | undefined>('equityWeight', 'Zone weight', (row) => row.equityWeight, {
      size: 110,
      cell: (value) => <NumberCell value={value} digits={2} />,
      meta: { serverField: 'equityWeight', numeric: true }
    }), enableSorting: false },
    { ...makeColumn<LinkEdge, number | undefined>('weightedEquity', 'Weighted equity', (row) => row.weightedEquity, {
      size: 130,
      cell: (value) => <NumberCell value={value} digits={3} />,
      meta: { serverField: 'weightedEquity', numeric: true }
    }), enableSorting: false }
  ],
  queryFields: () => [
    makeField('internal', 'Internal link', 'boolean', { serverFilter: 'internal' }),
    makeField('domZone', 'DOM zone', 'text', { serverFilter: 'domZone' }),
    makeField('sourceUrlId', 'Source URL id', 'text', { serverFilter: 'sourceUrlId' }),
    makeField('targetUrlId', 'Target URL id', 'text', { serverFilter: 'targetUrlId' })
  ],
  drilldown: (row): DrilldownSpec => ({
    title: 'Link edge',
    subtitle: `${row.internal ? 'internal' : 'external'} · ${row.domZone ?? 'unzoned'}`,
    fields: [
      { label: 'Source', value: <UrlCell url={row.sourceUrl} /> },
      { label: 'Target', value: <UrlCell url={row.targetUrl} /> },
      { label: 'Anchor', value: <TextCell value={row.anchorText} /> },
      { label: 'DOM zone', value: row.domZone ? <Badge tone="ember">{row.domZone}</Badge> : <span className="cf-cell-empty">—</span> },
      { label: 'Zone weight', value: <NumberCell value={row.equityWeight} digits={2} /> },
      { label: 'Weighted equity', value: <NumberCell value={row.weightedEquity} digits={3} /> }
    ]
  })
};

// ── Custom Extraction Explorer ───────────────────────────────────────────

export const extractionExplorer: ExplorerDefinition<ExtractedContentRow> = {
  id: 'extraction',
  title: 'Custom Extraction',
  glyph: '⛏',
  tagline: 'Dynamic rule columns — multi-valued cells expand to the busiest page.',
  route: 'audits.listExtractedContent',
  adapterKind: 'dynamic',
  rowKey: (row, index) => `${row.urlId}:${row.ruleKey}:${index}`,
  columns: (context) => [
    // The extracted-content route binds no sort parameter — no column offers
    // a sort affordance (sort affordance audit, WP-06-03 reopen).
    { ...makeColumn<ExtractedContentRow, string>('canonicalUrl', 'URL', (row) => row.canonicalUrl, {
      size: 340,
      cell: (value) => <UrlCell url={value} />
    }), enableSorting: false },
    { ...makeColumn<ExtractedContentRow, string>('ruleKey', 'Rule', (row) => row.ruleKey, {
      size: 160,
      cell: (value) => <Badge tone="ember">{value}</Badge>,
      meta: { serverField: 'ruleKey', queryable: true, valueKind: 'text' }
    }), enableSorting: false },
    // Dynamic columns from the locked response column order.
    ...context.dynamicColumns.map((columnName) =>
      ({ ...makeColumn<ExtractedContentRow, unknown>(`dyn:${columnName}`, columnName, (row) => row.values[columnName], {
        size: 180,
        cell: (value) => <ExtractionValueCell value={value as never} />,
        meta: { dynamicOrigin: 'extraction', formatterKey: `extract:${columnName}` }
      }), enableSorting: false })
    )
  ],
  queryFields: (context) => [
    makeField('canonicalUrl', 'URL', 'text'),
    makeField('ruleKey', 'Rule key', 'text', { serverFilter: 'ruleKey' }),
    ...context.dynamicColumns.map((columnName) =>
      makeField(extractionField('*', columnName), `Extracted: ${columnName}`, 'text', { dynamicOrigin: 'extraction' })
    )
  ],
  fetchSummary: async (api, auditId) => {
    const envelope = await api.request('audits.extractionSummary', { params: { id: auditId } });
    if (envelope.error || !envelope.data) return [];
    return envelope.data.rules.slice(0, 6).map((rule) => ({
      label: rule.ruleKey,
      value: `${rule.pagesMatched} pages · ${rule.valuesCaptured} values`,
      tone: rule.discards > 0 ? ('warning' as const) : ('success' as const)
    }));
  },
  drilldown: (row): DrilldownSpec => ({
    title: row.canonicalUrl,
    subtitle: `Extraction rule ${row.ruleKey}`,
    fields: [
      { label: 'URL record', value: <TextCell value={row.urlId} mono /> },
      { label: 'Rule', value: <Badge tone="ember">{row.ruleKey}</Badge> },
      ...Object.entries(row.values).map(([columnName, value]) => ({
        label: columnName,
        value: <ExtractionValueCell value={value} />
      }))
    ]
  }),
  emptyHint: 'No extraction rules matched yet. Configure custom extraction rules in the audit configuration, then re-audit.'
};

// ── N-Gram Explorer ──────────────────────────────────────────────────────

export const ngramExplorer: ExplorerDefinition<NgramRow> = {
  id: 'ngrams',
  title: 'N-Gram Explorer',
  glyph: '▤',
  tagline: 'Topical density across the 4-zone model — site summary or per-URL.',
  route: 'ngrams.list',
  adapterKind: 'array',
  defaultSort: [{ field: 'count', order: 'desc' }],
  rowKey: (row, index) => `${row.ngram}:${row.n}:${row.zone}:${row.urlId ?? 'site'}:${index}`,
  columns: () => [
    // Ngrams route whitelist: [df, tf, count] — only count maps to a UI
    // column; the rest carry no sort affordance.
    { ...makeColumn<NgramRow, string>('ngram', 'N-gram', (row) => row.ngram, {
      size: 280,
      cell: (value) => <TextCell value={value} mono />,
      meta: { serverField: 'ngram', queryable: true, valueKind: 'text' }
    }), enableSorting: false },
    { ...makeColumn<NgramRow, number>('n', 'Gram size', (row) => row.n, {
      size: 100,
      cell: (value) => <Badge tone="neutral">{value}-gram</Badge>,
      meta: { serverField: 'n', queryable: true, valueKind: 'number', numeric: true }
    }), enableSorting: false },
    { ...makeColumn<NgramRow, string>('zone', 'Zone', (row) => row.zone, {
      size: 130,
      cell: (value) => <Badge tone="info">{value}</Badge>,
      meta: { serverField: 'zone', queryable: true, valueKind: 'text' }
    }), enableSorting: false },
    makeColumn<NgramRow, number>('count', 'Count', (row) => row.count, {
      size: 100,
      cell: (value) => <NumberCell value={value} />,
      meta: { serverField: 'count', queryable: true, valueKind: 'number', numeric: true }
    }),
    { ...makeColumn<NgramRow, number | undefined>('density', 'Density', (row) => row.density, {
      size: 160,
      cell: (value) => <PercentCell value={value} tone="info" />,
      meta: { serverField: 'density', numeric: true }
    }), enableSorting: false },
    { ...makeColumn<NgramRow, string | null | undefined>('urlId', 'Scope', (row) => row.urlId, {
      size: 180,
      cell: (value) => (value ? <TextCell value={value} mono /> : <Badge tone="ember">site summary</Badge>),
      meta: { serverField: 'urlId', queryable: true, valueKind: 'text' }
    }), enableSorting: false }
  ],
  queryFields: () => [
    makeField('ngram', 'N-gram', 'text'),
    makeField('zone', 'Zone', 'text', { serverFilter: 'zone' }),
    makeField('n', 'Gram size', 'number', { serverFilter: 'n' }),
    makeField('minCount', 'Minimum count', 'number', { serverFilter: 'minCount' }),
    makeField('urlId', 'URL record id', 'text', { serverFilter: 'urlId' })
  ],
  drilldown: (row): DrilldownSpec => ({
    title: `“${row.ngram}”`,
    subtitle: `${row.n}-gram · ${row.zone}`,
    fields: [
      { label: 'Occurrences', value: <NumberCell value={row.count} /> },
      { label: 'Topical density', value: <PercentCell value={row.density} tone="info" /> },
      { label: 'Zone', value: <Badge tone="info">{row.zone}</Badge> },
      { label: 'Scope', value: row.urlId ? <TextCell value={row.urlId} mono /> : <Badge tone="ember">site summary</Badge> }
    ]
  })
};
