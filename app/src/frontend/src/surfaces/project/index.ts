/**
 * Project surfaces barrel (WP-09-07): UI-02 project overview, UI-03
 * configurations list, UI-04 configuration editor. The lead mounts these
 * components (shell is lead-owned); see WP-09-07-HANDOFF.md.
 *
 * @module surfaces/project
 */

export { ProjectOverview } from './ProjectOverview';
export type { ProjectOverviewProps, OverviewSection, OverviewStorage, OverviewRetention } from './ProjectOverview';

export { ConfigurationsList } from './ConfigurationsList';
export type { ConfigurationsListProps } from './ConfigurationsList';

export { ConfigurationEditor } from './ConfigurationEditor';
export type { ConfigurationEditorProps } from './ConfigurationEditor';

export {
  ANALYSIS_CATEGORIES,
  CONFIGURATION_TABS,
  CRAWL_AS_PRESETS,
  DEFAULT_AIMD,
  DEFAULT_LIMITS,
  DEFAULT_SCOPE_RULES,
  TAB_IDS,
  UA_STRINGS,
  configurationFormErrors,
  configurationFromWire,
  configurationToCreateRequest,
  configurationToUpdateRequest,
  defaultConfigurationForm,
  presetFromUserAgent,
  toggleCategory,
  userAgentForPreset,
  validateAimd,
  validateLimit
} from './configurationModel';
export type {
  CanonicalizationRules,
  ConfigurationFormValue,
  ConfigurationTab,
  ConfigurationWireDto,
  CookieJarRules,
  CrawlAsPreset,
  DirectiveRules,
  ResourceRules,
  ScopeRules,
  ScopeRulesDocument,
  UserAgentComparisonRules,
  UserAgentRules
} from './configurationModel';
