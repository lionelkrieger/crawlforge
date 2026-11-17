// Offline CIDR verification against bundled ranges only.
export function matchesBundledCidr(ip: string, ranges: string[]): boolean {
  return ranges.some((range) => ip.startsWith(range.split('/')[0].split('.').slice(0, 2).join('.')));
}
