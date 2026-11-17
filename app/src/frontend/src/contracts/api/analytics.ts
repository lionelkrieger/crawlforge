/**
 * MarTech analytics DTOs (additive, FS-CRAWL-002 §14.5).
 *
 * Routes: GET /api/audits/{id}/analytics (tag inventory / consent / findings
 * views), GET /api/audits/{id}/analytics/coverage (per-container coverage
 * across indexable pages).
 *
 * Detections ride the existing findings-updated / url-batch hub events —
 * no new hub events for analytics (FS-CRAWL-002 Appendix C).
 *
 * @module contracts/api/analytics
 */

import type { EntityId } from '../common/primitives';
import type { ListQuery } from '../common/pagination';
import type { Finding } from './audits';

/** Analytics view selector. */
export type AnalyticsView = 'tags' | 'consent' | 'findings';

/** One detected tag/container instance. */
export interface AnalyticsTag {
  readonly name: string;
  readonly containerId: string | null;
  /** Tag kind key (e.g. container, pixel, dataLayer push). */
  readonly kind: string;
  readonly urlId: EntityId | null;
  readonly occurrences: number;
}

/** Consent Mode v2 / beacon rollup across the audited pages. */
export interface AnalyticsConsentSummary {
  readonly consentModeV2Present: boolean;
  readonly beaconPresent: boolean;
  readonly pagesWithConsent: number;
  readonly pagesWithoutConsent: number;
}

/** GET /api/audits/{id}/analytics — view + container filters. */
export interface AnalyticsQuery extends ListQuery {
  readonly view?: AnalyticsView;
  readonly containerId?: string;
}

/** Tag inventory, consent rollup, and analytics findings. */
export interface AnalyticsResponse {
  readonly tags: readonly AnalyticsTag[];
  readonly consent: AnalyticsConsentSummary;
  readonly findings: readonly Finding[];
}

/** Per-container coverage across indexable pages. */
export interface ContainerCoverage {
  readonly containerId: string;
  readonly containerName: string | null;
  readonly indexablePages: number;
  readonly pagesWithContainer: number;
  /** Coverage ratio in [0, 1]. */
  readonly coverage: number;
}

/** GET /api/audits/{id}/analytics/coverage — container filter, pagination. */
export interface AnalyticsCoverageQuery extends ListQuery {
  readonly containerId?: string;
}
