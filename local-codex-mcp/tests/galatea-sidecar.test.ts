import assert from "node:assert/strict";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { PassThrough, Writable } from "node:stream";
import test from "node:test";
import { fileURLToPath } from "node:url";
import {
  runGalateaSidecar,
  serveGalateaJsonl,
  type GalateaJsonlAdapter,
} from "../src/galatea-sidecar.js";
import type { GalateaDispatchFrame } from "../src/galatea/protocol.js";
import { JsonlFrameWriter } from "../src/galatea/protocol.js";
import { NullLogger } from "../src/logger.js";

const fixture = fileURLToPath(new URL("./fixtures/fake-app-server.js", import.meta.url));

test("sidecar fails malformed, wrong-case, unknown, and oversize input closed then stops on EOF", async () => {
  const input = new PassThrough();
  const output = new PassThrough();
  output.setEncoding("utf8");
  let outputText = "";
  output.on("data", (chunk: string) => { outputText += chunk; });
  const dispatched: GalateaDispatchFrame[] = [];
  let stopped = false;
  const adapter: GalateaJsonlAdapter = {
    async dispatch(frame) { dispatched.push(frame); },
    async stop() { stopped = true; },
  };
  const writer = new JsonlFrameWriter(output, 10_000);
  const serving = serveGalateaJsonl(
    input,
    adapter,
    writer,
    { maxInputFrameBytes: 256, maxTaskBytes: 100 },
    new NullLogger(),
  );

  input.write("{\n");
  input.write(`${JSON.stringify({ v: 1, type: "Dispatch", requestId: "r1", dispatchId: "d1", task: "x" })}\n`);
  input.write(`${JSON.stringify({ v: 1, type: "dispatch", requestId: "r2", dispatchId: "d2", task: "x", cwd: "/tmp" })}\n`);
  input.write(`${"x".repeat(257)}\n`);
  input.end(`${JSON.stringify({ v: 1, type: "dispatch", requestId: "r3", dispatchId: "d3", task: "do it" })}\n`);
  await serving;

  assert.equal(stopped, true);
  assert.equal(dispatched.length, 1);
  assert.equal(dispatched[0]?.dispatchId, "d3");
  const frames = outputText.trimEnd().split("\n").map((line) => JSON.parse(line));
  assert.deepEqual(frames.map((frame) => [frame.type, frame.stage, frame.code]), [
    ["failed", "protocol", "INVALID_FRAME"],
    ["failed", "protocol", "INVALID_FRAME"],
    ["failed", "protocol", "INVALID_FRAME"],
    ["failed", "protocol", "FRAME_TOO_LARGE"],
  ]);
});

test("terminal stdout EPIPE is fatal, stops input, and remains observable through flush", async () => {
  const input = new PassThrough();
  const output = new class extends Writable {
    override _write(
      _chunk: Buffer,
      _encoding: BufferEncoding,
      callback: (error?: Error | null) => void,
    ): void {
      const error = Object.assign(new Error("broken stdout"), { code: "EPIPE" });
      callback(error);
    }
  }();
  const writer = new JsonlFrameWriter(output, 10_000);
  let stopped = false;
  const adapter: GalateaJsonlAdapter = {
    async dispatch(frame) {
      await writer.write({
        v: 1,
        type: "accepted",
        requestId: frame.requestId,
        dispatchId: frame.dispatchId,
        threadId: "thread-1",
        turnId: "turn-1",
      });
    },
    async stop() { stopped = true; },
  };
  const serving = serveGalateaJsonl(
    input,
    adapter,
    writer,
    { maxInputFrameBytes: 1_000, maxTaskBytes: 100 },
    new NullLogger(),
  );
  input.write(`${JSON.stringify({
    v: 1,
    type: "dispatch",
    requestId: "request-epipe",
    dispatchId: "dispatch-epipe",
    task: "trigger terminal write",
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
  const writer = new JsonlFrameWriter(output, 10_000, 20);
  let stopped = false;
  const adapter: GalateaJsonlAdapter = {
    async dispatch(frame) {
      await writer.write({
        v: 1,
        type: "accepted",
        requestId: frame.requestId,
        dispatchId: frame.dispatchId,
        threadId: "thread-1",
        turnId: "turn-1",
      });
    },
    async stop() { stopped = true; },
  };
  const serving = serveGalateaJsonl(
    input,
    adapter,
    writer,
    { maxInputFrameBytes: 1_000, maxTaskBytes: 100 },
    new NullLogger(),
  );
  input.write(`${JSON.stringify({
    v: 1,
    type: "dispatch",
    requestId: "request-stall",
    dispatchId: "dispatch-stall",
    task: "stall output",
  })}\n`);

  await assert.rejects(serving, (error: unknown) =>
    typeof error === "object" && error !== null && "code" in error && error.code === "OUTPUT_WRITE_TIMEOUT");
  assert.equal(stopped, true);
  assert.equal(input.destroyed, true);
  assert.equal(output.destroyed, true);
});

test("composed sidecar emits ready, runs a natural turn, and reclaims app-server on EOF", { timeout: 5_000 }, async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "galatea-sidecar-entry-"));
  const input = new PassThrough();
  const output = new PassThrough();
  const frames: Array<Record<string, unknown>> = [];
  let buffered = "";
  let resolveCompleted!: () => void;
  const completed = new Promise<void>((resolve) => { resolveCompleted = resolve; });
  output.setEncoding("utf8");
  output.on("data", (chunk: string) => {
    buffered += chunk;
    while (buffered.includes("\n")) {
      const index = buffered.indexOf("\n");
      const line = buffered.slice(0, index);
      buffered = buffered.slice(index + 1);
      const frame = JSON.parse(line) as Record<string, unknown>;
      frames.push(frame);
      if (frame.type === "completed") resolveCompleted();
    }
  });

  try {
    const running = runGalateaSidecar(input, output, {
      CODEX_BRIDGE_ALLOWED_ROOTS: JSON.stringify([root]),
      CODEX_BRIDGE_DEFAULT_CWD: root,
      CODEX_BRIDGE_CODEX_COMMAND: process.execPath,
      CODEX_BRIDGE_CODEX_ARGS: JSON.stringify([fixture]),
      CODEX_BRIDGE_RPC_TIMEOUT_MS: "1000",
      GALATEA_CODEX_TURN_DEADLINE_MS: "1000",
    });
    input.write(`${JSON.stringify({
      v: 1,
      type: "dispatch",
      requestId: "request-1",
      dispatchId: "dispatch-1",
      task: "[NATURAL] reply",
    })}\n`);
    const sawCompleted = await Promise.race([
      completed.then(() => true),
      running.then(() => false),
    ]);
    assert.equal(sawCompleted, true);
    input.end();
    await running;
    assert.deepEqual(frames.map((frame) => frame.type), ["ready", "accepted", "completed"]);
    assert.match(String(frames[2]?.final), /```ts/);
  } finally {
    input.destroy();
    output.destroy();
    await rm(root, { recursive: true });
  }
});

test("immediate EOF during dispatch cannot restart or leak app-server", { timeout: 5_000 }, async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "galatea-sidecar-eof-race-"));
  const lifecycleFile = path.join(root, "lifecycle.log");
  const input = new PassThrough();
  const output = new PassThrough();
  output.resume();
  try {
    const running = runGalateaSidecar(input, output, {
      CODEX_BRIDGE_ALLOWED_ROOTS: JSON.stringify([root]),
      CODEX_BRIDGE_DEFAULT_CWD: root,
      CODEX_BRIDGE_CODEX_COMMAND: process.execPath,
      CODEX_BRIDGE_CODEX_ARGS: JSON.stringify([fixture, `--lifecycle-file=${lifecycleFile}`]),
      CODEX_BRIDGE_RPC_TIMEOUT_MS: "1000",
      GALATEA_CODEX_TURN_DEADLINE_MS: "1000",
    });
    input.end(`${JSON.stringify({
      v: 1,
      type: "dispatch",
      requestId: "request-eof",
      dispatchId: "dispatch-eof",
      task: "must not outlive EOF",
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
