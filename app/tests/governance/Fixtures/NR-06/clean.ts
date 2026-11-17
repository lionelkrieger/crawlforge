// Local structured logging only; no diagnostic data ever leaves the machine.
export interface LocalLogEntry {
  level: string;
  message: string;
  timestampUtc: string;
}

export function logLocal(entry: LocalLogEntry): void {
  console.info(`[${entry.level}] ${entry.message}`);
}
