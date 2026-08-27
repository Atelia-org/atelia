import assert from "node:assert/strict";
import test from "node:test";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import type {
  DelegateTaskInput,
  TaskBackend,
  TaskSnapshot,
} from "../src/backend/task-backend.js";
import { BridgeError } from "../src/errors.js";
import { NullLogger } from "../src/logger.js";
import { createMcpServer } from "../src/mcp/server.js";

const snapshot: TaskSnapshot = {
  threadId: "thread-1",
  latestTurnId: "turn-1",
  status: "completed",
  result: "Done.",
  changedFiles: [],
  validation: [],
  warnings: [],
};

class StubBackend implements TaskBackend {
  calls = 0;
  lastDelegate?: DelegateTaskInput;
  async start() {}
  async stop() {}
  async delegate(input: DelegateTaskInput) {
    this.calls += 1;
    this.lastDelegate = input;
    if (input.cwd === "/outside") {
      throw new BridgeError("CWD_NOT_ALLOWED", "Requested cwd is outside configured project roots.");
    }
    return snapshot;
  }
  async continue() { return snapshot; }
  async status() { return snapshot; }
  async read() { return snapshot; }
  async interrupt() { return snapshot; }
}

async function setup(t: test.TestContext) {
  const backend = new StubBackend();
  const server = createMcpServer(backend, { defaultWaitMs: 20, maxWaitMs: 1_000 }, new NullLogger());
  const client = new Client({ name: "test", version: "1" });
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  await Promise.all([server.connect(serverTransport), client.connect(clientTransport)]);
  t.after(async () => Promise.all([client.close(), server.close()]));
  return { backend, client };
}

test("tools/list exposes the five bounded tools with accurate annotations", async (t) => {
  const { client } = await setup(t);
  const tools = await client.listTools();
  assert.deepEqual(
    tools.tools.map((tool) => tool.name).sort(),
    ["codex_continue", "codex_delegate", "codex_interrupt", "codex_read", "codex_status"],
  );
  assert.equal(tools.tools.find((tool) => tool.name === "codex_status")?.annotations?.readOnlyHint, true);
  const delegate = tools.tools.find((tool) => tool.name === "codex_delegate");
  assert.equal(delegate?.annotations?.readOnlyHint, false);
  const properties = delegate?.inputSchema.properties ?? {};
  assert.equal(Object.hasOwn(properties, "network"), false);
  for (const name of [
    "local_command_network",
    "web_search",
    "image_generation",
    "view_image",
  ]) {
    assert.equal(Object.hasOwn(properties, name), true);
  }
});

test("delegate forwards independent local-network and built-in-tool policy", async (t) => {
  const { backend, client } = await setup(t);
  const result = await client.callTool({
    name: "codex_delegate",
    arguments: {
      task: "inspect",
      local_command_network: false,
      web_search: "live",
      image_generation: true,
      view_image: false,
    },
  });

  assert.notEqual(result.isError, true);
  assert.equal(backend.lastDelegate?.localCommandNetwork, false);
  assert.deepEqual(backend.lastDelegate?.tools, {
    webSearch: "live",
    imageGeneration: true,
    viewImage: false,
  });
});

test("delegate defaults to development-friendly network and built-in tools", async (t) => {
  const { backend, client } = await setup(t);
  const result = await client.callTool({
    name: "codex_delegate",
    arguments: { task: "inspect" },
  });

  assert.notEqual(result.isError, true);
  assert.equal(backend.lastDelegate?.localCommandNetwork, true);
  assert.deepEqual(backend.lastDelegate?.tools, {
    webSearch: "live",
    imageGeneration: true,
    viewImage: true,
  });
});

test("invalid MCP input schema is rejected before backend invocation", async (t) => {
  const { backend, client } = await setup(t);
  const result = await client.callTool({ name: "codex_delegate", arguments: { task: "" } });
  assert.equal(result.isError, true);
  assert.equal(backend.calls, 0);
});

test("stable cwd errors do not expose a stack", async (t) => {
  const { client } = await setup(t);
  const result = await client.callTool({
    name: "codex_delegate",
    arguments: { task: "inspect", cwd: "/outside" },
  });
  assert.equal(result.isError, true);
  const content = result.content as Array<{ type: string; text?: string }>;
  const text = content[0]?.type === "text" ? (content[0].text ?? "") : "";
  assert.match(text, /CWD_NOT_ALLOWED/);
  assert.doesNotMatch(text, /\n\s+at /);
});
