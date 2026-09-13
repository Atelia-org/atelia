import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, rm } from "node:fs/promises";
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
import type { JsonValue } from "../schemas/serde_json/JsonValue.js";

const fixture = fileURLToPath(new URL("./fixtures/fake-app-server.js", import.meta.url));
const acceptedTurnNotVisibleFixture = path.join(
  process.cwd(),
  "tests/fixtures/accepted-turn-not-visible.json",
);

async function harness(t: TestContext, options: { requestTimeoutMs?: number; fixtureArgs?: string[]; persistent?: boolean; codexConfig?: Record<string, JsonValue> } = {}) {
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
    galateaCodexConfig: options.codexConfig,
    galateaMaximumFinalUtf8Bytes: 20_000,
  });
  t.after(async () => { await backend.stop(); await rm(root, { recursive: true }); });
  return { root, client, backend, store, lifecycleFile };
}

async function bind(value: Awaited<ReturnType<typeof harness>>) {
  return value.backend.ensureBinding({ cwd: value.root });
}

async function start(value: Awaited<ReturnType<typeof harness>>, threadId: string, dispatchId: string, task: string) {
  return value.backend.startBoundTurn({
    threadId, cwd: value.root, dispatchId, task,
  });
}

const configCases: (Record<string, JsonValue> | undefined)[] = [undefined, {}, {
  sandbox_mode: "danger-full-access",
  approval_policy: "never",
  features: { apps: true, image_generation: false },
  sandbox_workspace_write: { writable_roots: ["/tmp/shared"], exclude_tmpdir_env_var: false },
  model_reasoning_effort: "high",
}];
for (const codexConfig of configCases) {
  test(`Galatea only forwards explicit native config: ${JSON.stringify(codexConfig)}`, async (t) => {
    const expected = structuredClone(codexConfig);
    const value = await harness(t, { codexConfig });
    // The sidecar owns a configuration snapshot; caller mutation cannot change it.
    if (codexConfig && "features" in codexConfig) codexConfig.features = { apps: false };
    const binding = await bind(value);
    await start(value, binding.threadId, "config-warmup", "[EARLY][NATURAL] warmup");
    await start(value, binding.threadId, "config-mail", "[EARLY][NATURAL] task");
    const requests = await value.client.request<{
      lastThreadStartParams: Record<string, unknown>;
      lastResumeParams: Record<string, unknown>;
      lastTurnParams: Record<string, unknown>;
    }>("test/lastRequests", {});
    for (const params of [requests.lastThreadStartParams, requests.lastResumeParams]) {
      if (expected && Object.keys(expected).length > 0) assert.deepEqual(params.config, expected);
      else assert.equal(Object.hasOwn(params, "config"), false);
      for (const key of ["approvalPolicy", "approvalsReviewer", "sandbox"]) {
        assert.equal(Object.hasOwn(params, key), false, key);
      }
    }
    for (const key of ["config", "approvalPolicy", "approvalsReviewer", "sandboxPolicy", "summary"]) {
      assert.equal(Object.hasOwn(requests.lastTurnParams, key), false, key);
    }
    assert.equal(requests.lastTurnParams.cwd, value.root);
    assert.equal(requests.lastTurnParams.clientUserMessageId, "config-mail");
  });
}

test("ensureBinding verifies new empty thread metadata without requiring a source rollout", async (t) => {
  const value = await harness(t, { fixtureArgs: ["--missing-empty-rollout"] });
  const binding = await bind(value);
  const requests = await value.client.request<{ threadReadIncludeTurns: boolean[]; threadTurnsListCount: number; turnStartCount: number }>("test/lastRequests", {});
  assert.match(binding.threadId, /^thread-/);
  assert.ok(requests.threadReadIncludeTurns.every((item) => item === false));
  assert.equal(requests.threadTurnsListCount, 0);
  assert.equal(requests.turnStartCount, 0);
  await assert.rejects(value.backend.inspectDispatch({
    threadId: binding.threadId, dispatchId: "unissued-mail", task: "not sent",
    expectedTurnId: "unavailable-turn", maximumFinalUtf8Bytes: 20_000,
  }), /missing source rollout/);
});

test("fresh binding starts its first turn without resuming an absent rollout", async (t) => {
  const value = await harness(t, { fixtureArgs: ["--missing-empty-rollout"] });
  const binding = await bind(value);
  const accepted = await start(value, binding.threadId, "first-mail", "[EARLY][NATURAL] first task");
  const requests = await value.client.request<{ threadResumeCount: number; turnStartCount: number }>("test/lastRequests", {});
  assert.equal(accepted.threadId, binding.threadId);
  assert.equal(requests.threadResumeCount, 0);
  assert.equal(requests.turnStartCount, 1);
});

test("fresh binding exemption cannot survive an app-server restart", async (t) => {
  const value = await harness(t, { fixtureArgs: ["--missing-empty-rollout"], persistent: true });
  const binding = await bind(value);
  await value.client.stop();
  await assert.rejects(start(value, binding.threadId, "cold-first-mail", "not sent"), /no rollout found/);
  const requests = await value.client.request<{ threadResumeCount: number; turnStartCount: number }>("test/lastRequests", {});
  assert.equal(requests.threadResumeCount, 1);
  assert.equal(requests.turnStartCount, 0);
});

test("ensureBinding rejects a nonempty thread/start response before ownership or dispatch", async (t) => {
  const value = await harness(t, { fixtureArgs: ["--nonempty-start-response"] });
  await assert.rejects(bind(value), /must be an empty owned thread/);
  const requests = await value.client.request<{ threadNameSetCount: number; turnStartCount: number }>("test/lastRequests", {});
  assert.equal(requests.threadNameSetCount, 0);
  assert.equal(requests.turnStartCount, 0);
});

test("Accepted uses exact live turn and completion survives early start-response reordering", async (t) => {
  const value = await harness(t);
  const binding = await bind(value);
  const task = "[EARLY][NATURAL] exact task";
  const accepted = await start(value, binding.threadId, "mail-early", task);
  const result = await value.backend.inspectDispatch({
    threadId: binding.threadId, dispatchId: "mail-early", task,
    expectedTurnId: accepted.turnId, maximumFinalUtf8Bytes: 20_000,
  });
  assert.equal(result.kind, "completed");
  assert.equal(result.source, "live");
  if (result.kind === "completed") assert.match(result.final, /事情已经办妥/);
});

test("live Running cannot hide a persistent terminal when terminal notifications are lost", async (t) => {
  const value = await harness(t);
  const binding = await bind(value);
  const task = "[DROP_TERMINAL_SIGNALS][NATURAL] exact task";
  const accepted = await start(value, binding.threadId, "mail-dropped-terminal", task);
  await delay(30);
  const completed = await value.backend.inspectDispatch({
    threadId: binding.threadId,
    dispatchId: "mail-dropped-terminal",
    task,
    expectedTurnId: accepted.turnId,
    maximumFinalUtf8Bytes: 20_000,
  });
  assert.equal(completed.kind, "completed");
  assert.equal(completed.source, "persistent");
});

test("incomplete live terminal maps to retryable inspection failure until final item arrives", async (t) => {
  const value = await harness(t);
  const binding = await bind(value);
  const task = "[SUMMARY_BEFORE_FINAL][NATURAL] exact task";
  const accepted = await start(value, binding.threadId, "mail-late-final", task);
  await delay(25);
  const request = {
    threadId: binding.threadId,
    dispatchId: "mail-late-final",
    task,
    expectedTurnId: accepted.turnId,
    maximumFinalUtf8Bytes: 20_000,
  };
  await assert.rejects(value.backend.inspectDispatch(request), (error: unknown) =>
    typeof error === "object" && error !== null && "code" in error
      && error.code === "CODEX_PROTOCOL_ERROR");
  await delay(60);
  const completed = await value.backend.inspectDispatch(request);
  assert.equal(completed.kind, "completed");
  assert.equal(completed.source, "live");
});

test("incomplete live terminal allows later healthy persistent final recovery without an item signal", async (t) => {
  const value = await harness(t);
  const binding = await bind(value);
  const task = "[SUMMARY_DROP_SIGNAL][NATURAL] exact task";
  const accepted = await start(value, binding.threadId, "mail-persistent-final", task);
  await delay(25);
  const request = {
    threadId: binding.threadId,
    dispatchId: "mail-persistent-final",
    task,
    expectedTurnId: accepted.turnId,
    maximumFinalUtf8Bytes: 20_000,
  };
  await assert.rejects(value.backend.inspectDispatch(request), (error: unknown) =>
    typeof error === "object" && error !== null && "code" in error
      && error.code === "CODEX_PROTOCOL_ERROR");
  await delay(80);
  const recovered = await value.backend.inspectDispatch(request);
  assert.equal(recovered.kind, "completed");
  assert.equal(recovered.source, "persistent");
});

test("turn/start response must match the pending dispatch identity and task", async (t) => {
  const value = await harness(t, { fixtureArgs: ["--mismatch-turn-start-response"] });
  const binding = await bind(value);
  await assert.rejects(start(
    value,
    binding.threadId,
    "mail-exact",
    "[STARTED_BEFORE_RESPONSE][LONG] exact task",
  ), (error: unknown) => typeof error === "object" && error !== null
    && "code" in error && error.code === "CODEX_PROTOCOL_ERROR");
  const counts = await value.client.request<{ turnStartCount: number }>("test/lastRequests", {});
  assert.equal(counts.turnStartCount, 1);
});

test("sparse turn/start projections acknowledge Accepted and defer dispatch/task evidence to persistent inspection", async (t) => {
  const value = await harness(t, { fixtureArgs: ["--sparse-start-projections"] });
  const binding = await bind(value);
  const task = "[NATURAL] sparse start response task";
  const accepted = await start(value, binding.threadId, "mail-sparse", task);
  await delay(30);
  const request = {
    threadId: binding.threadId, dispatchId: "mail-sparse", task,
    expectedTurnId: accepted.turnId, maximumFinalUtf8Bytes: 20_000,
  };
  const inspected = await value.backend.inspectDispatch(request);
  assert.equal(inspected.kind, "completed");
  assert.equal(inspected.source, "persistent");
  for (const changed of [{ task: "a different task" }, { dispatchId: "a-different-dispatch" }]) {
    const mismatch = await value.backend.inspectDispatch({ ...request, ...changed });
    assert.equal(mismatch.kind, "ambiguous");
    assert.equal(mismatch.source, "persistent");
  }
  const counts = await value.client.request<{ turnStartCount: number }>("test/lastRequests", {});
  assert.equal(counts.turnStartCount, 1);
});

test("Accepted missing from official turns is stable unavailable, not not-found", async (t) => {
  const sanitizedFixture = JSON.parse(await readFile(
    acceptedTurnNotVisibleFixture,
    "utf8",
  )) as {
    officialTurnsPages: Array<{ data: unknown[]; nextCursor: string | null; backwardsCursor: string | null }>;
    expected: { code: string };
  };
  assert.equal(sanitizedFixture.expected.code, "ACCEPTED_TURN_NOT_VISIBLE");
  assert.deepEqual(sanitizedFixture.officialTurnsPages, [{
    data: [], nextCursor: null, backwardsCursor: null,
  }]);
  const value = await harness(t, {
    fixtureArgs: [`--inspection-fixture=${acceptedTurnNotVisibleFixture}`],
    persistent: true,
  });
  const binding = await bind(value);
  const task = "[LONG] exact task";
  const accepted = await start(value, binding.threadId, "mail-hidden", task);
  await assert.rejects(value.client.request("test/crash", {}));
  const result = await value.backend.inspectDispatch({
    threadId: binding.threadId, dispatchId: "mail-hidden", task,
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

test("Accepted turn with an empty filtered item projection stays retryable unavailable", async (t) => {
  const value = await harness(t, { fixtureArgs: ["--empty-filtered-items"], persistent: true });
  const binding = await bind(value);
  const task = "[LONG] exact task";
  const accepted = await start(value, binding.threadId, "mail-items-empty", task);
  await assert.rejects(value.client.request("test/crash", {}));
  assert.deepEqual(await value.backend.inspectDispatch({
    threadId: binding.threadId, dispatchId: "mail-items-empty", task,
    expectedTurnId: accepted.turnId, maximumFinalUtf8Bytes: 20_000,
  }), {
    kind: "unavailable", threadId: binding.threadId, turnId: accepted.turnId,
    source: "persistent", code: "ACCEPTED_TURN_NOT_VISIBLE",
  });
});

test("OutcomeUnknown alone returns persistent not-found and discovers a timed-out start", async (t) => {
  const value = await harness(t, { requestTimeoutMs: 300 });
  const binding = await bind(value);
  const missing = await value.backend.inspectDispatch({
    threadId: binding.threadId, dispatchId: "missing", task: "never",
    expectedTurnId: null, maximumFinalUtf8Bytes: 20_000,
  });
  assert.deepEqual(missing, { kind: "not-found", threadId: binding.threadId, source: "persistent" });

  const task = "[HANG_TURN_START][NATURAL] exact task";
  await assert.rejects(start(value, binding.threadId, "mail-unknown", task), /timed out/);
  await delay(30);
  const recovered = await value.backend.inspectDispatch({
    threadId: binding.threadId, dispatchId: "mail-unknown", task,
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
    threadId: binding.threadId, dispatchId: "mail-restart", task,
    expectedTurnId: accepted.turnId, maximumFinalUtf8Bytes: 20_000,
  })).source, "live");
  await assert.rejects(value.client.request("test/crash", {}));
  assert.equal((await value.backend.status(binding.threadId)).status, "running");
  const cold = await value.backend.inspectDispatch({
    threadId: binding.threadId, dispatchId: "mail-restart", task,
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
    threadId: binding.threadId, dispatchId: "mail-first", task: firstTask,
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
  ["--missing-backwards-cursor", "PAGE_SHAPE_INVALID"],
  ["--unknown-item", "PAGE_SHAPE_INVALID"],
  ["--agent-missing-delivery", "PAGE_SHAPE_INVALID"],
  ["--file-change-missing-fields", "PAGE_SHAPE_INVALID"],
] as const) {
  test(`cold Accepted inspection fails closed for ${argument}`, async (t) => {
    const value = await harness(t, { persistent: true, fixtureArgs: [argument] });
    const binding = await bind(value);
    const task = "[LONG] exact task";
    const accepted = await start(value, binding.threadId, "mail-malformed", task);
    await assert.rejects(value.client.request("test/crash", {}));
    const result = await value.backend.inspectDispatch({
      threadId: binding.threadId, dispatchId: "mail-malformed", task,
      expectedTurnId: accepted.turnId, maximumFinalUtf8Bytes: 20_000,
    });
    assert.equal(result.kind, "ambiguous");
    if (result.kind === "ambiguous") assert.equal(result.code, code);
  });
}

test("inspection rejects a generation change between metadata and pagination", async (t) => {
  const value = await harness(t, {
    persistent: true,
    fixtureArgs: ["--signal-generation-change-after-metadata"],
  });
  const binding = await bind(value);
  const task = "[LONG] exact task";
  const accepted = await start(value, binding.threadId, "mail-generation", task);
  await assert.rejects(value.client.request("test/crash", {}));
  const unsubscribe = value.client.subscribe((notification) => {
    if (notification.method === "test/generationChanged") {
      (value.client as unknown as { appServerGeneration: number }).appServerGeneration += 1;
    }
  });
  t.after(unsubscribe);
  await assert.rejects(value.backend.inspectDispatch({
    threadId: binding.threadId, dispatchId: "mail-generation", task,
    expectedTurnId: accepted.turnId, maximumFinalUtf8Bytes: 20_000,
  }), (error: unknown) => typeof error === "object" && error !== null
    && "code" in error && error.code === "CODEX_PROTOCOL_ERROR");
});

test("inspection preflight rejects ownership drift before live evidence", async (t) => {
  const value = await harness(t);
  const binding = await bind(value);
  const task = "[LONG] exact task";
  const accepted = await start(value, binding.threadId, "mail-drift", task);
  await value.client.request("test/setThreadName", { threadId: binding.threadId, name: "not-owned" });
  const ownership = await value.backend.inspectDispatch({
    threadId: binding.threadId, dispatchId: "mail-drift", task,
    expectedTurnId: accepted.turnId, maximumFinalUtf8Bytes: 20_000,
  });
  assert.deepEqual(ownership, { kind: "ambiguous", threadId: binding.threadId, source: "persistent", code: "THREAD_OWNERSHIP_MISMATCH" });
});

for (const ignoreResumeCwd of [false, true]) {
  test(`same thread uses the new turn cwd with historical resume metadata (ignore override: ${ignoreResumeCwd})`, async (t) => {
    const value = await harness(t, {
      fixtureArgs: ["--preserve-resume-metadata", ...(ignoreResumeCwd ? ["--ignore-resume-cwd"] : [])],
    });
    const binding = await bind(value);
    const oldCwd = path.join(os.tmpdir(), "galatea-removed-old-directory");
    await start(value, binding.threadId, "home-warmup", "[EARLY][NATURAL] warmup");
    await value.client.request("test/setThreadCwd", { threadId: binding.threadId, cwd: oldCwd });
    const home = path.join(value.root, "new-home");
    await mkdir(home);
    const task = "[EARLY][NATURAL] new home task";
    const accepted = await value.backend.startBoundTurn({
      threadId: binding.threadId, cwd: home, dispatchId: "mail-home", task,
    });
    assert.equal(accepted.threadId, binding.threadId);
    const requests = await value.client.request<{
      lastResumeParams: { cwd: string };
      lastTurnParams: { cwd: string; sandboxPolicy?: unknown };
      threadStartCount: number;
      turnStartCount: number;
    }>("test/lastRequests", {});
    assert.equal(requests.lastResumeParams.cwd, home);
    assert.equal(requests.lastTurnParams.cwd, home);
    assert.equal(requests.lastTurnParams.sandboxPolicy, undefined);
    assert.equal(requests.threadStartCount, 1);
    assert.equal(requests.turnStartCount, 2);
    const metadata = await value.client.request<{ thread: { cwd: string } }>("thread/read", { threadId: binding.threadId, includeTurns: false });
    assert.equal(metadata.thread.cwd, oldCwd);
    const inspected = await value.backend.inspectDispatch({
      threadId: binding.threadId, dispatchId: "mail-home", task,
      expectedTurnId: accepted.turnId, maximumFinalUtf8Bytes: 20_000,
    });
    assert.equal(inspected.kind, "completed");
  });
}

test("live and persistent inspection survive a removed cwd outside current allowed roots without starting a turn", async (t) => {
  const value = await harness(t, { persistent: true });
  const binding = await bind(value);
  const task = "[EARLY][NATURAL] old completed task";
  const accepted = await start(value, binding.threadId, "mail-old-home", task);
  const outside = await mkdtemp(path.join(os.tmpdir(), "galatea-former-home-"));
  await value.client.request("test/setThreadCwd", { threadId: binding.threadId, cwd: outside });
  await rm(outside, { recursive: true });
  const request = {
    threadId: binding.threadId, dispatchId: "mail-old-home", task,
    expectedTurnId: accepted.turnId, maximumFinalUtf8Bytes: 20_000,
  };
  const live = await value.backend.inspectDispatch(request);
  assert.equal(live.kind, "completed");
  assert.equal(live.source, "live");
  await assert.rejects(value.client.request("test/crash", {}));
  for (const expectedTurnId of [accepted.turnId, null]) {
    const persistent = await value.backend.inspectDispatch({ ...request, expectedTurnId });
    assert.equal(persistent.kind, "completed");
    assert.equal(persistent.source, "persistent");
  }
  const requests = await value.client.request<{ turnStartCount: number; threadResumeCount: number }>("test/lastRequests", {});
  assert.equal(requests.turnStartCount, 0);
  assert.equal(requests.threadResumeCount, 0);
});

test("new invalid cwd is rejected before any Codex request", async (t) => {
  const value = await harness(t);
  const binding = await bind(value);
  const before = await value.client.request("test/lastRequests", {});
  for (const [cwd, code] of [[path.join(value.root, "missing"), "INVALID_CWD"], [os.tmpdir(), "CWD_NOT_ALLOWED"]]) {
    await assert.rejects(value.backend.startBoundTurn({
      threadId: binding.threadId, cwd: cwd!, dispatchId: "mail-invalid-home", task: "task",
    }), (error: unknown) => typeof error === "object" && error !== null && "code" in error && error.code === code);
  }
  assert.deepEqual(await value.client.request("test/lastRequests", {}), before);
});

test("two users share one backend while concurrent turns receive their own cwd without overriding native sandbox policy", async (t) => {
  const value = await harness(t);
  const homes = [path.join(value.root, "a"), path.join(value.root, "b")];
  await Promise.all(homes.map((home) => mkdir(home)));
  const bindings = await Promise.all(homes.map((cwd) => value.backend.ensureBinding({ cwd })));
  const accepted = await Promise.all(bindings.map((binding, index) => value.backend.startBoundTurn({
    threadId: binding.threadId, cwd: homes[index]!, dispatchId: `mail-user-${index}`, task: `[EARLY][NATURAL] user ${index}`,
  })));
  assert.notEqual(accepted[0]!.threadId, accepted[1]!.threadId);
  const requests = await value.client.request<{ allTurnParams: { threadId: string; cwd: string; sandboxPolicy?: unknown }[] }>("test/lastRequests", {});
  for (let index = 0; index < homes.length; index += 1) {
    const turn = requests.allTurnParams.find((request) => request.threadId === bindings[index]!.threadId)!;
    assert.equal(turn.cwd, homes[index]);
    assert.equal(turn.sandboxPolicy, undefined);
  }
});
