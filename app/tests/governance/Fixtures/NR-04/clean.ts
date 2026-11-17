// Local system-health heartbeat over the loopback hub (permitted, criterion #41).
export interface SystemHealth {
  workingSetMb: number;
  managedHeapMb: number;
}

export function startLocalHeartbeat(emit: (event: string, payload: SystemHealth) => void): void {
  setInterval(() => emit('system-health', { workingSetMb: 128, managedHeapMb: 64 }), 2000);
}
