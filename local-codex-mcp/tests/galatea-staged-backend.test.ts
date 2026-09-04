import assert from "node:assert/strict";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import test, { type TestContext } from "node:test";
import { fileURLToPath } from "node:url";
import { CodexBackend } from "../src/codex/backend.js";
import { CodexAppServerClient } from "../src/codex/client.js";
import { TaskStore } from "../src/codex/task-store.js";
import { galateaCodexBackendProfile } from "../src/galatea/backend-profile.js";
import { NullLogger } from "../src/logger.js";
import { PathPolicy } from "../src/security/paths.js";

const fixture = fileURLToPath(new URL("./fixtures/fake-app-server.js", import.meta.url));
const tools = { webSearch: "live", imageGeneration: true, viewImage: true } as const;

async function harness(t: TestContext, options: { requestTimeoutMs?: number; fixtureArgs?: string[]; persistent?: boolean } = {}) {
  const root = await mkdtemp(path.join(os.tmpdir(), "galatea-staged-backend-"));
  const lifecycleFile = options.persistent ? path.join(root, "lifecycle.log") : undefined;
  const stateFile = options.persistent ? path.join(root, "state.json") : undefined;
  const client = new CodexAppServerClient({
    command: process.execPath,
    args: [fixture, ...(options.fixtureArgs ?? []), ...(lifecycleFile ? [`--lifecycle-file=${lifecycleFile}`] : []), ...(stateFile ? [`--state-file=${stateFile}`] : [])],
    requestTimeoutMs: options.requestTimeoutMs ?? 1_000,
    logger: new NullLogger(),
  });
  const store = new TaskStore(20_000, 100);
  const backend = new CodexBackend({
    client,
    pathPolicy: await PathPolicy.create([root], root),
    store,
    logger: new NullLogger(),
    profile: galateaCodexBackendProfile,
    galateaMaximumFinalUtf8Bytes: 20_000,
  });
  t.after(async () => { await backend.stop(); await rm(root, { recursive: true }); });
  return { root, client, backend, store, lifecycleFile };
}

async function bind(value: Awaited<ReturnType<typeof harness>>) {
  return value.backend.ensureBinding({ cwd: value.root, mode: "work", tools });
}

async function start(value: Awaited<ReturnType<typeof harness>>, threadId: string, dispatchId: string, task: string) {
  return value.backend.startBoundTurn({
    threadId, expectedCwd: value.root, dispatchId, task, mode: "work", localCommandNetwork: false, tools,
  });
}

test("ensureBinding verifies ownership with metadata read and bounded turns page", async (t) => {
  const value = await harness(t);
  const binding = await bind(value);
  const requests = await value.client.request<{ threadReadIncludeTurns: boolean[]; threadTurnsListCount: number; turnStartCount: number }>("test/lastRequests", {});
  assert.match(binding.threadId, /^thread-/);
  assert.ok(requests.threadReadIncludeTurns.every((item) => item === false));
  assert.equal(requests.threadTurnsListCount, 1);
  assert.equal(requests.turnStartCount, 0);
});

test("Accepted uses exact live turn and completion survives early start-response reordering", async (t) => {
  const value = await harness(t);
  const binding = await bind(value);
  const task = "[EARLY][NATURAL] exact task";
  const accepted = await start(value, binding.threadId, "mail-early", task);
  const result = await value.backend.inspectDispatch({
    threadId: binding.threadId, expectedCwd: value.root, dispatchId: "mail-early", task,
    expectedTurnId: accepted.turnId, maximumFinalUtf8Bytes: 20_000,
  });
  assert.equal(result.kind, "completed");
  assert.equal(result.source, "live");
  if (result.kind === "completed") assert.match(result.final, /事情已经办妥/);
});

test("Accepted missing from official turns is stable unavailable, not not-found", async (t) => {
  const sanitizedFixture = JSON.parse(await readFile(
    path.join(process.cwd(), "tests/fixtures/accepted-turn-not-visible.json"),
    "utf8",
  )) as { expected: { code: string } };
  assert.equal(sanitizedFixture.expected.code, "ACCEPTED_TURN_NOT_VISIBLE");
  const value = await harness(t, { fixtureArgs: ["--hide-all-nonempty-turns"], persistent: true });
  const binding = await bind(value);
  const task = "[LONG] exact task";
  const accepted = await start(value, binding.threadId, "mail-hidden", task);
  await assert.rejects(value.client.request("test/crash", {}));
  const result = await value.backend.inspectDispatch({
    threadId: binding.threadId, expectedCwd: value.root, dispatchId: "mail-hidden", task,
    expectedTurnId: accepted.turnId, maximumFinalUtf8Bytes: 20_000,
  });
  assert.deepEqual(result, {
    kind: "unavailable", threadId: binding.threadId, turnId: accepted.turnId,
    source: "persistent", code: "ACCEPTED_TURN_NOT_VISIBLE",
  });
  assert.ok(value.lifecycleFile);
  const lifecycle = await readFile(value.lifecycleFile, "utf8");
  assert.equal(lifecycle.split("\n").filter((line) => line.endsWith(":turn/start")).length, 1);
});

test("OutcomeUnknown alone returns persistent not-found and discovers a timed-out start", async (t) => {
  const value = await harness(t, { requestTimeoutMs: 300 });
  const binding = await bind(value);
  const missing = await value.backend.inspectDispatch({
    threadId: binding.threadId, expectedCwd: value.root, dispatchId: "missing", task: "never",
    expectedTurnId: null, maximumFinalUtf8Bytes: 20_000,
  });
  assert.deepEqual(missing, { kind: "not-found", threadId: binding.threadId, source: "persistent" });

  const task = "[HANG_TURN_START][NATURAL] exact task";
  await assert.rejects(start(value, binding.threadId, "mail-unknown", task), /timed out/);
  await delay(30);
  const recovered = await value.backend.inspectDispatch({
    threadId: binding.threadId, expectedCwd: value.root, dispatchId: "mail-unknown", task,
    expectedTurnId: null, maximumFinalUtf8Bytes: 20_000,
  });
  assert.equal(recovered.kind, "completed");
  assert.equal(recovered.source, "persistent");
  const counts = await value.client.request<{ turnStartCount: number }>("test/lastRequests", {});
  assert.equal(counts.turnStartCount, 1);
});

test("cold restart clears live observations and pages the persisted exact Accepted turn", async (t) => {
  const value = await harness(t, { persistent: true });
  const binding = await bind(value);
  const task = "[LONG] exact task";
  const accepted = await start(value, binding.threadId, "mail-restart", task);
  assert.equal((await value.backend.inspectDispatch({
    threadId: binding.threadId, expectedCwd: value.root, dispatchId: "mail-restart", task,
    expectedTurnId: accepted.turnId, maximumFinalUtf8Bytes: 20_000,
  })).source, "live");
  await assert.rejects(value.client.request("test/crash", {}));
  assert.equal((await value.backend.status(binding.threadId)).status, "running");
  const cold = await value.backend.inspectDispatch({
    threadId: binding.threadId, expectedCwd: value.root, dispatchId: "mail-restart", task,
    expectedTurnId: accepted.turnId, maximumFinalUtf8Bytes: 20_000,
  });
  assert.equal(cold.kind, "running");
  assert.equal(cold.source, "persistent");
  assert.ok(value.lifecycleFile);
  assert.equal((await readFile(value.lifecycleFile, "utf8")).split("\n").filter((line) => line.startsWith("start:")).length, 2);
});

test("cold Accepted lookup scans all turn and item pages for a non-latest target", async (t) => {
  const value = await harness(t, { persistent: true, fixtureArgs: ["--inspection-page-size=1"] });
  const binding = await bind(value);
  const firstTask = "[NATURAL] first exact task";
  const first = await start(value, binding.threadId, "mail-first", firstTask);
  await delay(30);
  await start(value, binding.threadId, "mail-second", "[NATURAL] later task");
  await delay(30);
  await assert.rejects(value.client.request("test/crash", {}));
  const result = await value.backend.inspectDispatch({
    threadId: binding.threadId, expectedCwd: value.root, dispatchId: "mail-first", task: firstTask,
    expectedTurnId: first.turnId, maximumFinalUtf8Bytes: 20_000,
  });
  assert.equal(result.kind, "completed");
  assert.equal(result.source, "persistent");
  const counts = await value.client.request<{ threadTurnsListCount: number; threadItemsListCount: number }>("test/lastRequests", {});
  assert.ok(counts.threadTurnsListCount >= 2);
  assert.ok(counts.threadItemsListCount >= 2);
});

for (const [argument, code] of [
  ["--empty-turn-page-with-next", "PAGE_SHAPE_INVALID"],
  ["--loop-turn-cursor", "PAGINATION_CURSOR_LOOP"],
  ["--wrong-filtered-turn", "DISPATCH_TURN_MISMATCH"],
  ["--duplicate-item-entry", "ITEM_ID_NOT_UNIQUE"],
] as const) {
  test(`cold Accepted inspection fails closed for ${argument}`, async (t) => {
    const value = await harness(t, { persistent: true, fixtureArgs: [argument] });
    const binding = await bind(value);
    const task = "[LONG] exact task";
    const accepted = await start(value, binding.threadId, "mail-malformed", task);
    await assert.rejects(value.client.request("test/crash", {}));
    const result = await value.backend.inspectDispatch({
      threadId: binding.threadId, expectedCwd: value.root, dispatchId: "mail-malformed", task,
      expectedTurnId: accepted.turnId, maximumFinalUtf8Bytes: 20_000,
    });
    assert.equal(result.kind, "ambiguous");
    if (result.kind === "ambiguous") assert.equal(result.code, code);
  });
}

test("inspection preflight rejects ownership and cwd drift before live evidence", async (t) => {
  const value = await harness(t);
  const binding = await bind(value);
  const task = "[LONG] exact task";
  const accepted = await start(value, binding.threadId, "mail-drift", task);
  await value.client.request("test/setThreadName", { threadId: binding.threadId, name: "not-owned" });
  const ownership = await value.backend.inspectDispatch({
    threadId: binding.threadId, expectedCwd: value.root, dispatchId: "mail-drift", task,
    expectedTurnId: accepted.turnId, maximumFinalUtf8Bytes: 20_000,
  });
  assert.deepEqual(ownership, { kind: "ambiguous", threadId: binding.threadId, source: "persistent", code: "THREAD_OWNERSHIP_MISMATCH" });
});
