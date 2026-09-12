import assert from "node:assert/strict";
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { once } from "node:events";
import { mkdir, mkdtemp, readFile, readlink, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import readline from "node:readline";
import { setTimeout as delay } from "node:timers/promises";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { CodexAppServerClient } from "../src/codex/client.js";
import { loadConfig } from "../src/config.js";
import type { GalateaDurableInputFrame, GalateaDurableOutputFrame } from "../src/galatea/durable-protocol.js";
import { createGalateaCodexChildEnvironment } from "../src/galatea/sidecar-config.js";
import { NullLogger } from "../src/logger.js";

const runLive = process.env.CODEX_BRIDGE_RUN_GALATEA_HOME_LIVE === "1";
const entryPoint = fileURLToPath(new URL("../src/galatea-durable-sidecar.js", import.meta.url));

function environment(roots: string[]): NodeJS.ProcessEnv {
  const env = createGalateaCodexChildEnvironment(process.env);
  for (const key of Object.keys(env)) {
    if (key.startsWith("CODEX_BRIDGE_") || key.startsWith("GALATEA_CODEX_")) delete env[key];
  }
  return {
    ...env,
    CODEX_BRIDGE_ALLOWED_ROOTS: JSON.stringify(roots),
    CODEX_BRIDGE_RPC_TIMEOUT_MS: "60000",
    GALATEA_CODEX_MODE: "work",
    GALATEA_CODEX_LOCAL_COMMAND_NETWORK: "false",
    GALATEA_CODEX_WEB_SEARCH: "disabled",
    GALATEA_CODEX_IMAGE_GENERATION: "false",
    GALATEA_CODEX_VIEW_IMAGE: "false",
  };
}

class LiveSidecar {
  private readonly child: ChildProcessWithoutNullStreams;
  private readonly frames = new Map<string, GalateaDurableOutputFrame>();
  private sequence = 0;
  private failure?: Error;
  private appServerPid?: number;
  private readonly diagnostics: Record<string, unknown>[] = [];

  constructor(roots: string[]) {
    this.child = spawn(process.execPath, [entryPoint], { cwd: "/", env: environment(roots), stdio: "pipe" });
    this.child.on("error", (error) => { this.failure = error; });
    readline.createInterface({ input: this.child.stdout }).on("line", (line) => {
      const frame = JSON.parse(line) as GalateaDurableOutputFrame;
      if (frame.type === "failed" && frame.stage === "protocol") this.failure = new Error(`Sidecar protocol: ${frame.code}`);
      this.frames.set(frame.type === "ready" ? "ready" : "requestId" in frame ? frame.requestId ?? "protocol" : "protocol", frame);
    });
    readline.createInterface({ input: this.child.stderr }).on("line", (line) => {
      try {
        const item = JSON.parse(line) as { event?: string; pid?: number; error_code?: string; stage?: string; rpc_method?: string; rpc_code?: number };
        if (item.event === "codex_started") this.appServerPid = item.pid;
        if (item.event === "galatea_durable_sidecar_failed") this.failure = new Error(`Sidecar failed: ${item.error_code}`);
        if (item.event === "galatea_durable_operation_failed") {
          this.diagnostics.push({ stage: item.stage, code: item.error_code, method: item.rpc_method, rpcCode: item.rpc_code });
          if (this.diagnostics.length > 8) this.diagnostics.shift();
        }
      } catch { /* Native stderr is deliberately not copied into canary reports. */ }
    });
  }

  private async wait(key: string, timeoutMs = 65_000): Promise<GalateaDurableOutputFrame> {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
      const frame = this.frames.get(key);
      if (frame) {
        this.frames.delete(key);
        if (frame.type === "failed") {
          await delay(20); // Let the separate stderr pipe deliver its safe diagnostic.
          throw new Error(`Sidecar ${frame.stage}: ${frame.code}; diagnostics=${JSON.stringify(this.diagnostics)}`);
        }
        return frame;
      }
      if (this.failure) throw this.failure;
      if (this.child.exitCode !== null || this.child.signalCode !== null) throw new Error("Sidecar exited before replying.");
      await delay(20);
    }
    throw new Error(`Timed out waiting for sidecar ${key}.`);
  }

  async ready(): Promise<void> {
    assert.deepEqual(await this.wait("ready"), { v: 4, type: "ready" });
    assert.equal(await readlink(`/proc/${this.child.pid}/cwd`), "/");
    for (let attempt = 0; this.appServerPid === undefined && attempt < 100; attempt += 1) await delay(20);
    assert.ok(this.appServerPid);
    assert.equal(await readlink(`/proc/${this.appServerPid}/cwd`), "/");
    const children = (await readFile(`/proc/${this.appServerPid}/task/${this.appServerPid}/children`, "utf8")).trim();
    for (const pid of children.split(/\s+/).filter(Boolean)) {
      assert.equal(await readlink(`/proc/${pid}/cwd`), "/");
    }
  }

  async request(input: Omit<GalateaDurableInputFrame, "v" | "requestId"> | Record<string, unknown>): Promise<GalateaDurableOutputFrame> {
    const requestId = `home-canary-${++this.sequence}`;
    this.child.stdin.write(`${JSON.stringify({ ...input, v: 4, requestId })}\n`);
    return this.wait(requestId);
  }

  async bind(cwd: string): Promise<string> {
    const frame = await this.request({ type: "ensure-binding", bindingOperationId: `home-binding-${this.sequence}`, cwd });
    assert.equal(frame.type, "binding-established");
    if (frame.type !== "binding-established") throw new Error("Expected binding.");
    return frame.threadId;
  }

  async inspect(threadId: string, dispatchId: string, task: string, expectedTurnId: string | null): Promise<string> {
    const deadline = Date.now() + 180_000;
    while (Date.now() < deadline) {
      const frame = await this.request({ type: "inspect-dispatch", threadId, dispatchId, task, expectedTurnId });
      assert.equal(frame.type, "dispatch-inspected");
      if (frame.type !== "dispatch-inspected") throw new Error("Expected inspection.");
      if (frame.outcome === "completed") return frame.turnId;
      assert.ok(frame.outcome === "running" || frame.outcome === "unavailable", `Unexpected inspection outcome: ${frame.outcome}`);
      await delay(500);
    }
    throw new Error("Live task did not finish within three minutes.");
  }

  async write(threadId: string, cwd: string, dispatchId: string, marker: string): Promise<{ turnId: string; task: string }> {
    const task = `Use a local shell command to write exactly ${JSON.stringify(`${marker}\n`)} to the relative file home-canary.txt in your current working directory. Do not use an absolute file path. Do not modify any other files. Reply briefly after the write succeeds.`;
    const frame = await this.request({ type: "start-turn", threadId, cwd, dispatchId, task });
    assert.equal(frame.type, "turn-accepted");
    if (frame.type !== "turn-accepted") throw new Error("Expected accepted turn.");
    assert.equal(frame.threadId, threadId);
    assert.equal(await this.inspect(threadId, dispatchId, task, frame.turnId), frame.turnId);
    assert.equal(await readFile(path.join(cwd, "home-canary.txt"), "utf8"), `${marker}\n`);
    return { turnId: frame.turnId, task };
  }

  async stop(): Promise<void> {
    if (this.child.exitCode !== null || this.child.signalCode !== null) return;
    const exited = once(this.child, "exit");
    this.child.stdin.end();
    const timer = setTimeout(() => this.child.kill("SIGTERM"), 10_000);
    const killTimer = setTimeout(() => this.child.kill("SIGKILL"), 15_000);
    try { await exited; } finally { clearTimeout(timer); clearTimeout(killTimer); }
  }
}

test("live Galatea homes preserve threads across cold and hot cwd changes", {
  skip: !runLive || process.platform !== "linux", timeout: 600_000,
}, async (t) => {
  const root = await mkdtemp(path.join(os.tmpdir(), "galatea-home-live-"));
  const old = path.join(root, "old");
  const a = path.join(root, "a");
  const b = path.join(root, "b");
  const moved = path.join(root, "a-moved");
  await Promise.all([old, a, b, moved].map((cwd) => mkdir(cwd)));
  let sidecar = new LiveSidecar([old]);
  try {
    await sidecar.ready();
    const threadA = await sidecar.bind(old);
    const seed = await sidecar.write(threadA, old, "home-seed", "old-home");
    await sidecar.stop();
    await rm(old, { recursive: true });
    sidecar = new LiveSidecar([a, b, moved]);
    await sidecar.ready();
    assert.equal(await sidecar.inspect(threadA, "home-seed", seed.task, seed.turnId), seed.turnId);
    assert.equal(await sidecar.inspect(threadA, "home-seed", seed.task, null), seed.turnId);
    const threadB = await sidecar.bind(b);
    assert.notEqual(threadA, threadB);
    await Promise.all([
      sidecar.write(threadA, a, "home-cold", "user-a"),
      sidecar.write(threadB, b, "home-user-b", "user-b"),
    ]);
    await sidecar.write(threadA, moved, "home-hot", "user-a-moved");
    assert.equal(await readFile(path.join(a, "home-canary.txt"), "utf8"), "user-a\n");
    assert.equal(await readFile(path.join(b, "home-canary.txt"), "utf8"), "user-b\n");
    await sidecar.stop();

    // Official read-only projection verifies there were exactly the intended turns.
    const config = loadConfig(environment([a, b, moved]));
    const client = new CodexAppServerClient({ command: config.codexCommand, args: config.codexArgs, env: environment([a, b, moved]), requestTimeoutMs: 60_000, logger: new NullLogger() });
    try {
      for (const [threadId, expected] of [[threadA, 3], [threadB, 1]] as const) {
        const page = await client.request<{ data: unknown[]; nextCursor: string | null }>("thread/turns/list", { threadId, limit: 100, sortDirection: "asc" });
        assert.equal(page.data.length, expected);
        assert.equal(page.nextCursor, null);
      }
    } finally { await client.stop(); }
    t.diagnostic(JSON.stringify({ threadA, threadB, turnCounts: [3, 1], coldAndHotHomeWrites: true, removedOldHomeInspection: true, processCwd: "/" }));
  } finally {
    await sidecar.stop();
    await rm(root, { recursive: true, force: true });
  }
});
