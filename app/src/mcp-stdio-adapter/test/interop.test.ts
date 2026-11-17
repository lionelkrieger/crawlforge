/**
 * MCP interop conformance suite (criterion #16 gate): the official
 * @modelcontextprotocol/sdk client exercises the hand-rolled C# server on
 * BOTH transports — streamable HTTP and the packaged stdio adapter — covering
 * the handshake, all 8 tools, all 15 resources, all 5 prompts, invalid-token
 * rejection, and rotation invalidation.
 *
 * Driven by the C# McpInteropTests fixture, which starts the real loopback
 * server over the real persistence stack and passes connection details via
 * environment variables. Skipped when run without that fixture.
 */
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { describe, expect, it } from "vitest";

const endpoint = process.env.CF_MCP_ENDPOINT;
const token = process.env.CF_MCP_TOKEN ?? "";
const newToken = process.env.CF_MCP_NEW_TOKEN ?? "";
const mode = process.env.CF_MCP_MODE ?? "full";
const adapterPath = process.env.CF_MCP_ADAPTER ?? "";
const auditId = process.env.CF_MCP_AUDIT_ID ?? "1";
const urlId = process.env.CF_MCP_URL_ID ?? "1";
const projectId = process.env.CF_MCP_PROJECT_ID ?? "1";
const configurationId = process.env.CF_MCP_CONFIGURATION_ID ?? "1";
const scheduleId = process.env.CF_MCP_SCHEDULE_ID ?? "1";
const importId = process.env.CF_MCP_IMPORT_ID ?? "1";

const describeInterop = endpoint ? describe : describe.skip;
const itFull = mode === "full" ? it : it.skip;

async function connectHttp(bearer: string): Promise<Client> {
  const transport = new StreamableHTTPClientTransport(new URL(endpoint!), {
    requestInit: { headers: { Authorization: `Bearer ${bearer}` } },
  });
  const client = new Client({ name: "interop-http", version: "0.0.0" }, { capabilities: {} });
  await client.connect(transport);
  return client;
}

async function connectStdio(bearer: string): Promise<Client> {
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [adapterPath, "--endpoint", endpoint!, "--token", bearer],
  });
  const client = new Client({ name: "interop-stdio", version: "0.0.0" }, { capabilities: {} });
  await client.connect(transport);
  return client;
}

function toolArguments(name: string): Record<string, unknown> {
  switch (name) {
    case "start-audit":
      return { projectId: Number(projectId), configurationId: Number(configurationId) };
    case "pause-audit":
    case "resume-audit":
    case "cancel-audit":
    case "get-audit-progress":
      return { auditId: Number(auditId) };
    case "re-audit":
      return { auditId: Number(auditId), refresh: false };
    case "analyze-single-page":
      return { url: "https://example.test/interop", render: false };
    case "run-schedule-now":
      return { scheduleId: Number(scheduleId) };
    default:
      throw new Error(`Unknown tool ${name}`);
  }
}

const ALL_TOOLS = [
  "start-audit",
  "pause-audit",
  "resume-audit",
  "cancel-audit",
  "re-audit",
  "analyze-single-page",
  "run-schedule-now",
  "get-audit-progress",
];

const ALL_RESOURCE_URIS = [
  `crawlforge://audits/${auditId}/summary`,
  `crawlforge://audits/${auditId}/high-severity-findings`,
  `crawlforge://audits/${auditId}/urls/${urlId}`,
  "crawlforge://audits",
  "crawlforge://schedules",
  `crawlforge://audits/${auditId}/progress`,
  `crawlforge://audits/${auditId}/urls/${urlId}/content`,
  `crawlforge://audits/${auditId}/ngrams`,
  `crawlforge://audits/${auditId}/waf-blocks`,
  `crawlforge://audits/${auditId}/host-throttle`,
  `crawlforge://audits/${auditId}/redirect-matrix`,
  `crawlforge://audits/${auditId}/log-correlation?import=${importId}`,
  `crawlforge://audits/${auditId}/semantic-links`,
  `crawlforge://audits/${auditId}/geo-readiness`,
  `crawlforge://audits/${auditId}/analytics-coverage`,
];

const ALL_PROMPTS: Array<{ name: string; arguments: Record<string, string> }> = [
  { name: "audit-health-briefing", arguments: { auditId } },
  { name: "high-severity-triage", arguments: { auditId } },
  { name: "audit-delta-briefing", arguments: { baseAuditId: auditId, targetAuditId: auditId } },
  { name: "ngram-topical-briefing", arguments: { auditId } },
  { name: "semantic-topical-briefing", arguments: { auditId } },
];

describeInterop("MCP interop — full coverage", () => {
  itFull("HTTP transport: standard handshake negotiates capabilities", async () => {
    const client = await connectHttp(token);
    try {
      const info = client.getServerVersion();
      expect(info?.name).toBe("crawlforge");
      expect(client.getServerCapabilities()?.tools).toBeDefined();
      expect(client.getServerCapabilities()?.resources).toBeDefined();
      expect(client.getServerCapabilities()?.prompts).toBeDefined();
    } finally {
      await client.close();
    }
  });

  itFull("HTTP transport: all 8 lifecycle tools round-trip", async () => {
    const client = await connectHttp(token);
    try {
      const listed = await client.listTools();
      expect(listed.tools.map((t) => t.name).sort()).toEqual([...ALL_TOOLS].sort());
      for (const name of ALL_TOOLS) {
        const result = await client.callTool({ name, arguments: toolArguments(name) });
        expect(result.isError, `tool ${name} failed: ${JSON.stringify(result.content)}`).toBeFalsy();
        const content = result.content as Array<{ type: string; text: string }>;
        expect(content.length).toBeGreaterThan(0);
        expect(content[0].type).toBe("text");
        expect(() => JSON.parse(content[0].text)).not.toThrow();
      }
    } finally {
      await client.close();
    }
  });

  itFull("HTTP transport: all 15 resources list and read", async () => {
    const client = await connectHttp(token);
    try {
      const listed = await client.listResources();
      expect(listed.resources.length).toBe(15);
      for (const uri of ALL_RESOURCE_URIS) {
        const result = await client.readResource({ uri });
        expect(result.contents.length).toBeGreaterThan(0);
        const first = result.contents[0] as { uri: string; mimeType?: string; text?: string };
        expect(first.uri).toBe(uri);
        expect(first.mimeType).toBe("application/json");
        expect(() => JSON.parse(first.text ?? "")).not.toThrow();
      }
    } finally {
      await client.close();
    }
  });

  itFull("HTTP transport: all 5 prompts materialize side-effect free", async () => {
    const client = await connectHttp(token);
    try {
      const listed = await client.listPrompts();
      expect(listed.prompts.map((p) => p.name).sort()).toEqual(ALL_PROMPTS.map((p) => p.name).sort());
      for (const prompt of ALL_PROMPTS) {
        const result = await client.getPrompt({ name: prompt.name, arguments: prompt.arguments });
        expect(result.messages.length).toBeGreaterThan(0);
        expect(result.messages[0].role).toBe("user");
      }
    } finally {
      await client.close();
    }
  });

  itFull("HTTP transport: invalid token is rejected", async () => {
    await expect(connectHttp("not-the-token")).rejects.toThrow();
  });

  itFull("stdio transport: handshake and representative round-trips through the packaged adapter", async () => {
    const client = await connectStdio(token);
    try {
      expect(client.getServerVersion()?.name).toBe("crawlforge");

      const tools = await client.listTools();
      expect(tools.tools.map((t) => t.name).sort()).toEqual([...ALL_TOOLS].sort());

      const progress = await client.callTool({
        name: "get-audit-progress",
        arguments: { auditId: Number(auditId) },
      });
      expect(progress.isError).toBeFalsy();

      const resources = await client.listResources();
      expect(resources.resources.length).toBe(15);
      const summary = await client.readResource({ uri: `crawlforge://audits/${auditId}/summary` });
      expect(summary.contents.length).toBeGreaterThan(0);

      const prompts = await client.listPrompts();
      expect(prompts.prompts.length).toBe(5);
      const briefing = await client.getPrompt({
        name: "audit-health-briefing",
        arguments: { auditId },
      });
      expect(briefing.messages.length).toBeGreaterThan(0);
    } finally {
      await client.close();
    }
  });

  itFull("stdio transport: invalid token is rejected before bridging", async () => {
    await expect(connectStdio("not-the-token")).rejects.toThrow();
  });
});

describeInterop("MCP interop — rotation invalidation (criterion #16)", () => {
  it.runIf(mode === "rotation")("HTTP: prior token rejected after rotation", async () => {
    await expect(connectHttp(token)).rejects.toThrow();
  });

  it.runIf(mode === "rotation")("HTTP: rotated token accepted", async () => {
    const client = await connectHttp(newToken);
    try {
      expect(client.getServerVersion()?.name).toBe("crawlforge");
      const tools = await client.listTools();
      expect(tools.tools.length).toBe(8);
    } finally {
      await client.close();
    }
  });

  it.runIf(mode === "rotation")("stdio: prior token rejected after rotation", async () => {
    await expect(connectStdio(token)).rejects.toThrow();
  });

  it.runIf(mode === "rotation")("stdio: rotated token bridges successfully", async () => {
    const client = await connectStdio(newToken);
    try {
      expect(client.getServerVersion()?.name).toBe("crawlforge");
      const tools = await client.listTools();
      expect(tools.tools.length).toBe(8);
    } finally {
      await client.close();
    }
  });
});
