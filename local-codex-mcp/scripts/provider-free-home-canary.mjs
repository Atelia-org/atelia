#!/usr/bin/env node
// Disposable homes and localhost provider only; no inherited credentials/state.
import assert from "node:assert/strict";
import { once } from "node:events";
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from "node:fs";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { CodexBackend } from "../dist/src/codex/backend.js";
import { CodexAppServerClient } from "../dist/src/codex/client.js";
import { TaskStore } from "../dist/src/codex/task-store.js";
import { galateaCodexBackendProfile } from "../dist/src/galatea/backend-profile.js";
import { loadGalateaSidecarConfig, createGalateaCodexChildEnvironment } from "../dist/src/galatea/sidecar-config.js";
import { taskCommitment } from "../dist/src/galatea/task-commitment.js";
import { NullLogger } from "../dist/src/logger.js";
import { PathPolicy } from "../dist/src/security/paths.js";

const root = mkdtempSync(join(tmpdir(), "galatea-home-canary-"));
const personal = join(root, "personal");
const dedicated = join(root, "dedicated");
const empty = join(root, "empty");
const cwd = join(root, "workspace");
for (const dir of [personal, join(personal, ".codex"), dedicated, empty, cwd]) mkdirSync(dir);
writeFileSync(join(personal, ".codex", "config.toml"), 'model = "personal-sentinel"\n');
const requests = [];
const server = createServer(async (request, response) => {
  const chunks = [];
  for await (const chunk of request) chunks.push(chunk);
  const body = JSON.parse(Buffer.concat(chunks).toString());
  requests.push({ url: request.url, model: body.model, auth: request.headers.authorization });
  const item = { type: "message", role: "assistant", id: "message", phase: "final_answer",
    content: [{ type: "output_text", text: "HOME_CANARY_OK" }] };
  const events = [
    { type: "response.created", response: { id: "response" } },
    { type: "response.output_item.added", output_index: 0, item: { ...item, content: [] } },
    { type: "response.output_text.delta", item_id: item.id, output_index: 0, content_index: 0, delta: "HOME_CANARY_OK" },
    { type: "response.output_item.done", output_index: 0, item },
    { type: "response.completed", response: { id: "response", status: "completed", output: [item],
      usage: { input_tokens: 0, output_tokens: 0, total_tokens: 0 } } },
  ];
  response.writeHead(200, { "content-type": "text/event-stream" });
  response.end(events.map(event => `event: ${event.type}\ndata: ${JSON.stringify(event)}\n\n`).join(""));
});
let backend;
let client;
function configure(home, provider) {
  writeFileSync(join(home, "config.toml"), `
model = "model-${provider}"
model_provider = "${provider}"
approval_policy = "never"
sandbox_mode = "read-only"
web_search = "disabled"
[features]
apps = false
[analytics]
enabled = false
[feedback]
enabled = false
[model_providers.${provider}]
name = "Local fixture"
base_url = "http://127.0.0.1:${server.address().port}/${provider}"
wire_api = "responses"
requires_openai_auth = false
env_key = "HOME_CANARY_KEY"
supports_websockets = false
request_max_retries = 0
stream_max_retries = 0
`);
}
async function open(home) {
  const config = loadGalateaSidecarConfig({ CODEX_BRIDGE_ALLOWED_ROOTS: JSON.stringify([cwd]) });
  client = new CodexAppServerClient({ command: config.bridge.codexCommand, args: config.bridge.codexArgs,
    env: createGalateaCodexChildEnvironment({ PATH: process.env.PATH, HOME: personal, CODEX_HOME: home,
      HOME_CANARY_KEY: "synthetic-only", XDG_CONFIG_HOME: join(personal, "xdg"),
      XDG_DATA_HOME: join(personal, "data"), XDG_CACHE_HOME: join(personal, "cache") }),
    requestTimeoutMs: 15_000, logger: new NullLogger() });
  backend = new CodexBackend({ client, pathPolicy: await PathPolicy.create([cwd], cwd),
    store: new TaskStore(20_000, 100), logger: new NullLogger(), profile: galateaCodexBackendProfile });
  await client.start();
  assert.deepEqual(await client.request("account/read", { refreshToken: false }),
    { account: null, requiresOpenaiAuth: false });
}
async function turn(threadId, dispatchId) {
  const task = "Return the local fixture.";
  let unsubscribe;
  let timer;
  const completed = new Promise((resolve, reject) => {
    timer = setTimeout(() => reject(new Error("Local turn timed out")), 20_000);
    unsubscribe = client.subscribe(event => {
      if (event.method === "turn/completed" && event.params.threadId === threadId) resolve();
    });
  });
  try {
    const accepted = await backend.startBoundTurn({ threadId, cwd, dispatchId, task });
    await completed;
    const inspection = { threadId, dispatchId, ...taskCommitment(task), expectedTurnId: accepted.turnId, maximumFinalUtf8Bytes: 20_000 };
    assert.equal((await backend.inspectDispatch(inspection)).kind, "completed");
    return inspection;
  } finally { clearTimeout(timer); unsubscribe(); }
}
try {
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  configure(dedicated, "first");
  await open(dedicated);
  assert.equal((await client.request("config/read", {})).config.model, "model-first");
  const binding = await backend.ensureBinding({ cwd });
  const first = await turn(binding.threadId, "first-mail");
  await backend.stop();
  configure(dedicated, "second");
  await open(dedicated);
  assert.equal((await client.request("config/read", {})).config.model, "model-second");
  // Production inspection verifies persisted ownership and the exact old turn.
  assert.equal((await backend.inspectDispatch(first)).kind, "completed");
  await assert.rejects(turn(binding.threadId, "second-mail"), error =>
    error.dispatchState === "not-dispatched" && /provider `first` not found/.test(error.message));
  const fresh = await backend.ensureBinding({ cwd });
  await turn(fresh.threadId, "third-mail");
  assert.deepEqual(requests, [
    { url: "/first/responses", model: "model-first", auth: "Bearer synthetic-only" },
    { url: "/second/responses", model: "model-second", auth: "Bearer synthetic-only" },
  ]);
  await backend.stop();
  configure(empty, "second");
  await open(empty);
  await assert.rejects(client.request("thread/read", { threadId: binding.threadId, includeTurns: false }));
  process.stdout.write("home canary PASS: dedicated config, null-account provider, persisted ownership, cold switch uses new provider for new threads, old provider identity retained, empty-home rejection; 2 localhost requests\n");
} finally {
  try { await backend?.stop(); } finally {
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
    rmSync(root, { recursive: true, force: true });
  }
}
