#!/usr/bin/env node
/**
 * CrawlForge packaged stdio adapter entry point (FS-CRAWL-001 §11.2).
 * An agent spawns this process as a child; it bridges MCP protocol messages
 * between local stdio pipes and the backend's loopback-only endpoint. It
 * holds no domain logic, opens no non-loopback sockets, and terminates when
 * its parent client or the backend exits. No remote or cloud bridging exists.
 *
 * Configuration (local process-spawn trust):
 *   --endpoint <url>  or CF_MCP_ENDPOINT  (loopback only, enforced)
 *   --token <token>   or CF_MCP_TOKEN     (local bearer token)
 */
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { createBackendClient, createStdioServer } from "./bridge.js";
import { assertLoopbackUrl } from "./loopback.js";

function readOption(name: string, envName: string): string | undefined {
  const index = process.argv.indexOf(`--${name}`);
  if (index >= 0 && process.argv[index + 1]) {
    return process.argv[index + 1];
  }
  return process.env[envName];
}

async function main(): Promise<void> {
  const endpoint = readOption("endpoint", "CF_MCP_ENDPOINT");
  const token = readOption("token", "CF_MCP_TOKEN");
  if (!endpoint || !token) {
    process.stderr.write(
      "crawlforge-mcp-stdio: --endpoint/CF_MCP_ENDPOINT and --token/CF_MCP_TOKEN are required.\n",
    );
    process.exit(1);
  }

  // Reject non-loopback endpoints before any socket is opened.
  assertLoopbackUrl(new URL(endpoint));

  const backend = await createBackendClient({ endpoint, token });
  const server = createStdioServer(backend);
  const transport = new StdioServerTransport();

  let shuttingDown = false;
  const shutdown = async (code: number): Promise<void> => {
    if (shuttingDown) {
      return;
    }
    shuttingDown = true;
    try {
      await server.close();
      await backend.close();
    } catch {
      // Best-effort cleanup on exit.
    }
    // Let libuv settle open handles before exiting (Windows teardown).
    setTimeout(() => process.exit(code), 25);
  };

  // Terminate when the parent client closes its pipes or the backend exits.
  process.stdin.on("end", () => void shutdown(0));
  process.stdin.on("close", () => void shutdown(0));
  backend.onclose = () => void shutdown(0);
  server.onclose = () => void shutdown(0);
  process.on("SIGINT", () => void shutdown(0));
  process.on("SIGTERM", () => void shutdown(0));

  await server.connect(transport);
}

main().catch((error: unknown) => {
  process.stderr.write(
    `crawlforge-mcp-stdio: ${error instanceof Error ? error.message : String(error)}\n`,
  );
  process.exit(1);
});
