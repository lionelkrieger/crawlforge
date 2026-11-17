/**
 * Standard selector surface (contracts/store/selectors). Every selector is
 * a pure, side-effect-free projection returning bounded data — never a full
 * dataset (FS-CRAWL-002 §7.4/§8.8).
 *
 * @module store/selectors
 */

import type { AppSettings, SystemHealthSnapshot } from '../contracts/api/system';
import type { ProjectSummary } from '../contracts/api/projects';
import type { AuditSummary, Finding, LinkEdge, UrlRecordSummary } from '../contracts/api/audits';
import type { NgramRow } from '../contracts/api/ngrams';
import type { HostState } from '../contracts/api/hosts';
import type { PluginRegistryEntry } from '../contracts/api/plugins';
import type { CrawlForgeSelectorContracts, Selector } from '../contracts/store/selectors';
import type { WindowedPage } from '../contracts/store/state';
import { tableToList } from './entityTable';

/** Full settings object or null before first load. */
export const selectSettings: Selector<AppSettings | null> = (state) => state.settings.settings;

/** Project summaries in list order (bounded window). */
export const selectProjects: Selector<readonly ProjectSummary[]> = (state) =>
  state.projects.listPage ? state.projects.listPage.items : tableToList(state.projects.entities);

/** One project by id. */
export function selectProjectById(id: string): Selector<ProjectSummary | undefined> {
  return (state) => state.projects.entities.byId[id];
}

/** Audit summaries in list order (bounded window). */
export const selectAudits: Selector<readonly AuditSummary[]> = (state) =>
  state.audits.listPage ? state.audits.listPage.items : tableToList(state.audits.entities);

/** The active audit summary, when one is selected. */
export const selectActiveAudit: Selector<AuditSummary | null> = (state) =>
  state.audits.activeAuditId !== null
    ? (state.audits.entities.byId[state.audits.activeAuditId] ?? null)
    : null;

/** One audit by id. */
export function selectAuditById(id: string): Selector<AuditSummary | undefined> {
  return (state) => state.audits.entities.byId[id];
}

/** URL window for a query key. */
export function selectUrlPage(key: string): Selector<WindowedPage<UrlRecordSummary> | null> {
  return (state) => state.urlPages.pages[key] ?? null;
}

/** Finding window for a query key. */
export function selectFindingPage(key: string): Selector<WindowedPage<Finding> | null> {
  return (state) => state.findingPages.pages[key] ?? null;
}

/** Link window for a query key. */
export function selectLinkPage(key: string): Selector<WindowedPage<LinkEdge> | null> {
  return (state) => state.linkPages.pages[key] ?? null;
}

/** N-gram window for a query key. */
export function selectNgramPage(key: string): Selector<WindowedPage<NgramRow> | null> {
  return (state) => state.ngramPages.pages[key] ?? null;
}

/** Per-host AIMD state of the active audit (bounded by loaded hosts). */
export const selectHostStates: Selector<readonly HostState[]> = (state) =>
  Object.values(state.hostThrottle.byHostKey);

/** Latest backend heartbeat snapshot. */
export const selectSystemHealth: Selector<SystemHealthSnapshot | null> = (state) =>
  state.systemHealth.latest;

/** Locally measured renderer JS heap (null when unavailable). */
export const selectFrontendJsHeapBytes: Selector<number | null> = (state) =>
  state.systemHealth.frontendJsHeapBytes;

/** Plugin registry entries. */
export const selectPlugins: Selector<readonly PluginRegistryEntry[]> = (state) =>
  tableToList(state.plugins.entities);

/** Hub connection state. */
export const selectConnectionState = (state: { hub: { connectionState: string } }) =>
  state.hub.connectionState;

/** Recent activity entries (bounded ring buffer projection). */
export const selectRecentActivity = (state: {
  hub: { recentActivity: { entries: readonly unknown[] } };
}) => state.hub.recentActivity.entries;

/** The complete locked selector contract object. */
export const selectors: CrawlForgeSelectorContracts = {
  selectSettings,
  selectProjects,
  selectProjectById,
  selectAudits,
  selectActiveAudit,
  selectAuditById,
  selectUrlPage,
  selectFindingPage,
  selectLinkPage,
  selectNgramPage,
  selectHostStates,
  selectSystemHealth,
  selectFrontendJsHeapBytes,
  selectPlugins
};
