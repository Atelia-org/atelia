import assert from "node:assert/strict";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { PassThrough } from "node:stream";
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
import { JsonlFrameWriter } from "../src/galatea/protocol.js";
import { NullLogger } from "../src/logger.js";

const fixture = fileURLToPath(new URL("./fixtures/fake-app-server.js", import.meta.url));

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

test("runnable durable sidecar emits V2 ready and serves staged binding, start, and inspect", { timeout: 5_000 }, async () => {
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
    assert.deepEqual(ready, { v: 2, type: "ready" });

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
    assert.equal(rejected.v, 2);

    input.write(`${JSON.stringify({
      v: 2,
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
      v: 2,
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
        v: 2,
        type: "inspect-dispatch",
        requestId,
        dispatchId: "dispatch-1",
        threadId: binding.threadId,
        task,
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

test("durable JSONL server emits V2 protocol failures, stops on EOF, and flushes", async () => {
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
      [2, "protocol", "INVALID_UTF8"],
      [2, "protocol", "FRAME_TOO_LARGE"],
      [2, "protocol", "INVALID_FRAME"],
    ],
  );
});
