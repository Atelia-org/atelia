import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { PassThrough, Writable } from "node:stream";
import { setTimeout as delay } from "node:timers/promises";
import test from "node:test";
import { fileURLToPath } from "node:url";
import {
  runGalateaDurableSidecar,
  serveGalateaDurableJsonl,
  type GalateaDurableJsonlAdapter,
} from "../src/galatea-durable-sidecar.js";
import {
  encodeGalateaDurableOutputFrame,
  type GalateaDurableOutputFrame,
} from "../src/galatea/durable-protocol.js";
import { JsonlFrameWriter } from "../src/galatea/jsonl.js";
import { createGalateaCodexChildEnvironment } from "../src/galatea/sidecar-config.js";
import { NullLogger } from "../src/logger.js";

const fixture = fileURLToPath(new URL("./fixtures/fake-app-server.js", import.meta.url));
const sidecarEntry = fileURLToPath(new URL("../src/galatea-durable-sidecar.js", import.meta.url));

test("Galatea app-server environment removes only confirmed parent Codex context", () => {
  const sanitized = createGalateaCodexChildEnvironment({
    PATH: "/safe/path",
    HOME: "/safe/home",
    CODEX_HOME: "/safe/codex-home",
    CODEX_MANAGED_BY_NPM: "1",
    CODEX_MANAGED_PACKAGE_ROOT: "/safe/package-root",
    OPENAI_API_KEY: "test-auth-sentinel",
    OPENAI_BASE_URL: "https://provider.invalid/v1",
    HTTPS_PROXY: "https://proxy.invalid",
    CODEX_SESSION_ID: "ambient-session",
    CODEX_THREAD_ID: "ambient-thread",
    CODEX_INTERNAL_ORIGINATOR_OVERRIDE: "ambient-origin",
    CODEX_PERMISSION_PROFILE: "ambient-permission",
    CODEX_CI: "1",
  });

  for (const key of [
    "CODEX_SESSION_ID",
    "CODEX_THREAD_ID",
    "CODEX_INTERNAL_ORIGINATOR_OVERRIDE",
    "CODEX_PERMISSION_PROFILE",
    "CODEX_CI",
  ]) {
    assert.equal(sanitized[key], undefined);
  }
  assert.equal(sanitized.PATH, "/safe/path");
  assert.equal(sanitized.HOME, "/safe/home");
  assert.equal(sanitized.CODEX_HOME, "/safe/codex-home");
  assert.equal(sanitized.CODEX_MANAGED_BY_NPM, "1");
  assert.equal(sanitized.CODEX_MANAGED_PACKAGE_ROOT, "/safe/package-root");
  assert.equal(sanitized.OPENAI_API_KEY, "test-auth-sentinel");
  assert.equal(sanitized.OPENAI_BASE_URL, "https://provider.invalid/v1");
  assert.equal(sanitized.HTTPS_PROXY, "https://proxy.invalid");
});

test("durable sidecar fails before ready when configured Codex version drifts", { timeout: 5_000 }, async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "galatea-codex-version-drift-"));
  try {
    const child = spawn(process.execPath, [sidecarEntry], {
      env: {
        ...process.env,
        CODEX_BRIDGE_ALLOWED_ROOTS: JSON.stringify([root]),
        CODEX_BRIDGE_DEFAULT_CWD: root,
        CODEX_BRIDGE_CODEX_COMMAND: process.execPath,
        CODEX_BRIDGE_CODEX_ARGS: JSON.stringify([
          fixture,
          "--user-agent=atelia_local_codex_mcp/0.151.0 (must-not-be-logged)",
        ]),
        CODEX_BRIDGE_RPC_TIMEOUT_MS: "1000",
      },
      stdio: ["ignore", "pipe", "pipe"],
    });
    child.stdout.setEncoding("utf8");
    child.stderr.setEncoding("utf8");
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (chunk: string) => { stdout += chunk; });
    child.stderr.on("data", (chunk: string) => { stderr += chunk; });
    const exitCode = await new Promise<number | null>((resolve, reject) => {
      child.once("error", reject);
      child.once("exit", (code) => resolve(code));
    });

    assert.equal(exitCode, 1);
    assert.equal(stdout, "");
    assert.match(stderr, /"event":"codex_version_mismatch"/);
    assert.match(stderr, /"error_code":"CODEX_VERSION_MISMATCH"/);
    assert.match(stderr, /"expected_version":"0\.154\.0-alpha\.3"/);
    assert.match(stderr, /"actual_version":"0\.151\.0"/);
    assert.doesNotMatch(stderr, /must-not-be-logged/);
  } finally {
    await rm(root, { recursive: true });
  }
});

class FrameCollector {
  readonly frames: GalateaDurableOutputFrame[] = [];
  private buffered = "";
  private readonly waiters = new Set<() => void>();

  constructor(output: PassThrough) {
    output.setEncoding("utf8");
    output.on("data", (chunk: string) => {
      this.buffered += chunk;
      while (this.buffered.includes("\n")) {
        const index = this.buffered.indexOf("\n");
        const line = this.buffered.slice(0, index);
        this.buffered = this.buffered.slice(index + 1);
        this.frames.push(JSON.parse(line) as GalateaDurableOutputFrame);
        for (const wake of [...this.waiters]) wake();
      }
    });
  }

  async waitFor(
    predicate: (frame: GalateaDurableOutputFrame) => boolean,
    timeoutMs = 2_000,
  ): Promise<GalateaDurableOutputFrame> {
    const existing = this.frames.find(predicate);
    if (existing) return existing;
    return await new Promise<GalateaDurableOutputFrame>((resolve, reject) => {
      let timer: NodeJS.Timeout;
      const inspect = () => {
        const found = this.frames.find(predicate);
        if (!found) return;
        clearTimeout(timer);
        this.waiters.delete(inspect);
        resolve(found);
      };
      timer = setTimeout(() => {
        this.waiters.delete(inspect);
        reject(new Error("Timed out waiting for durable sidecar frame."));
      }, timeoutMs);
      this.waiters.add(inspect);
    });
  }
}

test("runnable durable sidecar emits V3 ready and serves staged binding, start, and inspect", { timeout: 5_000 }, async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "galatea-durable-entry-"));
  const lifecycleFile = path.join(root, "lifecycle.log");
  const input = new PassThrough();
  const output = new PassThrough();
  const collector = new FrameCollector(output);
  try {
    const running = runGalateaDurableSidecar(input, output, {
      CODEX_BRIDGE_ALLOWED_ROOTS: JSON.stringify([root]),
      CODEX_BRIDGE_DEFAULT_CWD: root,
      CODEX_BRIDGE_CODEX_COMMAND: process.execPath,
      CODEX_BRIDGE_CODEX_ARGS: JSON.stringify([
        fixture,
        `--lifecycle-file=${lifecycleFile}`,
      ]),
      CODEX_BRIDGE_RPC_TIMEOUT_MS: "1000",
      GALATEA_CODEX_MAX_INPUT_FRAME_BYTES: "1048576",
      GALATEA_CODEX_MAX_OUTPUT_FRAME_BYTES: "1048576",
      GALATEA_CODEX_MAX_TASK_BYTES: "100000",
      GALATEA_CODEX_MAX_FINAL_BYTES: "100000",
    });
    const ready = await collector.waitFor((frame) => frame.type === "ready");
    assert.deepEqual(ready, { v: 3, type: "ready" });

    input.write(`${JSON.stringify({
      v: 1,
      type: "dispatch",
      requestId: "legacy-request",
      dispatchId: "legacy-dispatch",
      task: "must be rejected",
    })}\n`);
    const rejected = await collector.waitFor(
      (frame) => frame.type === "failed" && frame.stage === "protocol",
    );
    assert.equal(rejected.type === "failed" && rejected.code, "INVALID_FRAME");
    assert.equal(rejected.v, 3);

    input.write(`${JSON.stringify({
      v: 3,
      type: "ensure-binding",
      requestId: "binding-request",
      bindingOperationId: "binding-1",
    })}\n`);
    const binding = await collector.waitFor(
      (frame) => frame.type === "binding-established",
    );
    assert.equal(binding.type, "binding-established");
    if (binding.type !== "binding-established") return;

    const task = "[NATURAL] durable task";
    input.write(`${JSON.stringify({
      v: 3,
      type: "start-turn",
      requestId: "start-request",
      dispatchId: "dispatch-1",
      threadId: binding.threadId,
      task,
    })}\n`);
    const accepted = await collector.waitFor(
      (frame) => frame.type === "turn-accepted",
    );
    assert.equal(accepted.type, "turn-accepted");
    if (accepted.type !== "turn-accepted") return;
    assert.equal(accepted.threadId, binding.threadId);

    let terminal: GalateaDurableOutputFrame | undefined;
    for (let attempt = 0; attempt < 10; attempt += 1) {
      const requestId = `inspect-${attempt}`;
      input.write(`${JSON.stringify({
        v: 3,
        type: "inspect-dispatch",
        requestId,
        dispatchId: "dispatch-1",
        threadId: binding.threadId,
        task,
        expectedTurnId: accepted.turnId,
      })}\n`);
      const inspected = await collector.waitFor(
        (frame) => frame.type === "dispatch-inspected"
          && frame.requestId === requestId,
      );
      if (inspected.type === "dispatch-inspected"
          && inspected.outcome === "completed") {
        terminal = inspected;
        break;
      }
      await delay(20);
    }
    assert.ok(terminal);
    if (terminal?.type === "dispatch-inspected"
        && terminal.outcome === "completed") {
      assert.equal(terminal.turnId, accepted.turnId);
      assert.match(terminal.final, /事情已经办妥/);
    }

    input.end();
    await running;
    const lifecycle = await readFile(lifecycleFile, "utf8");
    const starts = lifecycle.split("\n").filter((line) => line.startsWith("start:"));
    assert.equal(
      starts.length,
      1,
    );
    const pid = Number(starts[0]?.slice("start:".length));
    assert.ok(Number.isInteger(pid));
    assert.throws(
      () => process.kill(pid, 0),
      (error: unknown) => typeof error === "object"
        && error !== null
        && "code" in error
        && error.code === "ESRCH",
    );
  } finally {
    input.destroy();
    output.destroy();
    await rm(root, { recursive: true });
  }
});

test("durable JSONL server emits V3 protocol failures, stops on EOF, and flushes", async () => {
  const input = new PassThrough();
  const output = new PassThrough();
  const collector = new FrameCollector(output);
  let stopped = false;
  const adapter: GalateaDurableJsonlAdapter = {
    async handle() {
      assert.fail("Invalid inputs must not reach the durable adapter.");
    },
    async stop() {
      stopped = true;
    },
  };
  const writer = new JsonlFrameWriter<GalateaDurableOutputFrame>(
    output,
    10_000,
    1_000,
    encodeGalateaDurableOutputFrame,
  );
  const serving = serveGalateaDurableJsonl(
    input,
    adapter,
    writer,
    { maxInputFrameBytes: 256, maxTaskBytes: 100 },
    new NullLogger(),
  );
  input.write(Buffer.from([0xff, 0x0a]));
  input.write(`${"x".repeat(257)}\n`);
  input.end(`${JSON.stringify({
    v: 1,
    type: "dispatch",
    requestId: "r",
    dispatchId: "d",
    task: "x",
  })}\n`);
  await serving;
  assert.equal(stopped, true);
  assert.deepEqual(
    collector.frames.map((frame) => frame.type === "failed"
      ? [frame.v, frame.stage, frame.code]
      : [frame.v, frame.type]),
    [
      [3, "protocol", "INVALID_UTF8"],
      [3, "protocol", "FRAME_TOO_LARGE"],
      [3, "protocol", "INVALID_FRAME"],
    ],
  );
});

test("terminal stdout EPIPE is fatal, stops input, and remains observable through flush", async () => {
  const input = new PassThrough();
  const output = new class extends Writable {
    override _write(
      _chunk: Buffer,
      _encoding: BufferEncoding,
      callback: (error?: Error | null) => void,
    ): void {
      callback(Object.assign(new Error("broken stdout"), { code: "EPIPE" }));
    }
  }();
  const writer = new JsonlFrameWriter<GalateaDurableOutputFrame>(
    output,
    10_000,
    1_000,
    encodeGalateaDurableOutputFrame,
  );
  let stopped = false;
  const adapter: GalateaDurableJsonlAdapter = {
    async handle(frame) {
      await writer.write({
        v: 3,
        type: "binding-established",
        requestId: frame.requestId,
        bindingOperationId: "binding-epipe",
        threadId: "thread-epipe",
      });
    },
    async stop() { stopped = true; },
  };
  const serving = serveGalateaDurableJsonl(
    input,
    adapter,
    writer,
    { maxInputFrameBytes: 1_000, maxTaskBytes: 100 },
    new NullLogger(),
  );
  input.write(`${JSON.stringify({
    v: 3,
    type: "ensure-binding",
    requestId: "request-epipe",
    bindingOperationId: "binding-epipe",
  })}\n`);

  await assert.rejects(serving, (error: unknown) =>
    typeof error === "object" && error !== null && "code" in error && error.code === "EPIPE");
  assert.equal(stopped, true);
  assert.equal(input.destroyed, true);
  await assert.rejects(writer.flush(), (error: unknown) =>
    typeof error === "object" && error !== null && "code" in error && error.code === "EPIPE");
});

test("stalled stdout backpressure hits a bounded deadline and stops the sidecar", { timeout: 1_000 }, async () => {
  const input = new PassThrough();
  const output = new class extends Writable {
    override _write(
      _chunk: Buffer,
      _encoding: BufferEncoding,
      _callback: (error?: Error | null) => void,
    ): void {
      // Deliberately never acknowledge the write.
    }
  }();
  const writer = new JsonlFrameWriter<GalateaDurableOutputFrame>(
    output,
    10_000,
    20,
    encodeGalateaDurableOutputFrame,
  );
  let stopped = false;
  const adapter: GalateaDurableJsonlAdapter = {
    async handle(frame) {
      await writer.write({
        v: 3,
        type: "binding-established",
        requestId: frame.requestId,
        bindingOperationId: "binding-stall",
        threadId: "thread-stall",
      });
    },
    async stop() { stopped = true; },
  };
  const serving = serveGalateaDurableJsonl(
    input,
    adapter,
    writer,
    { maxInputFrameBytes: 1_000, maxTaskBytes: 100 },
    new NullLogger(),
  );
  input.write(`${JSON.stringify({
    v: 3,
    type: "ensure-binding",
    requestId: "request-stall",
    bindingOperationId: "binding-stall",
  })}\n`);

  await assert.rejects(serving, (error: unknown) =>
    typeof error === "object"
      && error !== null
      && "code" in error
      && error.code === "OUTPUT_WRITE_TIMEOUT");
  assert.equal(stopped, true);
  assert.equal(input.destroyed, true);
  assert.equal(output.destroyed, true);
});

test("immediate EOF during durable operation cannot restart or leak app-server", { timeout: 5_000 }, async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "galatea-durable-eof-race-"));
  const lifecycleFile = path.join(root, "lifecycle.log");
  const input = new PassThrough();
  const output = new PassThrough();
  output.resume();
  try {
    const running = runGalateaDurableSidecar(input, output, {
      CODEX_BRIDGE_ALLOWED_ROOTS: JSON.stringify([root]),
      CODEX_BRIDGE_DEFAULT_CWD: root,
      CODEX_BRIDGE_CODEX_COMMAND: process.execPath,
      CODEX_BRIDGE_CODEX_ARGS: JSON.stringify([fixture, `--lifecycle-file=${lifecycleFile}`]),
      CODEX_BRIDGE_RPC_TIMEOUT_MS: "1000",
    });
    input.end(`${JSON.stringify({
      v: 3,
      type: "ensure-binding",
      requestId: "request-eof",
      bindingOperationId: "binding-eof",
    })}\n`);
    await running;

    const lifecycle = (await readFile(lifecycleFile, "utf8")).trimEnd().split("\n");
    const starts = lifecycle.filter((line) => line.startsWith("start:"));
    assert.equal(starts.length, 1);
    const pid = Number(starts[0]?.slice("start:".length));
    assert.throws(() => process.kill(pid, 0), (error: unknown) =>
      typeof error === "object" && error !== null && "code" in error && error.code === "ESRCH");
  } finally {
    input.destroy();
    output.destroy();
    await rm(root, { recursive: true });
  }
});
