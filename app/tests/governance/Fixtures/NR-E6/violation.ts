export interface PluginEntitlement {
  pluginId: string;
  pluginLicenseKey: string;
}

export function gatedPlugin(pluginId: string): boolean {
  return false;
}
