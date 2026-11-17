import { promises as dns } from 'dns';

// Consented DNS verification only: user-triggered, off by default, locally logged.
export async function verifyBotDns(ip: string, consented: boolean): Promise<string[]> {
  if (!consented) {
    return [];
  }
  return dns.reverse(ip);
}
