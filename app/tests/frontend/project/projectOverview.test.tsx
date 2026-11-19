// @vitest-environment jsdom
/**
 * ProjectOverview component tests (WP-09-07, UI-02): root testid, the
 * Configurations/Integrations section markers, navigation affordances, audit
 * list with status + start-audit action, and the storage/retention summary.
 */

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ProjectOverview, type OverviewSection } from '../../../src/frontend/src/surfaces/project/ProjectOverview';
import {
  auditSummaryFixture,
  byTestId,
  click,
  configurationSummaryFixture,
  flush,
  projectSummaryFixture,
  renderProject,
  selectOption,
  textOf,
  type Mounted
} from './helpers';

let mounted: Mounted | null = null;

beforeEach(() => {
  document.body.replaceChildren();
});

afterEach(() => {
  mounted?.unmount();
  mounted = null;
});

const configurations = [configurationSummaryFixture('7', 'Full crawl'), configurationSummaryFixture('8', 'Quick sample')];
const audits = [auditSummaryFixture('11', 'completed'), auditSummaryFixture('12', 'running')];

async function renderOverview(overrides: Partial<Parameters<typeof ProjectOverview>[0]> = {}) {
  const navigated: OverviewSection[] = [];
  const started: string[] = [];
  const opened: string[] = [];
  mounted = await renderProject(
    <ProjectOverview
      project={projectSummaryFixture}
      audits={audits}
      configurations={configurations}
      storage={{ databaseLocation: 'C:\\Data\\crawlforge.db', contentStoreLocation: 'beside-database', compressionEnabled: true }}
      retention={{ keepLastAudits: 20, keepDays: null }}
      onNavigate={(section) => navigated.push(section)}
      onStartAudit={(configurationId) => started.push(configurationId)}
      onOpenAudit={(auditId) => opened.push(auditId)}
      {...overrides}
    />
  );
  await flush();
  return { container: mounted.container, navigated, started, opened };
}

describe('walker UI-02 contract', () => {
  it('renders the project-overview root with Configurations AND Integrations markers', async () => {
    const { container } = await renderOverview();
    expect(byTestId(container, 'project-overview')).not.toBeNull();
    expect(textOf(byTestId(container, 'overview-nav-configurations'))).toBe('Configurations');
    expect(textOf(byTestId(container, 'overview-nav-integrations'))).toBe('Integrations');
    expect(container.textContent).toContain('Configurations');
    expect(container.textContent).toContain('Integrations');
  });

  it('exposes Segments/Audits/Logs navigation affordances', async () => {
    const { container, navigated } = await renderOverview();
    for (const section of ['segments', 'audits', 'logs'] as const) {
      expect(byTestId(container, `overview-nav-${section}`)).not.toBeNull();
    }
    click(byTestId(container, 'overview-nav-configurations')!);
    click(byTestId(container, 'overview-nav-integrations')!);
    click(byTestId(container, 'overview-nav-segments')!);
    click(byTestId(container, 'overview-nav-audits')!);
    click(byTestId(container, 'overview-nav-logs')!);
    await flush();
    expect(navigated).toEqual(['configurations', 'integrations', 'segments', 'audits', 'logs']);
  });
});

describe('audit list + start action', () => {
  it('lists audits with status badges and open actions', async () => {
    const { container, opened } = await renderOverview();
    expect(byTestId(container, 'overview-audit-row-11')).not.toBeNull();
    expect(textOf(byTestId(container, 'overview-audit-state-11'))).toBe('completed');
    expect(textOf(byTestId(container, 'overview-audit-state-12'))).toBe('running');
    click(byTestId(container, 'overview-open-audit-12')!);
    await flush();
    expect(opened).toEqual(['12']);
  });

  it('starts an audit with the selected configuration', async () => {
    const { container, started } = await renderOverview();
    // Defaults to the first configuration.
    click(byTestId(container, 'overview-start-audit')!);
    await flush();
    expect(started).toEqual(['7']);
    await selectOption(container, 'overview-start-configuration', 'Quick sample');
    click(byTestId(container, 'overview-start-audit')!);
    await flush();
    expect(started).toEqual(['7', '8']);
  });

  it('disables the start action without configurations', async () => {
    const { container } = await renderOverview({ configurations: [], audits: [] });
    expect((byTestId(container, 'overview-start-audit') as HTMLButtonElement).disabled).toBe(true);
    expect(byTestId(container, 'overview-audits-empty')).not.toBeNull();
  });

  it('shows a start error when provided', async () => {
    const { container } = await renderOverview({ startError: 'Configuration has no seeds.' });
    expect(textOf(byTestId(container, 'overview-start-error'))).toContain('Configuration has no seeds.');
  });
});

describe('storage, retention and integrations', () => {
  it('summarizes storage and the retention policy', async () => {
    const { container } = await renderOverview();
    expect(textOf(byTestId(container, 'overview-storage-summary'))).toContain('C:\\Data\\crawlforge.db');
    expect(textOf(byTestId(container, 'overview-retention-policy'))).toContain('Retention policy');
    expect(textOf(byTestId(container, 'overview-retention-policy'))).toContain('last 20 audits');
  });

  it('shows the keep-all default when no pruning is configured', async () => {
    const { container } = await renderOverview({ retention: null });
    expect(textOf(byTestId(container, 'overview-retention-policy'))).toContain('keep all audits');
  });

  it('integrations connect actions are disabled with a phase-14 note', async () => {
    const { container } = await renderOverview();
    expect((byTestId(container, 'overview-connect-gsc') as HTMLButtonElement).disabled).toBe(true);
    expect((byTestId(container, 'overview-connect-ga4') as HTMLButtonElement).disabled).toBe(true);
    expect(textOf(byTestId(container, 'overview-integrations-note'))).toContain('engine lands in phase 14');
  });

  it('lists configuration names in the Configurations section', async () => {
    const { container } = await renderOverview();
    expect(textOf(byTestId(container, 'overview-configuration-7'))).toBe('Full crawl');
    expect(textOf(byTestId(container, 'overview-configuration-8'))).toBe('Quick sample');
  });
});
