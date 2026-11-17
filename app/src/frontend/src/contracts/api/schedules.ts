/**
 * Scheduled audit DTOs.
 *
 * Routes: GET/POST /api/projects/{id}/schedules, GET/PUT/DELETE /api/schedules/{id},
 * POST /api/schedules/{id}/enable|disable|run-now, GET /api/schedules/{id}/runs.
 *
 * Schedule fields compile the §6.1 references present in the REST/hub
 * contracts: configuration reference, recurrence, enablement, next-due time,
 * and completion actions (reported by schedule-run-completed).
 *
 * @module contracts/api/schedules
 */

import type { EntityId, IsoTimestamp, JsonValue } from '../common/primitives';
import type { ListQuery } from '../common/pagination';
import type { ExportFormat } from './exports';

/** Firing trigger types (§10.3: scheduled or catch-up; run-now is manual). */
export type ScheduleTriggerType = 'scheduled' | 'catch-up' | 'manual';

/** Scheduled run outcome. */
export type ScheduleRunOutcome = 'completed' | 'failed' | 'cancelled';

/**
 * One completion action executed after a scheduled run. `kind` is a local
 * action key (e.g. export delivery); parameters are action-specific.
 */
export interface ScheduleCompletionAction {
  readonly kind: string;
  readonly parameters?: Readonly<Record<string, JsonValue>>;
}

/** Result of one completion action (schedule-run-completed payload). */
export interface CompletionActionResult {
  readonly kind: string;
  readonly succeeded: boolean;
  readonly detail: string | null;
}

/** Schedule summary / full record. */
export interface Schedule {
  readonly id: EntityId;
  readonly projectId: EntityId;
  readonly configurationId: EntityId;
  readonly name: string;
  readonly enabled: boolean;
  /** Cron-style recurrence expression (local scheduler). */
  readonly cron: string | null;
  readonly nextDueAt: IsoTimestamp | null;
  readonly lastRunAt: IsoTimestamp | null;
  readonly completionActions: readonly ScheduleCompletionAction[];
  readonly createdAt: IsoTimestamp;
  readonly updatedAt: IsoTimestamp;
}

/** POST /api/projects/{id}/schedules — schedule fields (project from path). */
export interface ScheduleCreateRequest {
  readonly configurationId: EntityId;
  readonly name: string;
  readonly enabled?: boolean;
  readonly cron?: string | null;
  readonly completionActions?: readonly ScheduleCompletionAction[];
}

/** PUT /api/schedules/{id} — any subset of schedule fields. */
export interface ScheduleUpdateRequest {
  readonly configurationId?: EntityId;
  readonly name?: string;
  readonly enabled?: boolean;
  readonly cron?: string | null;
  readonly completionActions?: readonly ScheduleCompletionAction[];
}

/** POST /api/schedules/{id}/enable|disable — new state and next-due time. */
export interface ScheduleStateResponse {
  readonly scheduleId: EntityId;
  readonly enabled: boolean;
  readonly nextDueAt: IsoTimestamp | null;
}

/** POST /api/schedules/{id}/run-now — audit identifier (manual trigger). */
export interface ScheduleRunNowResponse {
  readonly auditId: EntityId;
}

/** One run-history row. */
export interface ScheduleRun {
  readonly id: EntityId;
  readonly scheduleId: EntityId;
  readonly auditId: EntityId;
  readonly triggerType: ScheduleTriggerType;
  readonly startedAt: IsoTimestamp;
  readonly completedAt: IsoTimestamp | null;
  readonly outcome: ScheduleRunOutcome;
  readonly completionActionResults: readonly CompletionActionResult[];
}

/** GET /api/schedules/{id}/runs — pagination. */
export type ScheduleRunListQuery = ListQuery;

/** Export-flavoured completion action parameters (typed convenience). */
export interface ExportCompletionActionParameters {
  readonly format: ExportFormat;
  readonly destinationId?: EntityId | null;
}
