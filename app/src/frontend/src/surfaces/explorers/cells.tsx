/**
 * Shared explorer cell renderers — badges, meters, and value formatting in
 * the shell's night/ember/signal language. Pure presentational components;
 * every explorer column composes these so the eleven surfaces read as one
 * instrument.
 *
 * @module surfaces/explorers/cells
 */

import type { ReactNode } from 'react';
import type { ExtractionValue } from '../../contracts/api/content';

/** Tone → CSS modifier mapping (semantic colors from shell.css). */
export type BadgeTone = 'neutral' | 'success' | 'warning' | 'danger' | 'info' | 'ember';

/** Compact status/severity badge. */
export function Badge({ tone = 'neutral', children, title }: { tone?: BadgeTone; children: ReactNode; title?: string }) {
  return (
    <span className={`cf-badge cf-badge-${tone}`} title={title} data-tone={tone}>
      {children}
    </span>
  );
}

/** HTTP status class → semantic tone. */
export function statusTone(statusClass: string): BadgeTone {
  switch (statusClass) {
    case '2xx': return 'success';
    case '3xx': return 'info';
    case '4xx': return 'warning';
    case '5xx': return 'danger';
    default: return 'neutral';
  }
}

/** Finding severity → semantic tone (local catalogue keys). */
export function severityTone(severity: string): BadgeTone {
  const key = severity.toLowerCase();
  if (key === 'critical' || key === 'error' || key === 'high') return 'danger';
  if (key === 'warning' || key === 'medium') return 'warning';
  if (key === 'info' || key === 'low' || key === 'notice') return 'info';
  return 'neutral';
}

/** URL cell: monospace, ellipsized, full value on hover. */
export function UrlCell({ url, muted = false }: { url: string | null | undefined; muted?: boolean }) {
  if (!url) return <span className="cf-cell-empty">—</span>;
  return (
    <span className={`cf-url-cell${muted ? ' cf-url-muted' : ''}`} title={url}>
      {url}
    </span>
  );
}

/** Boolean verdict cell (indexable, internal, allowed…). */
export function BoolCell({ value, trueLabel = 'Yes', falseLabel = 'No', invert = false }: {
  value: boolean | null | undefined;
  trueLabel?: string;
  falseLabel?: string;
  invert?: boolean;
}) {
  if (value === null || value === undefined) return <span className="cf-cell-empty">—</span>;
  const good = invert ? !value : value;
  return <Badge tone={good ? 'success' : 'danger'}>{value ? trueLabel : falseLabel}</Badge>;
}

/** Tri-state boolean (robots/llms governance: true/false/unknown). */
export function TriStateCell({ value }: { value: boolean | null | undefined }) {
  if (value === null || value === undefined) return <Badge tone="neutral">n/a</Badge>;
  return <Badge tone={value ? 'success' : 'danger'}>{value ? 'allow' : 'deny'}</Badge>;
}

/** Numeric cell with tabular figures. */
export function NumberCell({ value, digits = 0 }: { value: number | null | undefined; digits?: number }) {
  if (value === null || value === undefined) return <span className="cf-cell-empty">—</span>;
  return <span className="cf-number-cell">{value.toLocaleString(undefined, { maximumFractionDigits: digits, minimumFractionDigits: digits })}</span>;
}

/** Ratio in [0,1] rendered as a percent with a micro-bar. */
export function PercentCell({ value, tone = 'ember' }: { value: number | null | undefined; tone?: BadgeTone }) {
  if (value === null || value === undefined) return <span className="cf-cell-empty">—</span>;
  const pct = Math.max(0, Math.min(1, value)) * 100;
  return (
    <span className="cf-percent-cell" title={`${pct.toFixed(1)}%`}>
      <span className="cf-percent-track">
        <span className={`cf-percent-fill cf-percent-${tone}`} style={{ width: `${pct}%` }} />
      </span>
      <span className="cf-percent-label">{pct.toFixed(1)}%</span>
    </span>
  );
}

/** Confidence meter in [0,1] (WAF detection, technology confidence). */
export function ConfidenceCell({ value }: { value: number | null | undefined }) {
  if (value === null || value === undefined) return <span className="cf-cell-empty">—</span>;
  const tone: BadgeTone = value >= 0.8 ? 'success' : value >= 0.5 ? 'warning' : 'danger';
  return <PercentCell value={value} tone={tone} />;
}

/** ISO timestamp → compact local display. */
export function TimestampCell({ value }: { value: string | null | undefined }) {
  if (!value) return <span className="cf-cell-empty">—</span>;
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return <span className="cf-cell-empty">—</span>;
  return (
    <span className="cf-timestamp-cell" title={value}>
      {date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })}{' '}
      {date.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })}
    </span>
  );
}

/**
 * Dynamic extraction/snippet value cell. Multi-valued cells expand to the
 * busiest page in the grid (FS-CRAWL-002 §8.3): arrays render as a compact
 * count + first values, with the full list on hover.
 */
export function ExtractionValueCell({ value }: { value: ExtractionValue | undefined }) {
  if (value === undefined || value === null) return <span className="cf-cell-empty">—</span>;
  // typeof guard narrows readonly arrays reliably (Array.isArray does not
  // exclude readonly array members of the union).
  if (typeof value === 'object') {
    const list = value as readonly string[];
    if (list.length === 0) return <span className="cf-cell-empty">—</span>;
    const preview = list.slice(0, 2).join(', ');
    return (
      <span className="cf-multivalue-cell" title={list.join('\n')}>
        <Badge tone="info">{list.length}×</Badge>
        <span className="cf-multivalue-preview">{preview}{list.length > 2 ? '…' : ''}</span>
      </span>
    );
  }
  if (typeof value === 'boolean') return <BoolCell value={value} trueLabel="true" falseLabel="false" />;
  if (typeof value === 'number') return <NumberCell value={value} digits={Number.isInteger(value) ? 0 : 2} />;
  return <span className="cf-text-cell" title={value}>{value}</span>;
}

/** Truncated plain text with hover title. */
export function TextCell({ value, mono = false }: { value: string | null | undefined; mono?: boolean }) {
  if (value === null || value === undefined || value === '') return <span className="cf-cell-empty">—</span>;
  return <span className={mono ? 'cf-text-cell cf-mono' : 'cf-text-cell'} title={value}>{value}</span>;
}

/** Hop-chain cell: redirect chain rendered as status-coded arrows. */
export function HopChainCell({ hops }: { hops: readonly { readonly url: string; readonly statusCode: number }[] }) {
  if (hops.length === 0) return <span className="cf-cell-empty">direct</span>;
  return (
    <span className="cf-hopchain-cell" title={hops.map((hop) => `${hop.statusCode} ${hop.url}`).join('\n')}>
      {hops.map((hop, index) => (
        <span key={index} className={`cf-hop cf-hop-${Math.floor(hop.statusCode / 100)}xx`}>
          {hop.statusCode}
          {index < hops.length - 1 ? '→' : ''}
        </span>
      ))}
    </span>
  );
}
