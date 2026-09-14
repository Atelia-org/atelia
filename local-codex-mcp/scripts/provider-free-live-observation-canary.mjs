#!/usr/bin/env node

// Exercises the production backend and pinned app-server against a localhost SSE
// fixture. HOME, auth, configuration, workspace, and sessions are disposable.
import assert from "node:assert/strict";
import { once } from "node:events";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { CodexBackend } from "../dist/src/codex/backend.js";
import { CodexAppServerClient } from "../dist/src/codex/client.js";
import { TaskStore } from "../dist/src/codex/task-store.js";
import { galateaCodexBackendProfile } from "../dist/src/galatea/backend-profile.js";
import { loadGalateaSidecarConfig } from "../dist/src/galatea/sidecar-config.js";
import { NullLogger } from "../dist/src/logger.js";
import { PathPolicy } from "../dist/src/security/paths.js";

const root = mkdtempSync(join(tmpdir(), "atelia-codex-live-observation-"));
const home = join(root, "home");
const codexHome = join(home, ".codex");
const cwd = join(root, "workspace");
const temporary = join(root, "temporary");
const task = "Synthetic protocol probe; return the local fixture.";
const dispatchId = "provider-free-live-observation";
const final = "LOCAL_FIXTURE_OK";
const syntheticKey = "synthetic-canary-not-a-real-key";
const requests = [];
let backend;
let startResponse;
let historyPages = 0;
let timeout;
let fail;
const failed = new Promise((_, reject) => { fail = reject; });
let releaseResponse;
const responseBarrier = new Promise((resolve) => { releaseResponse = resolve; });
let receivedResponseRequest;
const requestReceived = new Promise((resolve) => { receivedResponseRequest = resolve; });
let startedTurn;
const startedNotification = new Promise((resolve) => { startedTurn = resolve; });
let completedUserItem;
const userCompletedNotification = new Promise((resolve) => { completedUserItem = resolve; });
let completedTurn;
const completed = new Promise((resolve) => { completedTurn = resolve; });
const server = createServer(async (request, response) => {
  try {
    const buffers = [];
    for await (const chunk of request) buffers.push(chunk);
    requests.push({ method: request.method, url: request.url });
    assert.equal(request.headers.authorization, `Bearer ${syntheticKey}`, "Only the fixed synthetic credential is permitted.");
    assert.equal(request.method, "POST");
    assert.equal(request.url, "/v1/responses");
    assert.equal(JSON.parse(Buffer.concat(buffers).toString()).stream, true);
    receivedResponseRequest();
    await responseBarrier;
    const message = {
      type: "message", role: "assistant", id: "fixture-message", phase: "final_answer",
      content: [{ type: "output_text", text: final }],
    };
    const stream = [
      { type: "response.created", response: { id: "fixture-response" } },
      { type: "response.output_item.added", output_index: 0, item: { ...message, content: [] } },
      { type: "response.output_text.delta", item_id: message.id, output_index: 0, content_index: 0, delta: final },
      { type: "response.output_item.done", output_index: 0, item: message },
      { type: "response.completed", response: {
        id: "fixture-response", status: "completed", output: [message],
        usage: { input_tokens: 0, output_tokens: 0, total_tokens: 0 },
      } },
    ];
    response.writeHead(200, { "content-type": "text/event-stream" });
    response.end(stream.map((event) => `event: ${event.type}\ndata: ${JSON.stringify(event)}\n\n`).join(""));
  } catch (error) {
    fail(error);
    response.destroy();
  }
});

async function run() {
  for (const directory of [home, codexHome, cwd, temporary]) mkdirSync(directory, { recursive: true });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  writeFileSync(join(codexHome, "config.toml"), `
model = "mock-model"
model_provider = "local_fixture"
approval_policy = "never"
sandbox_mode = "read-only"
web_search = "disabled"
cli_auth_credentials_store = "file"
[features]
apps = false
[analytics]
enabled = false
[feedback]
enabled = false
[model_providers.local_fixture]
name = "Isolated synthetic protocol fixture"
base_url = "http://127.0.0.1:${server.address().port}/v1"
wire_api = "responses"
requires_openai_auth = true
supports_websockets = false
request_max_retries = 0
stream_max_retries = 0
`);
  // The production account gate needs a non-null account. Only this synthetic
  // credential exists here, and the localhost handler checks its exact value.
  writeFileSync(join(codexHome, "auth.json"), JSON.stringify({ OPENAI_API_KEY: syntheticKey }));
  const config = loadGalateaSidecarConfig({ CODEX_BRIDGE_ALLOWED_ROOTS: JSON.stringify([cwd]) });
  const client = new CodexAppServerClient({
    command: config.bridge.codexCommand,
    args: config.bridge.codexArgs,
    env: {
      PATH: process.env.PATH, HOME: home, CODEX_HOME: codexHome, TMPDIR: temporary,
      XDG_CONFIG_HOME: join(home, ".config"), XDG_DATA_HOME: join(home, ".local", "share"),
      XDG_CACHE_HOME: join(home, ".cache"),
    },
    requestTimeoutMs: 15_000,
    logger: new NullLogger(),
  });
  // Observe the real request's response without replacing any wire behavior.
  const request = client.request.bind(client);
  client.request = async (method, ...args) => {
    const result = await request(method, ...args);
    if (method === "turn/start") startResponse = result;
    if (method === "thread/turns/list" || method === "thread/items/list") historyPages += 1;
    return result;
  };
  backend = new CodexBackend({
    client, pathPolicy: await PathPolicy.create([cwd], cwd), store: new TaskStore(20_000, 100),
    logger: new NullLogger(), profile: galateaCodexBackendProfile, galateaMaximumFinalUtf8Bytes: 20_000,
  });
  client.subscribe((event) => {
    if (event.method === "turn/started") startedTurn(event.params);
    if (event.method === "item/completed" && event.params.item.type === "userMessage") completedUserItem(event.params);
    if (event.method === "turn/completed") completedTurn(event.params);
  });
  const { threadId } = await backend.ensureBinding({ cwd });
  const accepted = await backend.startBoundTurn({ threadId, cwd, dispatchId, task });
  assert.equal(accepted.threadId, threadId);
  assert.equal(accepted.turnId, startResponse.turn.id);
  assert.deepEqual(startResponse.turn.items, []);
  assert.equal(startResponse.turn.itemsView, "notLoaded");
  // HTTP and stdout have separate delivery queues; await each required fact.
  const [, startedEvent, user] = await Promise.all([requestReceived, startedNotification, userCompletedNotification]);
  const started = startedEvent.turn;
  assert.equal(started.id, accepted.turnId);
  assert.deepEqual(started.items, []);
  assert.equal(started.itemsView, "notLoaded");
  assert.equal(user.turnId, accepted.turnId);
  assert.equal(user.item.clientId, dispatchId);
  assert.deepEqual(user.item.content, [{ type: "text", text: task, text_elements: [] }]);
  const inspect = { threadId, dispatchId, task, expectedTurnId: accepted.turnId, maximumFinalUtf8Bytes: 20_000 };
  for (let index = 0; index < 10; index += 1) {
    const running = await backend.inspectDispatch(inspect);
    assert.equal(running.kind, "running");
    assert.equal(running.source, "live", `Running inspection ${index + 1} must retain live evidence.`);
    assert.equal(running.turnId, accepted.turnId);
  }
  releaseResponse();
  const terminal = (await completed).turn;
  assert.equal(terminal.itemsView, "summary");
  assert.equal(terminal.items.some((item) => item.type === "userMessage"), false);
  const result = await backend.inspectDispatch(inspect);
  assert.deepEqual(result, { kind: "completed", threadId, turnId: accepted.turnId, final, source: "live" });
  assert.equal(historyPages, 0, "Live observations must not depend on paginated history.");
  assert.deepEqual(requests, [{ method: "POST", url: "/v1/responses" }]);
  process.stdout.write("provider-free live observation canary PASS: sparse start, exact Accepted, 10 live Running inspections, live Completed, 0 history pages, 1 localhost request with synthetic auth\n");
}

try {
  timeout = setTimeout(() => fail(new Error("Live observation canary timed out after 30 seconds.")), 30_000);
  await Promise.race([run(), failed]);
} finally {
  clearTimeout(timeout);
  releaseResponse();
  try { await backend?.stop(); }
  finally {
    server.closeAllConnections();
    await new Promise((resolve) => server.close(resolve));
    rmSync(root, { recursive: true, force: true });
  }
}
