/**
 * Configuration editor (WP-09-07, UI-04) — all 13 tabs, VERBATIM labels.
 *
 * Self-contained and props-driven: the parent owns the form value and the
 * change callback. No shell imports, no data fetching — the lead embeds this
 * component inside the create-project-modal (walker UI-04) and the
 * ConfigurationsList embeds it for open/new configuration (UI-03 → UI-04).
 *
 * The default tab (Scope) renders >3 real inputs; every tab label is always
 * mounted in the tab list (Mantine Tabs), satisfying the walker's tab check
 * regardless of the active panel.
 *
 * @module surfaces/project/ConfigurationEditor
 */

import { useState } from 'react';
import { Stack, Tabs, TextInput } from '@mantine/core';
import type { ConfigurationFormValue } from './configurationModel';
import { CONFIGURATION_TABS, TAB_IDS } from './configurationModel';
import {
  AdvancedPanel,
  AuthPanel,
  CategoriesPanel,
  ClickDiscoveryPanel,
  ConsentPanel,
  CrawlAsPanel,
  DirectivesPanel,
  ExtractionPanel,
  LimitsPanel,
  ResourcesPanel,
  RulesPanel,
  ScopePanel,
  ThresholdsPanel,
  type PanelProps
} from './editorPanels';

/** Configuration editor props. */
export interface ConfigurationEditorProps {
  /** The configuration form value (wire DTO + additive engine documents). */
  readonly value: ConfigurationFormValue;
  /** Whole-value change callback (controlled component). */
  readonly onChange: (next: ConfigurationFormValue) => void;
  /** Owning project, when known (display/context only). */
  readonly projectId?: string;
}

const PANELS: readonly ((props: PanelProps) => React.ReactNode)[] = [
  ScopePanel,
  CrawlAsPanel,
  LimitsPanel,
  RulesPanel,
  DirectivesPanel,
  ResourcesPanel,
  AuthPanel,
  ClickDiscoveryPanel,
  ConsentPanel,
  CategoriesPanel,
  ThresholdsPanel,
  ExtractionPanel,
  AdvancedPanel
];

/** The 13-tab configuration editor. */
export function ConfigurationEditor({ value, onChange, projectId }: ConfigurationEditorProps) {
  const [activeTab, setActiveTab] = useState<string>(TAB_IDS[0]);
  return (
    <Stack gap="sm" data-testid="configuration-editor" data-project-id={projectId ?? undefined}>
      <TextInput
        label="Configuration name"
        value={value.name}
        onChange={(event) => onChange({ ...value, name: event.currentTarget.value })}
        data-testid="config-name"
      />
      <TextInput
        label="Description"
        value={value.description ?? ''}
        onChange={(event) => onChange({ ...value, description: event.currentTarget.value || null })}
        data-testid="config-description"
      />
      <Tabs value={activeTab} onChange={(next) => setActiveTab(next ?? TAB_IDS[0])} data-testid="config-editor-tabs">
        <Tabs.List grow={false}>
          {CONFIGURATION_TABS.map((label, index) => (
            <Tabs.Tab key={TAB_IDS[index]} value={TAB_IDS[index]} data-testid={`config-tab-${TAB_IDS[index]}`}>
              {label}
            </Tabs.Tab>
          ))}
        </Tabs.List>
        {PANELS.map((Panel, index) => (
          <Tabs.Panel key={TAB_IDS[index]} value={TAB_IDS[index]} pt="sm">
            <Panel value={value} onChange={onChange} />
          </Tabs.Panel>
        ))}
      </Tabs>
    </Stack>
  );
}
