// Local MCP token handshake over loopback; no account system exists.
export interface LocalAutomationToken {
  token: string;
  createdUtc: string;
}

export function createLocalToken(): string {
  return crypto.randomUUID();
}
