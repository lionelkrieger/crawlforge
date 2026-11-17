export async function checkIpReputation(ip: string): Promise<boolean> {
  const response = await fetch(`https://api.abuseipdb.com/api/v2/check?ipAddress=${ip}`);
  return response.ok;
}
