import assert from "node:assert/strict";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { CodexAppServerClient } from "../src/codex/client.js";
import { NullLogger, type BridgeLogger, type LogLevel } from "../src/logger.js";

const fixture = fileURLToPath(new URL("./fixtures/fake-app-server.js", import.meta.url));

class RecordingLogger implements BridgeLogger {
  readonly entries: Array<{ level: LogLevel; event: string; fields: Record<string, unknown> | undefined }> = [];

  log(level: LogLevel, event: string, fields?: Record<string, unknown>): void {
    this.entries.push({ level, event, fields });
  }
}

function client(timeout = 1_000): CodexAppServerClient {
  return new CodexAppServerClient({
    command: process.execPath,
    args: [fixture],
    requestTimeoutMs: timeout,
    logger: new NullLogger(),
  });
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

test("initialize handshake runs once and request correlation handles out-of-order responses", async (t) => {
  const value = client();
  t.after(() => value.stop());
  await value.start();
  const state = await value.request<{ initialized: boolean; initializeCount: number }>("test/state", {});
  assert.equal(state.initialized, true);
  assert.equal(state.initializeCount, 1);

  const [slow, fast] = await Promise.all([
    value.request<{ value: string }>("test/delay", { delay: 40, value: "slow" }),
    value.request<{ value: string }>("test/delay", { delay: 5, value: "fast" }),
  ]);
  assert.equal(slow.value, "slow");
  assert.equal(fast.value, "fast");
});

test("notifications are dispatched without becoming responses", async (t) => {
  const value = client();
  t.after(() => value.stop());
  const notifications: string[] = [];
  value.subscribe((notification) => notifications.push(notification.method));
  await value.request("test/notify", {});
  assert.ok(notifications.includes("warning"));
});

test("server requests fail closed with bounded responses", async (t) => {
  const value = client();
  t.after(() => value.stop());

  const command = await value.request<{ response: { decision: string } }>("test/serverRequest", {
    method: "item/commandExecution/requestApproval",
  });
  assert.equal(command.response.decision, "decline");

  const file = await value.request<{ response: { decision: string } }>("test/serverRequest", {
    method: "item/fileChange/requestApproval",
  });
  assert.equal(file.response.decision, "decline");

  const legacy = await value.request<{ response: { decision: { denied: { rejection: string } } } }>(
    "test/serverRequest",
    { method: "execCommandApproval" },
  );
  assert.match(legacy.response.decision.denied.rejection, /never grants/);

  const permissions = await value.request<{ response: { permissions: object; scope: string } }>(
    "test/serverRequest",
    { method: "item/permissions/requestApproval" },
  );
  assert.deepEqual(permissions.response, { permissions: {}, scope: "turn" });

  const elicitation = await value.request<{ response: { action: string; content: null } }>(
    "test/serverRequest",
    { method: "mcpServer/elicitation/request" },
  );
  assert.equal(elicitation.response.action, "decline");

  const userInput = await value.request<{ response: { answers: object } }>("test/serverRequest", {
    method: "item/tool/requestUserInput",
  });
  assert.deepEqual(userInput.response.answers, {});

  const unknown = await value.request<{ error: { code: number } }>("test/serverRequest", {
    method: "unknown/server/request",
  });
  assert.equal(unknown.error.code, -32601);
});

test("request timeout rejects without stopping the process", async (t) => {
  // Keep enough startup margin for slower supported CI hosts.
  const value = client(300);
  t.after(() => value.stop());
  await assert.rejects(value.request("test/hang", {}), /timed out/);
  const state = await value.request<{ initialized: boolean }>("test/state", {});
  assert.equal(state.initialized, true);
});

test("process crash rejects pending requests", async (t) => {
  const value = client();
  t.after(() => value.stop());
  await assert.rejects(value.request("test/crash", {}), /exited unexpectedly/);
});

test("process failure logs bounded stderr locally", async (t) => {
  const logger = new RecordingLogger();
  const value = new CodexAppServerClient({
    command: process.execPath,
    args: [fixture, "--stderr-on-crash"],
    requestTimeoutMs: 1_000,
    logger,
  });
  t.after(() => value.stop());

  await assert.rejects(value.request("test/crash", {}), /exited unexpectedly/);

  const failure = logger.entries.find((entry) => entry.event === "codex_start_failed");
  assert.equal(failure?.level, "debug");
  const stderrTail = failure?.fields?.stderr_tail;
  assert.equal(typeof stderrTail, "string");
  assert.match(stderrTail as string, /fake stderr tail/);
  assert.ok(Buffer.byteLength(stderrTail as string) <= 8 * 1024);
});

test("malformed stdout terminates the failed child and a new generation can start", async (t) => {
  const value = new CodexAppServerClient({
    command: process.execPath,
    args: [fixture, "--ignore-sigterm"],
    requestTimeoutMs: 1_000,
    stopTimeoutMs: 50,
    logger: new NullLogger(),
  });
  t.after(() => value.stop());
  const first = await value.request<{ pid: number }>("test/state", {});
  await assert.rejects(value.request("test/malformed", {}), /invalid JSON/);
  await waitForProcessExit(first.pid);

  const second = await value.request<{ initialized: boolean; pid: number }>("test/state", {});
  assert.equal(second.initialized, true);
  assert.notEqual(second.pid, first.pid);
});

test("stop waits for a stubborn child before an immediate restart", async (t) => {
  const value = new CodexAppServerClient({
    command: process.execPath,
    args: [fixture, "--ignore-sigterm"],
    requestTimeoutMs: 1_000,
    stopTimeoutMs: 50,
    logger: new NullLogger(),
  });
  t.after(() => value.stop());
  const first = await value.request<{ pid: number }>("test/state", {});
  const stopping = value.stop();
  const restarting = value.start();
  await Promise.all([stopping, restarting]);
  await waitForProcessExit(first.pid);
  const second = await value.request<{ pid: number }>("test/state", {});
  assert.notEqual(second.pid, first.pid);
});

test("stdin closure fails requests without an unhandled EPIPE", async (t) => {
  const value = client(500);
  t.after(() => value.stop());
  await value.request("test/closeStdin", {});
  await new Promise((resolve) => setTimeout(resolve, 20));
  await assert.rejects(value.request("test/state", {}), /exited unexpectedly|not running|stopped/);
});

test("missing Codex executable returns CODEX_NOT_FOUND", async () => {
  const value = new CodexAppServerClient({
    command: "/definitely/not/a/codex-executable",
    args: [],
    requestTimeoutMs: 1_000,
    logger: new NullLogger(),
  });
  await assert.rejects(
    value.start(),
    (error: unknown) =>
      typeof error === "object" && error !== null && "code" in error && error.code === "CODEX_NOT_FOUND",
  );
});
