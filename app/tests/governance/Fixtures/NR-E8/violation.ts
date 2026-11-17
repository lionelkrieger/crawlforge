export async function enrichLogEntry(ip: string): Promise<string> {
  const response = await fetch(`https://ipinfo.io/${ip}/json`);
  return response.text();
}

export async function fetchBotList(): Promise<string[]> {
  const response = await fetch('https://lists.vendor.example.com/bot-list');
  return response.json();
}
