/**
 * CrawlForge desktop shell root (FS-CRAWL-001 §12, FS-CRAWL-002 §8).
 *
 * Mantine v8 AppShell, dark-first theme, dockable split panes, Ctrl+K
 * Spotlight palette, persistent telemetry status bar, and the bounded
 * activity feed. One hub connection feeds the normalized store; every
 * surface renders from local aggregates only. Mountable root exported for
 * the renderer entry (App.tsx) and for tests.
 *
 * @module shell/CrawlForgeShell
 */

import { useMemo } from 'react';
import { AppShell, MantineProvider, Text, Tooltip } from '@mantine/core';
import '@mantine/core/styles.css';
import '@mantine/spotlight/styles.css';
import '../theme/shell.css';
import { crawlForgeTheme } from '../theme/theme';
import { CrawlForgeMark, IconSplit, IconBell } from '../theme/icons';
import { ShellProvider, useShell, useShellLifecycle, type ShellServices } from './ShellContext';
import { NavigationRail } from './NavigationRail';
import { StatusBar } from './StatusBar';
import { SplitPanes } from './SplitPanes';
import { CommandPalette, PaletteTrigger } from './CommandPalette';
import { ActivityFeed } from './ActivityFeed';
import { HubDashboard } from './HubDashboard';
import { AuditsView, ReportView } from './AuditViews';
import { ToolsView } from './ToolsView';
import { SettingsSurface } from './SettingsSurface';
import { useLiveProgress } from '../store/liveProgress';
import type { ShellView } from './windowState';

/** Connection pill (live hub state). */
function ConnectionPill() {
  const { services } = useShell();
  const state = services.hook((s) => s.hub.connectionState);
  const normalized = state.toLowerCase();
  const label =
    state === 'Connected' ? 'Hub connected' : state === 'Reconnecting' ? 'Reconnecting…' : state === 'Connecting' ? 'Connecting…' : 'Offline';
  return (
    <Tooltip label={`SignalR hub: ${state}`} openDelay={300}>
      <span className="cf-connection-pill" data-testid="connection-pill">
        <span className="cf-connection-dot" data-state={normalized} />
        {label}
      </span>
    </Tooltip>
  );
}

/** Backend shutdown notice banner (backend-lifecycle event). */
function LifecycleBanner() {
  const { services } = useShell();
  const lifecycle = useLiveProgress(services.live, (state) => state.backendLifecycle);
  if (lifecycle?.kind !== 'shutdown') return null;
  return (
    <div
      role="alert"
      data-testid="lifecycle-banner"
      style={{
        padding: '6px 16px',
        fontSize: 12,
        background: 'rgba(240, 62, 62, 0.12)',
        borderBottom: '1px solid var(--cf-danger)',
        color: 'var(--cf-danger)',
        display: 'flex',
        alignItems: 'center',
        gap: 8
      }}
    >
      <IconBell size={14} />
      Backend shutting down{lifecycle.message ? `: ${lifecycle.message}` : ''} — the shell will reconnect automatically.
    </div>
  );
}

/** The active view content. */
function ViewContent({ view }: { view: ShellView }) {
  switch (view) {
    case 'hub':
      return <HubDashboard />;
    case 'audits':
      return <AuditsView />;
    case 'report':
      return <ReportView />;
    case 'tools':
      return <ToolsView />;
    case 'settings':
      return <SettingsSurface />;
    default:
      return <HubDashboard />;
  }
}

/** Inner shell (inside providers). */
function ShellFrame() {
  const { services, navigation } = useShell();
  useShellLifecycle(services);

  const settings = services.hook((state) => state.settings.settings);
  const colorScheme = settings?.theme === 'light' ? 'light' : 'dark';

  const secondaryPane = useMemo(
    () => (
      <div className="cf-pane" data-testid="secondary-pane">
        <div className="cf-pane-header">
          <IconSplit size={14} className="cf-pane-icon" />
          Live workspace
        </div>
        <div className="cf-pane-body cf-scroll">
          <ActivityFeed />
        </div>
      </div>
    ),
    []
  );

  return (
    <MantineProvider theme={crawlForgeTheme} defaultColorScheme="dark" forceColorScheme={colorScheme}>
      <AppShell
        header={{ height: 56 }}
        navbar={{ width: 216, breakpoint: 'sm' }}
        footer={{ height: 40 }}
        padding="md"
        data-testid="cf-appshell"
      >
        <AppShell.Header>
          <div className="cf-header">
            <span className="cf-brand">
              <CrawlForgeMark size={24} className="cf-brand-mark" />
              <span className="cf-brand-name">CrawlForge</span>
            </span>
            <span className="cf-header-spacer" />
            <PaletteTrigger />
            <ConnectionPill />
          </div>
          <LifecycleBanner />
        </AppShell.Header>

        <AppShell.Navbar>
          <NavigationRail />
          <div style={{ flex: 1 }} />
          <Text size="xs" c="dimmed" p="md" style={{ opacity: 0.7 }}>
            Local-first · no remote content
          </Text>
        </AppShell.Navbar>

        <AppShell.Main style={{ height: 'calc(100vh - 96px)' }}>
          <SplitPanes
            direction={navigation.layout.direction}
            split={navigation.layout.split}
            collapsed={navigation.layout.collapsed}
            onSplitChange={(split) => navigation.setLayout({ ...navigation.layout, split })}
            onCollapsedChange={(collapsed) => navigation.setLayout({ ...navigation.layout, collapsed })}
            firstLabel="Main workspace"
            secondLabel="Live activity"
            first={
              <div className="cf-pane" data-testid="primary-pane">
                <div className="cf-pane-header">
                  <IconSplit size={14} className="cf-pane-icon" />
                  {navigation.view === 'hub'
                    ? 'Main hub'
                    : navigation.view === 'audits'
                      ? 'Audits'
                      : navigation.view === 'report'
                        ? 'Report'
                        : navigation.view === 'tools'
                          ? 'Tools'
                          : 'Settings'}
                  <span style={{ flex: 1 }} />
                  <Tooltip label={navigation.layout.collapsed ? 'Restore activity pane' : 'Collapse activity pane'} openDelay={300}>
                    <button
                      type="button"
                      className="cf-palette-trigger"
                      style={{ padding: '2px 8px', fontSize: 11 }}
                      onClick={() => navigation.setLayout({ ...navigation.layout, collapsed: !navigation.layout.collapsed })}
                      data-testid="toggle-secondary-pane"
                      aria-label="Toggle activity pane"
                    >
                      <IconSplit size={12} />
                      {navigation.layout.collapsed ? 'Show' : 'Hide'}
                    </button>
                  </Tooltip>
                </div>
                <div className="cf-pane-body cf-scroll" data-testid="view-content">
                  <ViewContent view={navigation.view} />
                </div>
              </div>
            }
            second={secondaryPane}
          />
        </AppShell.Main>

        <AppShell.Footer>
          <StatusBar />
        </AppShell.Footer>
      </AppShell>
      <CommandPalette />
    </MantineProvider>
  );
}

/** Shell root props. */
export interface CrawlForgeShellProps {
  readonly services: ShellServices;
}

/**
 * The mountable CrawlForge shell. Construct services with
 * `createShellServices` (baseUrl from the preload bridge; null = offline).
 */
export function CrawlForgeShell({ services }: CrawlForgeShellProps) {
  return (
    <ShellProvider services={services}>
      <ShellFrame />
    </ShellProvider>
  );
}
