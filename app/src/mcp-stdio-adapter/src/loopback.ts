/**
 * Loopback-only endpoint enforcement for the stdio adapter (FS-CRAWL-001 §3
 * row 4, §11.2). Mirrors the backend's LoopbackEndpointValidator: only
 * 127.0.0.0/8, ::1, and localhost are accepted; no DNS resolution is ever
 * performed, so a hostname cannot smuggle a non-loopback address past the
 * check. The adapter opens no non-loopback sockets.
 */

const IPV4_PATTERN = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/;

/** Whether a hostname is provably loopback without DNS. */
export function isLoopbackHostname(hostname: string): boolean {
  const host = hostname.replace(/^\[|\]$/g, "").toLowerCase();
  if (host === "localhost" || host === "::1") {
    return true;
  }

  const match = IPV4_PATTERN.exec(host);
  if (!match) {
    return false;
  }

  const octets = match.slice(1).map(Number);
  return octets.every((octet) => octet >= 0 && octet <= 255) && octets[0] === 127;
}

/** Throws unless the URL is an http(s) loopback endpoint. */
export function assertLoopbackUrl(url: URL): void {
  if (url.protocol !== "http:" && url.protocol !== "https:") {
    throw new Error(`Unsupported MCP endpoint scheme '${url.protocol}'; only http/https loopback endpoints are allowed.`);
  }

  if (!isLoopbackHostname(url.hostname)) {
    throw new Error(
      `MCP endpoint '${url.hostname}' is not loopback. The stdio adapter bridges only to 127.0.0.0/8, ::1, or localhost (network boundary E7/§11.2).`,
    );
  }
}
