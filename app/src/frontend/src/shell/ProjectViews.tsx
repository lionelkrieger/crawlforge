/**
 * Project-scoped shell views (WP-09-11 serial wiring): the project overview
 * (UI-02, opened by a hub project-row click) and the configurations list
 * (UI-03). Both are thin wrappers binding the accepted props-driven surfaces
 * to the shell store/navigation — the surfaces themselves stay shell-free.
 *
 * @module shell/ProjectViews
 */

import { useCallback, useEffect, useState } from 'react';
import { Button, Group, Stack, Text, Title } from '@mantine/core';
import { useShell, useStoreSelector } from './ShellContext';
import { selectAudits, selectProjects } from '../store/selectors';
import { ProjectOverview, type OverviewSection } from '../surfaces/project/ProjectOverview';
import { ConfigurationsList } from '../surfaces/project/ConfigurationsList';
import type { ConfigurationSummary } from '../contracts/api/projects';

/** Loads the selected project's configuration summaries (local REST read). */
function useProjectConfigurations(projectId: string | null): readonly ConfigurationSummary[] {
  const { services } = useShell();
  const [configurations, setConfigurations] = useState<readonly ConfigurationSummary[]>([]);
  useEffect(() => {
    let cancelled = false;
    if (!projectId || !services.api) {
      setConfigurations([]);
      return;
    }
    void services.api
      .request('configurations.list', { params: { id: projectId }, query: { offset: 0, limit: 200 } })
      .then((envelope) => {
        if (!cancelled && !envelope.error && envelope.data) setConfigurations(envelope.data);
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [services, projectId]);
  return configurations;
}

/** Back-to-hub affordance shared by the project-scoped views. */
function BackToHub() {
  const { navigation } = useShell();
  return (
    <Button variant="subtle" color="steel" size="compact-sm" onClick={() => navigation.setView('hub')} data-testid="back-to-hub">
      ← Main hub
    </Button>
  );
}

/** The project overview view (UI-02). */
export function ProjectView() {
  const { services, navigation } = useShell();
  const projects = useStoreSelector(selectProjects);
  const audits = useStoreSelector(selectAudits);
  const projectId = navigation.selectedProjectId;
  const project = projects.find((candidate) => candidate.id === projectId) ?? null;
  const configurations = useProjectConfigurations(projectId);
  const [starting, setStarting] = useState(false);
  const [startError, setStartError] = useState<string | null>(null);

  const startAudit = useCallback(
    async (configurationId: string) => {
      if (!services.api) {
        setStartError('Backend disconnected.');
        return;
      }
      setStarting(true);
      setStartError(null);
      try {
        const started = await services.api.request('audits.start', { body: { configurationId } });
        if (started.error || !started.data) {
          setStartError(started.error?.message ?? 'Audit start failed.');
          return;
        }
        await services.actions.loadAudits();
        navigation.openAudit(started.data.auditId);
      } catch {
        setStartError('Transport failure — is the backend running?');
      } finally {
        setStarting(false);
      }
    },
    [services, navigation]
  );

  const navigateSection = useCallback(
    (section: OverviewSection) => {
      if (!projectId) return;
      if (section === 'configurations') navigation.openConfigurations(projectId);
      else if (section === 'audits') navigation.setView('audits');
      else navigation.setView('tools');
    },
    [navigation, projectId]
  );

  if (!project) {
    return (
      <Stack gap="sm" data-testid="project-view">
        <BackToHub />
        <Text size="sm" c="dimmed">
          {projectId ? 'Project not found — it may have been deleted.' : 'No project selected.'}
        </Text>
      </Stack>
    );
  }

  return (
    <Stack gap="sm" data-testid="project-view">
      <Group justify="space-between">
        <BackToHub />
        <Title order={4}>{project.name}</Title>
      </Group>
      <ProjectOverview
        project={project}
        audits={audits.filter((audit) => audit.projectId === project.id)}
        configurations={configurations}
        onNavigate={navigateSection}
        onStartAudit={(configurationId) => void startAudit(configurationId)}
        onOpenAudit={navigation.openAudit}
        startingAudit={starting}
        startError={startError}
      />
    </Stack>
  );
}

/** The project configurations view (UI-03). */
export function ConfigurationsView() {
  const { services, navigation } = useShell();
  const projectId = navigation.selectedProjectId;

  if (!projectId) {
    return (
      <Stack gap="sm" data-testid="configurations-view">
        <BackToHub />
        <Text size="sm" c="dimmed">No project selected.</Text>
      </Stack>
    );
  }

  return (
    <Stack gap="sm" data-testid="configurations-view">
      <BackToHub />
      <ConfigurationsList projectId={projectId} api={services.api} />
    </Stack>
  );
}