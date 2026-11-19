/**
 * Ctrl+K Spotlight command palette (FS-CRAWL-002 §8.2). Navigation, audits,
 * tools, and settings actions with local fuzzy search — no remote content.
 * Built on the mandated @mantine/spotlight component.
 *
 * @module shell/CommandPalette
 */

import { useMemo } from 'react';
import { Spotlight, spotlight, type SpotlightActionData } from '@mantine/spotlight';
import { Group, Kbd } from '@mantine/core';
import { useShell, useStoreSelector } from './ShellContext';
import { selectAudits, selectProjects } from '../store/selectors';
import {
  IconDashboard,
  IconSpider,
  IconChart,
  IconTools,
  IconSettings,
  IconFolder,
  IconSearch,
  IconRefresh,
  IconExport
} from '../theme/icons';
import type { ShellView } from './windowState';

/** Palette trigger button (header). */
export function PaletteTrigger() {
  return (
    <button
      type="button"
      className="cf-palette-trigger"
      onClick={() => spotlight.open()}
      data-testid="palette-trigger"
      aria-label="Open command palette (Ctrl+K)"
    >
      <IconSearch size={14} />
      <span>Search commands…</span>
      <span className="cf-kbd">Ctrl K</span>
    </button>
  );
}

/** Builds the action list from live store projections. */
export function usePaletteActions(): SpotlightActionData[] {
  const { navigation, services } = useShell();
  const audits = useStoreSelector(selectAudits);
  const projects = useStoreSelector(selectProjects);

  return useMemo(() => {
    const nav = (view: ShellView, label: string, icon: React.ReactNode, keywords: string): SpotlightActionData => ({
      id: `nav-${view}`,
      label,
      description: 'Navigation',
      group: 'Navigate',
      leftSection: icon,
      keywords,
      onClick: () => navigation.setView(view)
    });

    const actions: SpotlightActionData[] = [
      nav('hub', 'Main hub — projects & dashboards', <IconDashboard size={16} />, 'home dashboard projects'),
      nav('audits', 'Audits — crawl runs', <IconSpider size={16} />, 'crawl runs audits'),
      nav('report', 'Report — active audit', <IconChart size={16} />, 'report findings urls'),
      nav('tools', 'Standalone tools', <IconTools size={16} />, 'tools single-page analyzer scope rewrite llms'),
      nav('settings', 'Settings', <IconSettings size={16} />, 'settings database proxy mcp theme language diagnostics')
    ];

    for (const project of projects.slice(0, 20)) {
      actions.push({
        id: `project-${project.id}`,
        label: project.name,
        description: `Project · ${project.description ?? project.scopeSummary ?? 'local project'}`,
        group: 'Projects',
        leftSection: <IconFolder size={16} />,
        keywords: `project ${project.name} ${project.tags.join(' ')}`,
        // WP-09-11 (UI-02): picking a project opens its overview, not the hub.
        onClick: () => navigation.openProject(project.id)
      });
    }

    for (const audit of audits.slice(0, 20)) {
      actions.push({
        id: `audit-${audit.id}`,
        label: `Audit ${audit.id} — ${audit.state}`,
        description: `Phase ${audit.phase} · ${audit.counters.fetched} fetched`,
        group: 'Audits',
        leftSection: <IconSpider size={16} />,
        keywords: `audit ${audit.id} ${audit.state} ${audit.phase}`,
        onClick: () => navigation.openAudit(audit.id)
      });
    }

    actions.push(
      {
        id: 'tool-refresh',
        label: 'Refresh local aggregates',
        description: 'Reload projects, audits, and plugins from the local API',
        group: 'Tools',
        leftSection: <IconRefresh size={16} />,
        keywords: 'refresh reload sync',
        onClick: () => {
          void services.actions.loadProjects();
          void services.actions.loadAudits();
          void services.actions.loadPlugins();
        }
      },
      {
        id: 'tool-resubscribe',
        label: 'Re-subscribe active audit channel',
        description: 'Re-join the per-audit hub channel',
        group: 'Tools',
        leftSection: <IconExport size={16} />,
        keywords: 'subscribe hub channel realtime',
        onClick: () => {
          const active = services.store.getState().audits.activeAuditId;
          if (active && services.hub) {
            void services.hub.subscribe([active]).catch(() => undefined);
          }
        }
      }
    );

    return actions;
  }, [navigation, services, audits, projects]);
}

/** The Spotlight palette surface (mount once inside MantineProvider). */
export function CommandPalette() {
  const actions = usePaletteActions();
  return (
    <Spotlight
      actions={actions}
      nothingFound="No matching commands…"
      highlightQuery
      searchProps={{
        leftSection: <IconSearch size={16} />,
        placeholder: 'Search navigation, audits, tools, settings…'
      }}
      shortcut={['mod + K', 'mod + P']}
      data-testid="cf-palette"
    />
  );
}

/** Header hint showing the palette shortcut. */
export function PaletteHint() {
  return (
    <Group gap={4} wrap="nowrap">
      <Kbd size="xs">Ctrl</Kbd>
      <Kbd size="xs">K</Kbd>
    </Group>
  );
}
