export interface PluginHookBus {
  onLoad(pluginId: string): void;
  onTabActivate(auditId: string): void;
  onDataUpdate(payload: string): void;
  onCrawlComplete(auditId: string): void;
}

export function readPluginData(auditId: string): Promise<string> {
  return fetch(`/api/audits/${auditId}/findings`).then((response) => response.text());
}
