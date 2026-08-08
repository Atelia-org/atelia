import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtemp, readFile, readdir, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { CodexBackend } from "../src/codex/backend.js";
import { CodexAppServerClient } from "../src/codex/client.js";
import { loadConfig } from "../src/config.js";
import { TaskStore } from "../src/codex/task-store.js";
import { NullLogger } from "../src/logger.js";
import { PathPolicy } from "../src/security/paths.js";

const runLive = process.env.CODEX_BRIDGE_RUN_LIVE === "1";

function makeBackend(root: string, pathPolicy: PathPolicy): CodexBackend {
  const logger = new NullLogger();
  const env: NodeJS.ProcessEnv = { ...process.env, CODEX_BRIDGE_ALLOWED_ROOTS: JSON.stringify([root]) };
  delete env.CODEX_BRIDGE_CODEX_ARGS;
  const config = loadConfig(env);
  const client = new CodexAppServerClient({
    command: config.codexCommand,
    args: config.codexArgs,
    requestTimeoutMs: 60_000,
    logger,
  });
  return new CodexBackend({
    client,
    pathPolicy,
    store: new TaskStore(12_000, 2_000),
    logger,
  });
}

test("real Codex investigates, survives bridge restart, and continues the same thread", { skip: !runLive }, async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "codex-bridge-live-"));
  execFileSync("git", ["init", "-q", root]);
  await writeFile(path.join(root, "README.md"), "# Tiny fixture\n\nA temporary integration-test repository.\n");
  const pathPolicy = await PathPolicy.create([root]);
  let backend = makeBackend(root, pathPolicy);

  try {
    const research = await backend.delegate({
      task: "Inspect this repository and tell me what it does. Do not modify files.",
      cwd: root,
      mode: "research",
      network: false,
      waitMs: 180_000,
    });
    assert.equal(research.status, "completed", research.errorMessage);
    assert.ok(research.result);
    assert.equal(await readFile(path.join(root, "README.md"), "utf8"), "# Tiny fixture\n\nA temporary integration-test repository.\n");
    assert.deepEqual((await readdir(root)).sort(), [".git", "README.md"]);

    const threadId = research.threadId;
    await backend.stop();
    backend = makeBackend(root, pathPolicy);
    const restored = await backend.read(threadId, "summary");
    assert.equal(restored.threadId, threadId);
    assert.equal(restored.status, "completed");

    const work = await backend.continue({
      threadId,
      task: "Create hello.txt containing exactly the line: hello from codex bridge",
      mode: "work",
      network: false,
      waitMs: 180_000,
    });
    assert.equal(work.threadId, threadId);
    assert.equal(work.status, "completed", work.errorMessage);
    assert.equal(await readFile(path.join(root, "hello.txt"), "utf8"), "hello from codex bridge\n");
  } finally {
    await backend.stop();
    await rm(root, { recursive: true });
  }
});
