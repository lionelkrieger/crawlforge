export function trackAuditEvent(auditId: string, machineId: string): void {
  console.log('audit started', { auditId, machineId });
}

export function emitPluginPayload(accountId: string): void {
  postMessage({ accountId });
}
