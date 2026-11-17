// User-configured destination credentials are the user's own (permitted).
export interface DestinationCredentials {
  username: string;
  password: string;
}

export const localToggles: Record<string, boolean> = { rendering: true };
