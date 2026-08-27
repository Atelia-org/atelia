import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { CodexBackend } from "../src/codex/backend.js";
import { CodexAppServerClient } from "../src/codex/client.js";
import { TaskStore } from "../src/codex/task-store.js";
import { NullLogger, type BridgeLogger, type LogLevel } from "../src/logger.js";
import { PathPolicy } from "../src/security/paths.js";

const fixture = fileURLToPath(new URL("./fixtures/fake-app-server.js", import.meta.url));

class RecordingLogger implements BridgeLogger {
  readonly entries: Array<{ level: LogLevel; event: string }> = [];

  log(level: LogLevel, event: string): void {
    this.entries.push({ level, event });
  }
}

async function waitForProcessExit(pid: number, timeoutMs = 1_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      process.kill(pid, 0);
    } catch (error) {
      if (typeof error === "object" && error !== null && "code" in error && error.code === "ESRCH") return;
      throw error;
    }
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  assert.fail(`process ${pid} did not exit within ${timeoutMs}ms`);
}

test("backend completes, continues, returns bounded data, and interrupts", async (t) => {
  const root = await mkdtemp(path.join(os.tmpdir(), "codex-bridge-backend-"));
  const logger = new NullLogger();
  const client = new CodexAppServerClient({
    command: process.execPath,
    args: [fixture, "--drop-persisted-thread-source"],
    requestTimeoutMs: 1_000,
    logger,
  });
  const backend = new CodexBackend({
    client,
    pathPolicy: await PathPolicy.create([root]),
    store: new TaskStore(2_000, 100),
    logger,
  });
  t.after(async () => {
    await backend.stop();
    const { rm } = await import("node:fs/promises");
    await rm(root, { recursive: true });
  });

  const first = await backend.delegate({
    task: "inspect",
    cwd: root,
    mode: "research",
    network: false,
    waitMs: 1_000,
  });
  assert.equal(first.status, "completed");
  assert.deepEqual(first.changedFiles, ["hello.txt"]);
  assert.match(first.result ?? "", /Fake task completed/);
  assert.doesNotMatch(first.result ?? "", /secret diff/);
  const researchRequest = await client.request<{
    lastThreadStartParams: { serviceName: string; threadSource: string };
    lastTurnParams: { approvalPolicy: string; approvalsReviewer: string; sandboxPolicy: { type: string; networkAccess: boolean }; outputSchema: unknown };
  }>("test/lastRequests", {});
  assert.equal(researchRequest.lastThreadStartParams.serviceName, "atelia_local_codex_mcp");
  assert.equal(researchRequest.lastThreadStartParams.threadSource, "atelia-local-codex-mcp");
  assert.equal(typeof researchRequest.lastTurnParams.outputSchema, "object");
  assert.equal(researchRequest.lastTurnParams.approvalPolicy, "never");
  assert.equal(researchRequest.lastTurnParams.approvalsReviewer, "user");
  assert.deepEqual(researchRequest.lastTurnParams.sandboxPolicy, {
    type: "readOnly",
    networkAccess: false,
  });
  const persisted = await client.request<{
    thread: {
      id: string;
      name: string | null;
      source: string;
      threadSource: string | null;
      status: { type: string };
    };
  }>("thread/read", { threadId: first.threadId, includeTurns: false });
  assert.equal(persisted.thread.id, first.threadId);
  assert.equal(persisted.thread.name, `[local-codex-mcp] ${first.threadId}`);
  assert.equal(persisted.thread.source, "vscode");
  assert.equal(persisted.thread.threadSource, null);
  assert.equal(persisted.thread.status.type, "notLoaded");

  const external = await client.request<{ thread: { id: string } }>("thread/start", {
    cwd: root,
    threadSource: "someone-else",
  });
  await assert.rejects(
    backend.status(external.thread.id),
    (error: unknown) =>
      typeof error === "object" && error !== null && "code" in error && error.code === "THREAD_NOT_FOUND",
  );

  const second = await backend.continue({
    threadId: first.threadId,
    task: "[LONG] keep working",
    mode: "work",
    network: false,
    waitMs: 5,
  });
  assert.equal(second.status, "running");
  const workRequest = await client.request<{
    lastResumeParams: { approvalPolicy: string; sandbox: string; config: { web_search: string } };
    lastTurnParams: { sandboxPolicy: { type: string; writableRoots: string[]; networkAccess: boolean } };
  }>("test/lastRequests", {});
  assert.equal(workRequest.lastResumeParams.approvalPolicy, "never");
  assert.equal(workRequest.lastResumeParams.sandbox, "workspace-write");
  assert.equal(workRequest.lastResumeParams.config.web_search, "disabled");
  assert.equal(workRequest.lastTurnParams.sandboxPolicy.type, "workspaceWrite");
  assert.deepEqual(workRequest.lastTurnParams.sandboxPolicy.writableRoots, [root]);
  assert.equal(workRequest.lastTurnParams.sandboxPolicy.networkAccess, false);
  const interrupted = await backend.interrupt(first.threadId);
  assert.equal(interrupted.status, "interrupted");

  await client.request("test/setResumeResponseThreadId", { threadId: "different-thread" });
  await assert.rejects(
    backend.continue({
      threadId: first.threadId,
      task: "must reject a mismatched resume response",
      mode: "research",
      network: false,
      waitMs: 0,
    }),
    (error: unknown) =>
      typeof error === "object" && error !== null && "code" in error && error.code === "THREAD_NOT_FOUND",
  );
});

test("backend reports missing Codex authentication", async (t) => {
  const root = await mkdtemp(path.join(os.tmpdir(), "codex-bridge-auth-"));
  const logger = new NullLogger();
  const client = new CodexAppServerClient({
    command: process.execPath,
    args: [fixture, "--unauth"],
    requestTimeoutMs: 1_000,
    logger,
  });
  const backend = new CodexBackend({
    client,
    pathPolicy: await PathPolicy.create([root]),
    store: new TaskStore(2_000, 100),
    logger,
  });
  t.after(async () => {
    await backend.stop();
    const { rm } = await import("node:fs/promises");
    await rm(root, { recursive: true });
  });
  await assert.rejects(
    backend.start(),
    (error: unknown) =>
      typeof error === "object" && error !== null && "code" in error && error.code === "CODEX_NOT_AUTHENTICATED",
  );
});

test("permanent stop gate prevents an operation paused at resolveCwd from restarting app-server", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "codex-backend-stop-gate-"));
  const logger = new RecordingLogger();
  const client = new CodexAppServerClient({
    command: process.execPath,
    args: [fixture],
    requestTimeoutMs: 1_000,
    logger,
  });
  const realPolicy = await PathPolicy.create([root], root);
  let releaseResolve!: () => void;
  let enteredResolve!: () => void;
  const release = new Promise<void>((resolve) => { releaseResolve = resolve; });
  const entered = new Promise<void>((resolve) => { enteredResolve = resolve; });
  const delayedPolicy = {
    ...realPolicy,
    async resolveCwd(requested?: string) {
      enteredResolve();
      await release;
      return realPolicy.resolveCwd(requested);
    },
  } as unknown as PathPolicy;
  const backend = new CodexBackend({
    client,
    pathPolicy: delayedPolicy,
    store: new TaskStore(2_000, 100),
    logger,
  });

  try {
    await backend.start();
    const state = await client.request<{ pid: number }>("test/state", {});
    const dispatching = backend.delegate({
      task: "must not start",
      cwd: root,
      mode: "work",
      network: false,
      waitMs: 0,
    });
    await entered;
    const stopping = backend.stop();
    releaseResolve();
    await assert.rejects(dispatching, /backend has stopped/);
    await stopping;
    await waitForProcessExit(state.pid);
    assert.equal(client.isRunning, false);
    await assert.rejects(backend.start(), /backend has stopped/);
    assert.equal(logger.entries.filter((entry) => entry.event === "codex_started").length, 1);
  } finally {
    releaseResolve();
    await backend.stop();
    await rm(root, { recursive: true });
  }
});
