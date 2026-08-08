import assert from "node:assert/strict";
import { mkdtemp } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { CodexBackend } from "../src/codex/backend.js";
import { CodexAppServerClient } from "../src/codex/client.js";
import { TaskStore } from "../src/codex/task-store.js";
import { NullLogger } from "../src/logger.js";
import { PathPolicy } from "../src/security/paths.js";

const fixture = fileURLToPath(new URL("./fixtures/fake-app-server.js", import.meta.url));

test("backend completes, continues, returns bounded data, and interrupts", async (t) => {
  const root = await mkdtemp(path.join(os.tmpdir(), "codex-bridge-backend-"));
  const logger = new NullLogger();
  const client = new CodexAppServerClient({
    command: process.execPath,
    args: [fixture],
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
    lastTurnParams: { approvalPolicy: string; approvalsReviewer: string; sandboxPolicy: { type: string; networkAccess: boolean } };
  }>("test/lastRequests", {});
  assert.equal(researchRequest.lastTurnParams.approvalPolicy, "never");
  assert.equal(researchRequest.lastTurnParams.approvalsReviewer, "user");
  assert.deepEqual(researchRequest.lastTurnParams.sandboxPolicy, {
    type: "readOnly",
    networkAccess: false,
  });

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
