import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import test, { type TestContext } from "node:test";
import { fileURLToPath } from "node:url";
import { CodexBackend } from "../src/codex/backend.js";
import { CodexAppServerClient } from "../src/codex/client.js";
import { TaskStore } from "../src/codex/task-store.js";
import { galateaCodexBackendProfile } from "../src/galatea/adapter.js";
import { NullLogger } from "../src/logger.js";
import { PathPolicy } from "../src/security/paths.js";

const fixture = fileURLToPath(new URL("./fixtures/fake-app-server.js", import.meta.url));
const tools = { webSearch: "live", imageGeneration: true, viewImage: true } as const;

async function harness(
  t: TestContext,
  requestTimeoutMs = 1_000,
) {
  const root = await mkdtemp(path.join(os.tmpdir(), "galatea-staged-backend-"));
  const client = new CodexAppServerClient({
    command: process.execPath,
    args: [fixture],
    requestTimeoutMs,
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
  return { root, client, backend };
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
    lastTurnParams: { clientUserMessageId: string; input: unknown };
  }>("test/lastRequests", {});
  assert.equal(requests.threadStartCount, 1);
  assert.equal(requests.threadResumeCount, 1);
  assert.equal(requests.turnStartCount, 1);
  assert.equal(requests.lastTurnParams.clientUserMessageId, "mail-1");
  assert.deepEqual(requests.lastTurnParams.input, [{
    type: "text",
    text: "[NATURAL] exact task",
    text_elements: [],
  }]);
});

test("inspectDispatch reconciles a persisted turn after turn/start response timeout without retry", async (t) => {
  const value = await harness(t, 300);
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
