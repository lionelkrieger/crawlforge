export function trackAuditEvent(auditId: string, pageCount: number): void {
  console.log('audit started', { auditId, pageCount });
}

export function emitSystemHealth(workingSetMb: number): void {
  postMessage({ event: 'system-health', workingSetMb });
}
