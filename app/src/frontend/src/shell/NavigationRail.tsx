/**
 * Navigation rail (FS-CRAWL-001 §12 window catalog; §8.2 workspace).
 * Local views only — no entitlement-driven screens exist.
 *
 * @module shell/NavigationRail
 */

import { NavLink, Stack, Text, Tooltip } from '@mantine/core';
import { useShell } from './ShellContext';
import type { ShellView } from './windowState';
import {
  IconDashboard,
  IconSpider,
  IconChart,
  IconTools,
  IconSettings
} from '../theme/icons';

interface RailEntry {
  readonly view: ShellView;
  readonly label: string;
  readonly description: string;
  readonly icon: React.ReactNode;
}

const ENTRIES: readonly RailEntry[] = [
  { view: 'hub', label: 'Main hub', description: 'Projects, dashboards, schedules', icon: <IconDashboard size={17} /> },
  { view: 'audits', label: 'Audits', description: 'Crawl runs and lifecycle', icon: <IconSpider size={17} /> },
  { view: 'report', label: 'Report', description: 'Active audit report surfaces', icon: <IconChart size={17} /> },
  { view: 'tools', label: 'Tools', description: 'Standalone local tools', icon: <IconTools size={17} /> },
  { view: 'settings', label: 'Settings', description: 'Local configuration', icon: <IconSettings size={17} /> }
];

/** The left navigation rail. */
export function NavigationRail() {
  const { navigation } = useShell();
  return (
    <Stack gap={4} p="xs" data-testid="cf-nav-rail">
      <Text size="xs" fw={700} tt="uppercase" lts={1} c="dimmed" px={8} pt={4}>
        Workspace
      </Text>
      {ENTRIES.map((entry) => (
        <Tooltip key={entry.view} label={entry.description} position="right" openDelay={350}>
          <NavLink
            label={entry.label}
            leftSection={entry.icon}
            active={navigation.view === entry.view}
            onClick={() => navigation.setView(entry.view)}
            data-testid={`nav-${entry.view}`}
            color="ember"
            variant="light"
          />
        </Tooltip>
      ))}
    </Stack>
  );
}
