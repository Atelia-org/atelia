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
  const backend = new CodexBackend({
    client,
    pathPolicy: await PathPolicy.create([root], root),
    store: new TaskStore(20_000, 100),
    logger: new NullLogger(),
    profile: galateaCodexBackendProfile,
  });
  t.after(async () => {
    await backend.stop();
    await rm(root, { recursive: true });
  });
  return { root, client, backend, ...(lifecycleFile ? { lifecycleFile } : {}) };
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
