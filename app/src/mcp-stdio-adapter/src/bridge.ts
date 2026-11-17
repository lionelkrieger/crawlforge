/**
 * Thin stdio ↔ loopback bridge (FS-CRAWL-001 §11.2). The adapter holds no
 * domain logic: it forwards MCP list/call/read/get requests to the backend's
 * loopback streamable-HTTP endpoint using the official SDK client, and serves
 * the local agent over stdio using the official SDK server transport. The
 * standard MCP handshake is handled by the SDK on both sides.
 */
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import {
  CallToolRequestSchema,
  GetPromptRequestSchema,
  ListPromptsRequestSchema,
  ListResourcesRequestSchema,
  ListToolsRequestSchema,
  ReadResourceRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import { assertLoopbackUrl } from "./loopback.js";

/** Bridge configuration: the loopback endpoint and local bearer token. */
export interface BridgeOptions {
  endpoint: string;
  token: string;
}

/** Connects an SDK client to the backend loopback endpoint. */
export async function createBackendClient(options: BridgeOptions): Promise<Client> {
  const url = new URL(options.endpoint);
  assertLoopbackUrl(url);

  const transport = new StreamableHTTPClientTransport(url, {
    requestInit: {
      headers: {
        Authorization: `Bearer ${options.token}`,
        // Local process-spawn trust: the adapter marks its traffic so the
        // backend action log records the stdio transport class.
        "X-MCP-Transport": "stdio",
      },
    },
  });

  const client = new Client(
    { name: "crawlforge-stdio-adapter", version: "0.0.1" },
    { capabilities: {} },
  );
  await client.connect(transport);
  return client;
}

/** Creates the stdio-facing server that forwards every request to the backend. */
export function createStdioServer(backend: Client): Server {
  const server = new Server(
    { name: "crawlforge", version: "1.0.0" },
    { capabilities: { tools: {}, resources: {}, prompts: {} } },
  );

  server.setRequestHandler(ListToolsRequestSchema, async () => backend.listTools());
  server.setRequestHandler(CallToolRequestSchema, async (request) => backend.callTool(request.params));
  server.setRequestHandler(ListResourcesRequestSchema, async () => backend.listResources());
  server.setRequestHandler(ReadResourceRequestSchema, async (request) => backend.readResource(request.params));
  server.setRequestHandler(ListPromptsRequestSchema, async () => backend.listPrompts());
  server.setRequestHandler(GetPromptRequestSchema, async (request) => backend.getPrompt(request.params));

  return server;
}
