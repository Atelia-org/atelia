import assert from "node:assert/strict";
import { mkdir, mkdtemp, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test, { type TestContext } from "node:test";
import { fileURLToPath } from "node:url";
import { CodexBackend } from "../src/codex/backend.js";
import { CodexAppServerClient } from "../src/codex/client.js";
import { TaskStore } from "../src/codex/task-store.js";
import {
  GalateaCodexAdapter,
  galateaCodexBackendProfile,
} from "../src/galatea/adapter.js";
import type { GalateaDispatchFrame, GalateaOutputFrame } from "../src/galatea/protocol.js";
import { NullLogger } from "../src/logger.js";
import { PathPolicy } from "../src/security/paths.js";

const fixture = fileURLToPath(new URL("./fixtures/fake-app-server.js", import.meta.url));

interface Harness {
  adapter: GalateaCodexAdapter;
  client: CodexAppServerClient;
  frames: GalateaOutputFrame[];
  root: string;
}

async function harness(
  t: TestContext,
  options: {
    fixtureArgs?: string[];
    maxResultChars?: number;
    maxFinalBytes?: number;
    turnDeadlineMs?: number;
    requestTimeoutMs?: number;
    maxDispatchTombstones?: number;
  } = {},
): Promise<Harness> {
  const root = await mkdtemp(path.join(os.tmpdir(), "galatea-codex-adapter-"));
  const logger = new NullLogger();
  const client = new CodexAppServerClient({
    command: process.execPath,
    args: [fixture, ...(options.fixtureArgs ?? [])],
    requestTimeoutMs: options.requestTimeoutMs ?? 1_000,
    logger,
  });
  const store = new TaskStore(options.maxResultChars ?? 20_000, 100);
  const backend = new CodexBackend({
    client,
    pathPolicy: await PathPolicy.create([root], root),
    store,
    logger,
    profile: galateaCodexBackendProfile,
  });
  const frames: GalateaOutputFrame[] = [];
  const adapter = new GalateaCodexAdapter({
    backend,
    store,
    logger,
    cwd: root,
    mode: "work",
    network: false,
    turnDeadlineMs: options.turnDeadlineMs ?? 1_000,
    interruptGraceMs: 100,
    maxFinalBytes: options.maxFinalBytes ?? 20_000,
    maxOutputFrameBytes: 100_000,
    maxDispatchTombstones: options.maxDispatchTombstones ?? 100,
    write: async (frame) => { frames.push(frame); },
  });
  t.after(async () => {
    await adapter.stop();
    await rm(root, { recursive: true });
  });
  return { adapter, client, frames, root };
}

function dispatch(
  dispatchId: string,
  task: string,
  threadId?: string,
): GalateaDispatchFrame {
  return {
    v: 1,
    type: "dispatch",
    requestId: `request-${dispatchId}`,
    dispatchId,
    ...(threadId ? { threadId } : {}),
    task,
  };
}

test("Galatea adapter keeps two natural Markdown replies on one owned thread without output schema", async (t) => {
  const value = await harness(t);
  await value.adapter.dispatch(dispatch("mail-1", "[NATURAL] first task"));
  const firstAccepted = value.frames[0];
  const firstCompleted = value.frames[1];
  assert.equal(firstAccepted?.type, "accepted");
  assert.equal(firstCompleted?.type, "completed");
  if (firstAccepted?.type !== "accepted" || firstCompleted?.type !== "completed") return;
  assert.match(firstCompleted.final, /```ts/);
  assert.match(firstCompleted.final, /~~~ fence/);

  await value.adapter.dispatch(dispatch("mail-2", "[NATURAL] second task", firstAccepted.threadId));
  const secondAccepted = value.frames[2];
  assert.equal(secondAccepted?.type, "accepted");
  if (secondAccepted?.type !== "accepted") return;
  assert.equal(secondAccepted.threadId, firstAccepted.threadId);

  const requests = await value.client.request<{
    lastThreadStartParams: Record<string, unknown>;
    lastResumeParams: Record<string, unknown>;
    allTurnParams: Record<string, unknown>[];
  }>("test/lastRequests", {});
  assert.equal(requests.lastThreadStartParams.serviceName, "atelia_galatea_codex_sidecar");
  assert.equal(requests.lastThreadStartParams.threadSource, "atelia-galatea-codex-sidecar");
  assert.match(String(requests.lastResumeParams.developerInstructions), /Galatea's persistent delegate/);
  assert.equal(requests.allTurnParams.length, 2);
  assert.equal(requests.allTurnParams[0]?.clientUserMessageId, "mail-1");
  assert.equal(requests.allTurnParams[1]?.clientUserMessageId, "mail-2");
  assert.equal(Object.hasOwn(requests.allTurnParams[0] ?? {}, "outputSchema"), false);
  assert.equal(Object.hasOwn(requests.allTurnParams[1] ?? {}, "outputSchema"), false);

  const mcpBackend = new CodexBackend({
    client: value.client,
    pathPolicy: await PathPolicy.create([value.root], value.root),
    store: new TaskStore(2_000, 100),
    logger: new NullLogger(),
  });
  await assert.rejects(
    mcpBackend.status(firstAccepted.threadId),
    (error: unknown) =>
      typeof error === "object" && error !== null && "code" in error && error.code === "THREAD_NOT_FOUND",
  );
  const mcpThread = await mcpBackend.delegate({
    task: "MCP-owned task",
    cwd: value.root,
    mode: "research",
    network: false,
    waitMs: 1_000,
  });
  await value.adapter.dispatch(dispatch("cross-profile", "must be rejected", mcpThread.threadId));
  const rejected = value.frames.at(-1);
  assert.equal(rejected?.type === "failed" && rejected.code, "THREAD_NOT_FOUND");
});

test("early legacy final is retained but accepted is emitted before completed", async (t) => {
  const value = await harness(t);
  await value.adapter.dispatch(dispatch("early-1", "[EARLY][LEGACY][NATURAL] task"));
  assert.deepEqual(value.frames.map((frame) => frame.type), ["accepted", "completed"]);
  const completed = value.frames[1];
  assert.equal(completed?.type, "completed");
  if (completed?.type === "completed") assert.match(completed.final, /事情已经办妥/);
});

test("missing, truncated, failed, and oversized finals map to stable terminal failures", async (t) => {
  await t.test("missing", async (t) => {
    const value = await harness(t);
    await value.adapter.dispatch(dispatch("missing-1", "[MISSING] task"));
    assert.deepEqual(value.frames.map((frame) => frame.type), ["accepted", "failed"]);
    assert.equal(value.frames[1]?.type === "failed" && value.frames[1].code, "FINAL_MISSING");
  });
  await t.test("truncated", async (t) => {
    const value = await harness(t, { maxResultChars: 64 });
    await value.adapter.dispatch(dispatch("truncated-1", "[OVERSIZE] task"));
    assert.equal(value.frames[1]?.type === "failed" && value.frames[1].code, "FINAL_TRUNCATED");
  });
  await t.test("failed", async (t) => {
    const value = await harness(t);
    await value.adapter.dispatch(dispatch("failed-1", "[FAIL] task"));
    assert.equal(value.frames[1]?.type === "failed" && value.frames[1].code, "TURN_FAILED");
  });
  await t.test("byte limit", async (t) => {
    const value = await harness(t, { maxFinalBytes: 16 });
    await value.adapter.dispatch(dispatch("large-1", "[NATURAL] task"));
    assert.equal(value.frames[1]?.type === "failed" && value.frames[1].code, "FINAL_TOO_LARGE");
  });
});

test("turn deadline requests interruption and returns TURN_TIMEOUT", async (t) => {
  const value = await harness(t, { turnDeadlineMs: 30 });
  await value.adapter.dispatch(dispatch("timeout-1", "[LONG] task"));
  assert.deepEqual(value.frames.map((frame) => frame.type), ["accepted", "failed"]);
  assert.equal(value.frames[1]?.type === "failed" && value.frames[1].code, "TURN_TIMEOUT");
});

test("app-server process exit after acceptance resolves as a stable failure", async (t) => {
  const value = await harness(t);
  await value.adapter.dispatch(dispatch("crash-1", "[CRASH] task"));
  assert.deepEqual(value.frames.map((frame) => frame.type), ["accepted", "failed"]);
  assert.equal(value.frames[1]?.type === "failed" && value.frames[1].code, "TURN_FAILED");
});

test("duplicate dispatchId is tombstoned before await and never starts a second thread or turn", async (t) => {
  const value = await harness(t);
  const first = value.adapter.dispatch(dispatch("duplicate-1", "[NATURAL] original"));
  const concurrentDuplicate = value.adapter.dispatch(dispatch("duplicate-1", "must not run"));
  await Promise.all([first, concurrentDuplicate]);
  await value.adapter.dispatch(dispatch("duplicate-1", "must still not run"));

  const failures = value.frames.filter((frame) => frame.type === "failed");
  assert.equal(failures.length, 2);
  assert.ok(failures.every((frame) => frame.code === "DUPLICATE_DISPATCH_ID"));
  const counts = await value.client.request<{ threadStartCount: number; turnStartCount: number }>(
    "test/lastRequests",
    {},
  );
  assert.equal(counts.threadStartCount, 1);
  assert.equal(counts.turnStartCount, 1);
});

test("dispatch tombstone capacity fails closed without evicting an older identity", async (t) => {
  const value = await harness(t, { maxDispatchTombstones: 1 });
  await value.adapter.dispatch(dispatch("capacity-1", "[NATURAL] first"));
  await value.adapter.dispatch(dispatch("capacity-2", "must not run"));
  await value.adapter.dispatch(dispatch("capacity-1", "old identity stays tombstoned"));

  const lastTwo = value.frames.slice(-2);
  assert.equal(lastTwo[0]?.type === "failed" && lastTwo[0].code, "DISPATCH_CAPACITY_EXCEEDED");
  assert.equal(lastTwo[1]?.type === "failed" && lastTwo[1].code, "DUPLICATE_DISPATCH_ID");
  const counts = await value.client.request<{ threadStartCount: number; turnStartCount: number }>(
    "test/lastRequests",
    {},
  );
  assert.equal(counts.threadStartCount, 1);
  assert.equal(counts.turnStartCount, 1);
});

test("continuation rejects persisted cwd drift even when both directories are allowed", async (t) => {
  const value = await harness(t);
  const drifted = path.join(value.root, "still-allowed");
  await mkdir(drifted);
  await value.adapter.dispatch(dispatch("cwd-1", "[NATURAL] first"));
  const accepted = value.frames[0];
  assert.equal(accepted?.type, "accepted");
  if (accepted?.type !== "accepted") return;

  await value.client.request("test/setThreadCwd", { threadId: accepted.threadId, cwd: drifted });
  await value.adapter.dispatch(dispatch("cwd-2", "must not run", accepted.threadId));
  const failed = value.frames.at(-1);
  assert.equal(failed?.type === "failed" && failed.code, "CWD_MISMATCH");
  const counts = await value.client.request<{ turnStartCount: number }>("test/lastRequests", {});
  assert.equal(counts.turnStartCount, 1);
});

test("side-effecting start RPC timeout is outcome-unknown and tombstoned without retry", async (t) => {
  const value = await harness(t, { requestTimeoutMs: 300 });
  await value.adapter.dispatch(dispatch("unknown-1", "[HANG_TURN_START] task"));
  const first = value.frames.at(-1);
  assert.equal(first?.type === "failed" && first.code, "START_OUTCOME_UNKNOWN");

  await value.adapter.dispatch(dispatch("unknown-1", "must not retry"));
  const duplicate = value.frames.at(-1);
  assert.equal(duplicate?.type === "failed" && duplicate.code, "DUPLICATE_DISPATCH_ID");
  const counts = await value.client.request<{ threadStartCount: number; turnStartCount: number }>(
    "test/lastRequests",
    {},
  );
  assert.equal(counts.threadStartCount, 1);
  assert.equal(counts.turnStartCount, 1);
});
