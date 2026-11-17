/**
 * Local UI plugin system barrel (FS-CRAWL-002 §6).
 *
 * Local-only, sandboxed plugin discovery, validation, registry, lifecycle,
 * and management. No remote channel (E1), no backend mutation rights (E5),
 * no licensing or gating (E6).
 *
 * @module plugins
 */

export {
  PLUGIN_MANIFEST_SCHEMA,
  MANIFEST_HOOK_NAMES,
  type PluginNetworkPermission,
  type PluginTabContribution,
  type PluginColumnFormatterContribution,
  type PluginFilterPresetContribution,
  type PluginContributions,
  type PluginEngineRequirement,
  type SpecPluginManifest
} from './manifestSchema';

export {
  validatePluginManifest,
  isLocalRelativePath,
  compareSemver,
  checkCompatibility,
  toBridgeManifest,
  type ManifestValidation,
  type ManifestValidationIssue,
  type CompatibilityResult
} from './manifestValidator';

export {
  isNetworkAllowed,
  grantedConnectOrigins,
  NO_NETWORK_GRANT,
  type NetworkGrant,
  type DestinationRule,
  type NetworkDecision
} from './networkGrantGuard';

export {
  canonicalJson,
  sha256Hex,
  computeIntegrityHashes,
  compareIntegrity,
  UNKNOWN_INTEGRITY,
  type IntegrityHashes,
  type IntegrityStatus,
  type IntegrityRecord
} from './integrity';

export {
  SANDBOX_PROTOCOL,
  READ_CAPABILITIES,
  UI_CAPABILITIES,
  NETWORK_CAPABILITY,
  KNOWN_CAPABILITIES,
  isKnownCapability,
  capabilityPermission,
  isFrameToHostMessage,
  type HostToFrameMessage,
  type FrameToHostMessage
} from './sandboxProtocol';

export {
  PluginRegistry,
  type PluginCandidate,
  type PluginRegistryDelegate,
  type PluginSource,
  type UiPluginEntry,
  type PluginRegistrySnapshot,
  type PluginRegistryOptions,
  type RegistryOperationResult
} from './pluginRegistry';

export {
  SandboxBridgeHost,
  type SandboxBridgeOptions,
  type PluginContributionEvent
} from './sandboxBridge';

export {
  SANDBOX_ATTRIBUTE_VALUE,
  buildCsp,
  buildSrcDoc,
  createSandboxFrame,
  type SandboxFrameOptions
} from './sandboxFrame';

export { PluginManager, type PluginManagerOptions } from './pluginManager';

export {
  PluginManagementSurface,
  type PluginManagementSurfaceProps
} from './PluginManagementSurface';