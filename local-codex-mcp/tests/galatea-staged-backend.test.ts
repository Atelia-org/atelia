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

async function harness(
  t: TestContext,
  options: {
    requestTimeoutMs?: number;
    fixtureArgs?: string[];
    persistentFixture?: boolean;
  } = {},
) {
  const root = await mkdtemp(path.join(os.tmpdir(), "galatea-staged-backend-"));
  const lifecycleFile = options.persistentFixture ? path.join(root, "lifecycle.log") : undefined;
  const stateFile = options.persistentFixture ? path.join(root, "state.json") : undefined;
  const client = new CodexAppServerClient({
    command: process.execPath,
    args: [
      fixture,
      ...(options.fixtureArgs ?? []),
      ...(lifecycleFile ? [`--lifecycle-file=${lifecycleFile}`] : []),
      ...(stateFile ? [`--state-file=${stateFile}`] : []),
    ],
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
  });
  t.after(async () => {
    await backend.stop();
    await rm(root, { recursive: true });
  });
  return { root, client, backend, store, ...(lifecycleFile ? { lifecycleFile } : {}) };
}

type Harness = Awaited<ReturnType<typeof harness>>;

async function startLongTurn(
  value: Harness,
  dispatchId: string,
  task = `[LONG][NATURAL] ${dispatchId}`,
) {
  const binding = await value.backend.ensureBinding({
    cwd: value.root,
    mode: "work",
    tools,
  });
  const accepted = await value.backend.startBoundTurn({
    threadId: binding.threadId,
    expectedCwd: value.root,
    dispatchId,
    task,
    mode: "work",
    localCommandNetwork: false,
    tools,
  });
  return { ...accepted, dispatchId, task };
}

async function inspectLongTurn(
  value: Harness,
  turn: Awaited<ReturnType<typeof startLongTurn>>,
) {
  return value.backend.inspectDispatch({
    threadId: turn.threadId,
    expectedCwd: value.root,
    dispatchId: turn.dispatchId,
    task: turn.task,
    maximumFinalUtf8Bytes: 20_000,
  });
}

async function readThreadReadTrace(value: Harness) {
  return value.client.request<{
    threadReadCount: number;
    threadReadIncludeTurns: boolean[];
  }>("test/lastRequests", {});
}

async function beginBlockedMetadataInspection(
  value: Harness,
  turn: Awaited<ReturnType<typeof startLongTurn>>,
) {
  await value.client.request("test/blockNextMetadataRead", {});
  const inspection = inspectLongTurn(value, turn);
  await value.client.request("test/waitForMetadataReadBarrier", {});
  return { inspection };
}

test("ensureBinding establishes and verifies an empty owned thread without starting a turn", async (t) => {
  const value = await harness(t);
  const binding = await value.backend.ensureBinding({
    cwd: value.root,
    mode: "work",
    tools,
  });
  const thread = await value.client.request<{
    thread: { id: string; name: string | null; cwd: string; turns: unknown[] };
  }>("thread/read", { threadId: binding.threadId, includeTurns: true });
  assert.equal(thread.thread.id, binding.threadId);
  assert.equal(
    thread.thread.name,
    `[galatea-codex-sidecar] ${binding.threadId}`,
  );
  assert.equal(thread.thread.cwd, value.root);
  assert.deepEqual(thread.thread.turns, []);

  const counts = await value.client.request<{
    threadStartCount: number;
    threadNameSetCount: number;
    threadResumeCount: number;
    turnStartCount: number;
  }>("test/lastRequests", {});
  assert.equal(counts.threadStartCount, 1);
  assert.equal(counts.threadNameSetCount, 1);
  assert.equal(counts.threadResumeCount, 0);
  assert.equal(counts.turnStartCount, 0);
});

test("startBoundTurn uses the known binding and inspectDispatch reads exact persisted state", async (t) => {
  const value = await harness(t);
  const binding = await value.backend.ensureBinding({
    cwd: value.root,
    mode: "work",
    tools,
  });
  const accepted = await value.backend.startBoundTurn({
    threadId: binding.threadId,
    expectedCwd: value.root,
    dispatchId: "mail-1",
    task: "[NATURAL] exact task",
    mode: "work",
    localCommandNetwork: false,
    tools,
  });
  assert.equal(accepted.threadId, binding.threadId);
  assert.match(accepted.turnId, /^turn-/);
  await delay(30);

  const inspection = await value.backend.inspectDispatch({
    threadId: binding.threadId,
    expectedCwd: value.root,
    dispatchId: "mail-1",
    task: "[NATURAL] exact task",
    maximumFinalUtf8Bytes: 20_000,
  });
  assert.equal(inspection.kind, "completed");
  if (inspection.kind === "completed") {
    assert.equal(inspection.turnId, accepted.turnId);
    assert.match(inspection.final, /事情已经办妥/);
  }

  const notFound = await value.backend.inspectDispatch({
    threadId: binding.threadId,
    expectedCwd: value.root,
    dispatchId: "mail-missing",
    task: "never sent",
    maximumFinalUtf8Bytes: 20_000,
  });
  assert.deepEqual(notFound, {
    kind: "not-found",
    threadId: binding.threadId,
  });

  const requests = await value.client.request<{
    threadStartCount: number;
    threadResumeCount: number;
    turnStartCount: number;
    lastThreadStartParams: Record<string, unknown>;
    lastResumeParams: Record<string, unknown>;
    lastTurnParams: { clientUserMessageId: string; input: unknown };
    allTurnParams: Record<string, unknown>[];
  }>("test/lastRequests", {});
  assert.equal(requests.threadStartCount, 1);
  assert.equal(requests.threadResumeCount, 1);
  assert.equal(requests.turnStartCount, 1);
  assert.equal(requests.lastThreadStartParams.serviceName, "atelia_galatea_codex_sidecar");
  assert.equal(requests.lastThreadStartParams.threadSource, "atelia-galatea-codex-sidecar");
  assert.deepEqual(requests.lastThreadStartParams.config, {
    web_search: "live",
    features: { image_generation: true },
    tools: { view_image: true },
  });
  assert.match(String(requests.lastResumeParams.developerInstructions), /Galatea's persistent delegate/);
  assert.equal(requests.lastTurnParams.clientUserMessageId, "mail-1");
  assert.deepEqual(requests.lastTurnParams.input, [{
    type: "text",
    text: "[NATURAL] exact task",
    text_elements: [],
  }]);
  assert.equal(Object.hasOwn(requests.allTurnParams[0] ?? {}, "outputSchema"), false);
});

test("validated running dispatches use metadata until terminal inspection", async (t) => {
  const value = await harness(t);
  const turn = await startLongTurn(value, "long-mail");
  const before = await readThreadReadTrace(value);

  const first = await inspectLongTurn(value, turn);
  const second = await inspectLongTurn(value, turn);

  assert.deepEqual(first, {
    kind: "running",
    threadId: turn.threadId,
    turnId: turn.turnId,
  });
  assert.deepEqual(second, first);
  const runningTrace = await readThreadReadTrace(value);
  assert.deepEqual(
    runningTrace.threadReadIncludeTurns.slice(before.threadReadCount),
    [true, false],
  );

  await value.client.request("turn/interrupt", {
    threadId: turn.threadId,
    turnId: turn.turnId,
  });
  const settled = await value.store.waitForTurn(
    turn.threadId,
    turn.turnId,
    1_000,
  );
  assert.equal(settled.status, "interrupted");
  const beforeTerminal = await readThreadReadTrace(value);

  const terminal = await inspectLongTurn(value, turn);

  assert.deepEqual(terminal, {
    kind: "failed",
    threadId: turn.threadId,
    turnId: turn.turnId,
    code: "TURN_INTERRUPTED",
  });
  const terminalTrace = await readThreadReadTrace(value);
  assert.deepEqual(
    terminalTrace.threadReadIncludeTurns.slice(beforeTerminal.threadReadCount),
    [true],
  );
});

test("completion before turn start response cannot resurrect live running proof", async (t) => {
  const value = await harness(t);
  const binding = await value.backend.ensureBinding({
    cwd: value.root,
    mode: "work",
    tools,
  });
  const task = "[EARLY][NATURAL] completes before acceptance";

  const accepted = await value.backend.startBoundTurn({
    threadId: binding.threadId,
    expectedCwd: value.root,
    dispatchId: "early-completion-mail",
    task,
    mode: "work",
    localCommandNetwork: false,
    tools,
  });

  const terminalRuntime = value.store.snapshot(binding.threadId);
  assert.equal(terminalRuntime.status, "completed");
  assert.equal(terminalRuntime.activeTurnId, undefined);
  assert.equal(terminalRuntime.latestTurnId, accepted.turnId);
  const before = await readThreadReadTrace(value);
  const request = {
    threadId: binding.threadId,
    expectedCwd: value.root,
    dispatchId: "early-completion-mail",
    task,
    maximumFinalUtf8Bytes: 20_000,
  };

  const first = await value.backend.inspectDispatch(request);
  const second = await value.backend.inspectDispatch(request);

  assert.equal(first.kind, "completed");
  assert.equal(second.kind, "completed");
  if (first.kind === "completed" && second.kind === "completed") {
    assert.equal(first.turnId, accepted.turnId);
    assert.equal(second.turnId, accepted.turnId);
    assert.equal(second.final, first.final);
    assert.match(first.final, /事情已经办妥/);
  }
  const after = await readThreadReadTrace(value);
  assert.deepEqual(
    after.threadReadIncludeTurns.slice(before.threadReadCount),
    [true, true],
  );
});

test("terminal notification during metadata read trips the second runtime fence", async (t) => {
  const value = await harness(t);
  const turn = await startLongTurn(value, "in-flight-terminal-mail");
  assert.equal((await inspectLongTurn(value, turn)).kind, "running");
  const before = await readThreadReadTrace(value);
  const blocked = await beginBlockedMetadataInspection(value, turn);

  await value.client.request("turn/interrupt", {
    threadId: turn.threadId,
    turnId: turn.turnId,
  });
  assert.equal(
    (await value.store.waitForTurn(turn.threadId, turn.turnId, 1_000)).status,
    "interrupted",
  );
  await value.client.request("test/releaseMetadataRead", {});
  const inspection = await blocked.inspection;

  assert.deepEqual(inspection, {
    kind: "failed",
    threadId: turn.threadId,
    turnId: turn.turnId,
    code: "TURN_INTERRUPTED",
  });
  const after = await readThreadReadTrace(value);
  assert.deepEqual(
    after.threadReadIncludeTurns.slice(before.threadReadCount),
    [false, true],
  );
});

test("cache input mismatch falls back to exact full classification", async (t) => {
  const value = await harness(t);
  const turn = await startLongTurn(value, "input-mismatch-mail");
  assert.equal((await inspectLongTurn(value, turn)).kind, "running");
  const before = await readThreadReadTrace(value);

  const inspection = await inspectLongTurn(value, {
    ...turn,
    task: `${turn.task} changed`,
  });

  assert.deepEqual(inspection, {
    kind: "ambiguous",
    threadId: turn.threadId,
    code: "DISPATCH_BODY_MISMATCH",
  });
  const after = await readThreadReadTrace(value);
  assert.deepEqual(
    after.threadReadIncludeTurns.slice(before.threadReadCount),
    [true],
  );
});

test("new active turn evicts the old running proof and prevents recaching it", async (t) => {
  const value = await harness(t);
  const turn = await startLongTurn(value, "old-active-mail");
  assert.equal((await inspectLongTurn(value, turn)).kind, "running");
  const before = await readThreadReadTrace(value);
  const blocked = await beginBlockedMetadataInspection(value, turn);
  await value.client.request("turn/start", {
    threadId: turn.threadId,
    clientUserMessageId: "external-new-turn",
    input: [{
      type: "text",
      text: "[LONG][STARTED_BEFORE_RESPONSE] external",
      text_elements: [],
    }],
    cwd: value.root,
  });
  await value.client.request("test/releaseMetadataRead", {});

  assert.equal((await blocked.inspection).kind, "running");
  assert.equal((await inspectLongTurn(value, turn)).kind, "running");

  const after = await readThreadReadTrace(value);
  assert.deepEqual(
    after.threadReadIncludeTurns.slice(before.threadReadCount),
    [false, true, true],
  );
});

test("oversize running task safely declines cache admission", async (t) => {
  const value = await harness(t);
  const task = `[LONG]${"界".repeat(50_000)}`;
  assert.ok(Buffer.byteLength(task, "utf8") > 128 * 1024);
  const turn = await startLongTurn(value, "oversize-cache-mail", task);
  const before = await readThreadReadTrace(value);

  assert.equal((await inspectLongTurn(value, turn)).kind, "running");
  assert.equal((await inspectLongTurn(value, turn)).kind, "running");

  const after = await readThreadReadTrace(value);
  assert.deepEqual(
    after.threadReadIncludeTurns.slice(before.threadReadCount),
    [true, true],
  );
});

test("running cache entry capacity fails open without evicting verified entries", async (t) => {
  const value = await harness(t);
  const turns: Awaited<ReturnType<typeof startLongTurn>>[] = [];
  for (let index = 0; index < 33; index += 1) {
    const turn = await startLongTurn(value, `capacity-mail-${index}`);
    assert.equal((await inspectLongTurn(value, turn)).kind, "running");
    turns.push(turn);
  }
  const before = await readThreadReadTrace(value);

  assert.equal((await inspectLongTurn(value, turns[32]!)).kind, "running");
  assert.equal((await inspectLongTurn(value, turns[0]!)).kind, "running");
  await value.client.request("turn/interrupt", {
    threadId: turns[0]!.threadId,
    turnId: turns[0]!.turnId,
  });
  assert.equal(
    (await value.store.waitForTurn(
      turns[0]!.threadId,
      turns[0]!.turnId,
      1_000,
    )).status,
    "interrupted",
  );
  assert.equal((await inspectLongTurn(value, turns[32]!)).kind, "running");
  assert.equal((await inspectLongTurn(value, turns[32]!)).kind, "running");

  const after = await readThreadReadTrace(value);
  assert.deepEqual(
    after.threadReadIncludeTurns.slice(before.threadReadCount),
    [true, false, true, false],
  );
});

test("running metadata fast path still rejects ownership drift", async (t) => {
  const value = await harness(t);
  const turn = await startLongTurn(value, "ownership-drift-mail");
  assert.equal((await inspectLongTurn(value, turn)).kind, "running");
  await value.client.request("test/setThreadName", {
    threadId: turn.threadId,
    name: "not-owned",
  });
  const before = await readThreadReadTrace(value);

  const inspection = await inspectLongTurn(value, turn);

  assert.deepEqual(inspection, {
    kind: "ambiguous",
    threadId: turn.threadId,
    code: "THREAD_OWNERSHIP_MISMATCH",
  });
  const after = await readThreadReadTrace(value);
  assert.deepEqual(
    after.threadReadIncludeTurns.slice(before.threadReadCount),
    [false],
  );
});

test("running metadata fast path still rejects cwd drift", async (t) => {
  const value = await harness(t);
  const turn = await startLongTurn(value, "cwd-drift-mail");
  assert.equal((await inspectLongTurn(value, turn)).kind, "running");
  await value.client.request("test/setThreadCwd", {
    threadId: turn.threadId,
    cwd: os.tmpdir(),
  });
  const before = await readThreadReadTrace(value);

  const inspection = await inspectLongTurn(value, turn);

  assert.deepEqual(inspection, {
    kind: "ambiguous",
    threadId: turn.threadId,
    code: "THREAD_CWD_MISMATCH",
  });
  const after = await readThreadReadTrace(value);
  assert.deepEqual(
    after.threadReadIncludeTurns.slice(before.threadReadCount),
    [false],
  );
});

test("app-server restart discards running cache and performs full inspection", async (t) => {
  const value = await harness(t, { persistentFixture: true });
  const turn = await startLongTurn(value, "restart-running-mail");
  assert.equal((await inspectLongTurn(value, turn)).kind, "running");
  assert.equal((await inspectLongTurn(value, turn)).kind, "running");

  await assert.rejects(value.client.request("test/crash", {}));
  const hydrated = await value.backend.status(turn.threadId);
  assert.equal(hydrated.status, "running");
  const beforeRecoveredInspection = await readThreadReadTrace(value);

  const recovered = await inspectLongTurn(value, turn);
  const repeated = await inspectLongTurn(value, turn);

  assert.deepEqual(recovered, {
    kind: "running",
    threadId: turn.threadId,
    turnId: turn.turnId,
  });
  assert.deepEqual(repeated, recovered);
  const restartedTrace = await readThreadReadTrace(value);
  assert.deepEqual(
    restartedTrace.threadReadIncludeTurns.slice(
      beforeRecoveredInspection.threadReadCount,
    ),
    [true, true],
  );
});

test("missing dispatch inspections do not populate runtime task state", async (t) => {
  const value = await harness(t);
  assert.equal(value.store.threadCountForTest, 0);

  for (let index = 0; index < 128; index += 1) {
    const threadId = `missing-thread-${index}`;
    assert.deepEqual(await value.backend.inspectDispatch({
      threadId,
      expectedCwd: value.root,
      dispatchId: `missing-dispatch-${index}`,
      task: "never sent",
      maximumFinalUtf8Bytes: 20_000,
    }), {
      kind: "ambiguous",
      threadId,
      code: "THREAD_NOT_FOUND",
    });
  }

  assert.equal(value.store.threadCountForTest, 0);
});

test("startBoundTurn accepts an exact resume response that omits its optional name", async (t) => {
  const value = await harness(t, {
    fixtureArgs: ["--drop-name-on-resume"],
  });
  const binding = await value.backend.ensureBinding({
    cwd: value.root,
    mode: "work",
    tools,
  });

  const beforeStart = await value.backend.inspectDispatch({
    threadId: binding.threadId,
    expectedCwd: value.root,
    dispatchId: "empty-thread-mail",
    task: "exact task",
    maximumFinalUtf8Bytes: 20_000,
  });
  assert.deepEqual(beforeStart, {
    kind: "not-found",
    threadId: binding.threadId,
  });

  await value.client.request("test/setResumeResponseThreadId", {
    threadId: "different-thread",
  });
  await assert.rejects(
    value.backend.startBoundTurn({
      threadId: binding.threadId,
      expectedCwd: value.root,
      dispatchId: "wrong-resume",
      task: "must reject a mismatched resume response",
      mode: "work",
      localCommandNetwork: false,
      tools,
    }),
    (error: unknown) => typeof error === "object"
      && error !== null
      && "code" in error
      && error.code === "THREAD_NOT_FOUND",
  );

  const accepted = await value.backend.startBoundTurn({
    threadId: binding.threadId,
    expectedCwd: value.root,
    dispatchId: "empty-thread-mail",
    task: "exact task",
    mode: "work",
    localCommandNetwork: false,
    tools,
  });
  assert.equal(accepted.threadId, binding.threadId);
  assert.match(accepted.turnId, /^turn-/);
});

test("inspectDispatch reconciles a persisted turn after turn/start response timeout without retry", async (t) => {
  const value = await harness(t, { requestTimeoutMs: 300 });
  const binding = await value.backend.ensureBinding({
    cwd: value.root,
    mode: "work",
    tools,
  });
  await assert.rejects(value.backend.startBoundTurn({
    threadId: binding.threadId,
    expectedCwd: value.root,
    dispatchId: "mail-unknown",
    task: "[HANG_TURN_START][NATURAL] exact task",
    mode: "work",
    localCommandNetwork: false,
    tools,
  }));

  const inspection = await value.backend.inspectDispatch({
    threadId: binding.threadId,
    expectedCwd: value.root,
    dispatchId: "mail-unknown",
    task: "[HANG_TURN_START][NATURAL] exact task",
    maximumFinalUtf8Bytes: 20_000,
  });
  assert.equal(inspection.kind, "completed");
  const counts = await value.client.request<{ turnStartCount: number }>(
    "test/lastRequests",
    {},
  );
  assert.equal(counts.turnStartCount, 1);
});

test("app-server restart reauthenticates and continues the exact persisted Galatea thread", async (t) => {
  const value = await harness(t, {
    fixtureArgs: ["--drop-persisted-thread-source"],
    persistentFixture: true,
  });
  const binding = await value.backend.ensureBinding({
    cwd: value.root,
    mode: "work",
    tools,
  });
  await value.backend.startBoundTurn({
    threadId: binding.threadId,
    expectedCwd: value.root,
    dispatchId: "restart-mail-1",
    task: "[NATURAL] first",
    mode: "work",
    localCommandNetwork: false,
    tools,
  });

  await assert.rejects(value.client.request("test/crash", {}));

  const second = await value.backend.startBoundTurn({
    threadId: binding.threadId,
    expectedCwd: value.root,
    dispatchId: "restart-mail-2",
    task: "[NATURAL] second",
    mode: "work",
    localCommandNetwork: false,
    tools,
  });
  assert.equal(second.threadId, binding.threadId);

  const persisted = await value.client.request<{
    thread: { id: string; name: string | null; status: { type: string } };
  }>("thread/read", { threadId: binding.threadId, includeTurns: false });
  assert.equal(persisted.thread.id, binding.threadId);
  assert.equal(persisted.thread.name, `[galatea-codex-sidecar] ${binding.threadId}`);
  assert.equal(persisted.thread.status.type, "notLoaded");

  assert.ok(value.lifecycleFile);
  const lifecycle = await readFile(value.lifecycleFile, "utf8");
  const lines = lifecycle.split("\n");
  const starts = lines.filter((line) => line.startsWith("start:"));
  assert.equal(starts.length, 2);
  assert.equal(lines.filter((line) => line.startsWith("exit:")).length, 1);
  const restartedPid = starts[1]?.slice("start:".length);
  assert.ok(restartedPid);
  assert.deepEqual(
    lines
      .filter((line) => line.startsWith(`rpc:${restartedPid}:`))
      .map((line) => line.slice(`rpc:${restartedPid}:`.length))
      .slice(0, 5),
    ["initialize", "account/read", "thread/read", "thread/resume", "turn/start"],
  );
});
