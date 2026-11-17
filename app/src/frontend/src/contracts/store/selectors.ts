/**
 * Store API and selector contracts (Zustand-class, structural types only).
 *
 * Spec surface: FS-CRAWL-002 §8.1 — "a single normalized frontend state
 * store (Zustand-class lightweight store) with selector subscriptions; it
 * is the contract surface for plugin lifecycle hooks." §8.8 — selector
 * subscriptions drive rendering; the hub demultiplexer updates slices
 * immutably.
 *
 * The interfaces below are structurally compatible with a Zustand store;
 * no runtime dependency on a store package is introduced by the contracts.
 *
 * @module contracts/store/selectors
 */

import type { AppSettings, SystemHealthSnapshot } from '../api/system';
import type { ProjectSummary } from '../api/projects';
import type { AuditSummary, Finding, LinkEdge, UrlRecordSummary } from '../api/audits';
import type { NgramRow } from '../api/ngrams';
import type { HostState } from '../api/hosts';
import type { PluginRegistryEntry } from '../api/plugins';
import type { CrawlForgeState, WindowedPage } from './state';

/** A pure projection of the store state. Selectors must be side-effect free. */
export type Selector<T> = (state: CrawlForgeState) => T;

/** Equality function controlling selector re-render notifications. */
export type EqualityFn<T> = (a: T, b: T) => boolean;

/**
 * Structural store API (Zustand-class). `setState` accepts a partial slice
 * update or an updater function; updates are immutable — the demultiplexer
 * and API loaders never mutate existing state objects.
 */
export interface StoreApi<TState> {
  getState(): TState;
  getInitialState(): TState;
  setState(
    partial:
      | TState
      | Partial<TState>
      | ((state: TState) => TState | Partial<TState>),
    replace?: boolean
  ): void;
  subscribe(listener: (state: TState, previousState: TState) => void): () => void;
}

/** The normalized CrawlForge store API. */
export type CrawlForgeStore = StoreApi<CrawlForgeState>;

/**
 * Bound-hook contract: calling the hook with a selector subscribes the
 * component to that projection only (selector subscriptions, §8.8).
 */
export interface CrawlForgeStoreHook extends CrawlForgeStore {
  <T>(selector: Selector<T>, equalityFn?: EqualityFn<T>): T;
}

/**
 * Standard selector surface implemented by the store workpiece. Every
 * selector returns a bounded projection — never a full dataset (§7.4).
 */
export interface CrawlForgeSelectorContracts {
  /** Full settings object or null before first load. */
  selectSettings: Selector<AppSettings | null>;
  /** Project summaries in list order (bounded window). */
  selectProjects: Selector<readonly ProjectSummary[]>;
  /** One project by id. */
  selectProjectById(id: string): Selector<ProjectSummary | undefined>;
  /** Audit summaries in list order (bounded window). */
  selectAudits: Selector<readonly AuditSummary[]>;
  /** The active audit summary, when one is selected. */
  selectActiveAudit: Selector<AuditSummary | null>;
  /** One audit by id. */
  selectAuditById(id: string): Selector<AuditSummary | undefined>;
  /** URL window for a query key. */
  selectUrlPage(key: string): Selector<WindowedPage<UrlRecordSummary> | null>;
  /** Finding window for a query key. */
  selectFindingPage(key: string): Selector<WindowedPage<Finding> | null>;
  /** Link window for a query key. */
  selectLinkPage(key: string): Selector<WindowedPage<LinkEdge> | null>;
  /** N-gram window for a query key. */
  selectNgramPage(key: string): Selector<WindowedPage<NgramRow> | null>;
  /** Per-host AIMD state of the active audit. */
  selectHostStates: Selector<readonly HostState[]>;
  /** Latest backend heartbeat snapshot. */
  selectSystemHealth: Selector<SystemHealthSnapshot | null>;
  /** Locally measured renderer JS heap (null when unavailable). */
  selectFrontendJsHeapBytes: Selector<number | null>;
  /** Plugin registry entries. */
  selectPlugins: Selector<readonly PluginRegistryEntry[]>;
}
