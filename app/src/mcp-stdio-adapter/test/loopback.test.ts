import { describe, expect, it } from "vitest";
import { assertLoopbackUrl, isLoopbackHostname } from "../src/loopback.js";

describe("isLoopbackHostname", () => {
  it.each(["127.0.0.1", "127.1.2.3", "127.255.255.255", "::1", "localhost", "LOCALHOST", "[::1]"])(
    "accepts loopback host %s",
    (host) => {
      expect(isLoopbackHostname(host)).toBe(true);
    },
  );

  it.each([
    "192.168.1.10",
    "10.0.0.5",
    "8.8.8.8",
    "0.0.0.0",
    "169.254.1.1",
    "example.com",
    "2001:db8::1",
    "128.0.0.1",
    "256.1.1.1",
    "",
  ])("rejects non-loopback host %s", (host) => {
    expect(isLoopbackHostname(host)).toBe(false);
  });
});

describe("assertLoopbackUrl", () => {
  it.each(["http://127.0.0.1:8080/mcp", "http://localhost:3000/mcp", "http://[::1]:9000/mcp"])(
    "accepts loopback endpoint %s",
    (endpoint) => {
      expect(() => assertLoopbackUrl(new URL(endpoint))).not.toThrow();
    },
  );

  it.each([
    "http://192.168.1.10:8080/mcp",
    "https://example.com/mcp",
    "http://10.0.0.5/mcp",
    "ftp://127.0.0.1/mcp",
    "ws://localhost/mcp",
  ])("rejects endpoint %s", (endpoint) => {
    expect(() => assertLoopbackUrl(new URL(endpoint))).toThrow();
  });
});
