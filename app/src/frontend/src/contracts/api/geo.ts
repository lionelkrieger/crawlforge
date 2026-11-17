/**
 * GEO / AI-bot governance DTOs (additive, FS-CRAWL-002 §13.5).
 *
 * Routes: GET /api/audits/{id}/geo/llms-txt, GET /api/audits/{id}/geo/governance.
 * (Generation/validation tools live in contracts/api/tools.)
 *
 * Clean-room (E9): no remote GEO/llms.txt registries or publishing; the
 * governance matrix is computed from bundled agent profiles plus locally
 * observed robots.txt / llms.txt content.
 *
 * @module contracts/api/geo
 */

import type { IsoTimestamp } from '../common/primitives';
import type { ListQuery } from '../common/pagination';

/** Per-host llms.txt validation result. */
export interface LlmsTxtValidation {
  readonly host: string;
  readonly url: string;
  readonly present: boolean;
  readonly valid: boolean;
  readonly issues: readonly string[];
  readonly checkedAt: IsoTimestamp;
}

/** GET /api/audits/{id}/geo/llms-txt — host filter, pagination. */
export interface LlmsTxtListQuery extends ListQuery {
  readonly host?: string;
}

/**
 * One bot-governance matrix row: an AI agent profile's effective access
 * verdict for a host, derived from robots.txt and llms.txt evidence.
 */
export interface BotGovernanceRow {
  /** Bundled AI agent profile name. */
  readonly botName: string;
  readonly host: string | null;
  readonly allowed: boolean;
  readonly robotsTxtAllowed: boolean | null;
  readonly llmsTxtAllowed: boolean | null;
  readonly notes: string | null;
}

/** GET /api/audits/{id}/geo/governance — bot/host filters, pagination. */
export interface BotGovernanceQuery extends ListQuery {
  readonly botName?: string;
  readonly host?: string;
}
