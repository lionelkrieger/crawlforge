export function grantPluginWriteAccess(pluginId: string): void {
  // never allowed: plugins get read-only data + UI contribution rights
}

export function onCrawlComplete(auditId: string): void {
  cancelCrawl(auditId);
}

declare function cancelCrawl(auditId: string): void;
