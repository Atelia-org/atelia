#!/usr/bin/env node

// No provider calls or real auth/config/session files. Tests the pinned server's
// configuration resolution; production request omission is covered by backend tests.
import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { CodexAppServerClient } from "../dist/src/codex/client.js";
import { loadGalateaSidecarConfig } from "../dist/src/galatea/sidecar-config.js";
import { NullLogger } from "../dist/src/logger.js";

const home = mkdtempSync(join(tmpdir(), "atelia-codex-config-canary-"));
const cwd = join(home, "workspace");
const temporary = join(home, "temporary");
const shared = join(home, "shared");
for (const directory of [cwd, temporary, shared]) mkdirSync(directory);
writeFileSync(join(home, "config.toml"), `
model = "gpt-5.6"
sandbox_mode = "workspace-write"
approval_policy = "on-request"
[sandbox_workspace_write]
writable_roots = [${JSON.stringify(shared)}]
network_access = false
exclude_tmpdir_env_var = false
exclude_slash_tmp = false
[features]
apps = false
`);

const config = loadGalateaSidecarConfig({ CODEX_BRIDGE_ALLOWED_ROOTS: JSON.stringify([cwd]) });
const client = new CodexAppServerClient({
  command: config.bridge.codexCommand,
  args: config.bridge.codexArgs,
  env: { PATH: process.env.PATH, CODEX_HOME: home, TMPDIR: temporary },
  requestTimeoutMs: 15_000,
  logger: new NullLogger(),
});

async function persistWithoutProvider(threadId) {
  let timeout;
  let unsubscribe;
  try {
    const completed = new Promise((resolve, reject) => {
      timeout = setTimeout(() => reject(new Error("No shell turn completion.")), 15_000);
      unsubscribe = client.subscribe((event) => {
        if (event.method === "turn/completed" && event.params?.threadId === threadId) resolve();
      });
    });
    // Explicit no-op shell turn materializes only this disposable thread's history.
    await client.request("thread/shellCommand", { threadId, command: ":", timeoutMs: 1000 });
    await completed;
  } finally {
    clearTimeout(timeout);
    unsubscribe?.();
  }
}

try {
  await client.start();
  const inherited = await client.request("thread/start", { cwd, ephemeral: false });
  assert.equal(inherited.sandbox.type, "workspaceWrite");
  assert.equal(inherited.approvalPolicy, "on-request");
  assert.equal(inherited.sandbox.excludeTmpdirEnvVar, false);
  assert.equal(inherited.sandbox.excludeSlashTmp, false);
  assert.ok(inherited.sandbox.writableRoots.includes(shared));

  // A partial override must not erase sibling user-config sandbox settings.
  const partial = await client.request("thread/start", {
    cwd, ephemeral: true, config: { sandbox_workspace_write: { network_access: true } },
  });
  assert.equal(partial.sandbox.networkAccess, true);
  assert.equal(partial.sandbox.excludeTmpdirEnvVar, false);
  assert.ok(partial.sandbox.writableRoots.includes(shared));
  assert.equal(partial.approvalPolicy, "on-request");

  await persistWithoutProvider(inherited.thread.id);
  await client.stop();
  await client.start();
  const overrides = { sandbox_mode: "danger-full-access", approval_policy: "never" };
  const resumed = await client.request("thread/resume", {
    threadId: inherited.thread.id, cwd, excludeTurns: true, config: overrides,
  });
  assert.deepEqual(resumed.sandbox, { type: "dangerFullAccess" });
  assert.equal(resumed.approvalPolicy, "never");
  const warm = await client.request("thread/resume", {
    threadId: inherited.thread.id, cwd, excludeTurns: true, config: overrides,
  });
  assert.deepEqual(warm.sandbox, resumed.sandbox);
  assert.equal(warm.approvalPolicy, "never");

  const output = join(temporary, "read-write-probe");
  const executed = await client.request("command/exec", {
    cwd,
    sandboxPolicy: resumed.sandbox,
    command: [process.execPath, "-e", "const fs=require('node:fs');fs.writeFileSync(process.argv[1],'ok');if(fs.readFileSync(process.argv[1],'utf8')!=='ok')process.exit(1)", output],
    timeoutMs: 10_000,
  });
  assert.equal(executed.exitCode, 0, executed.stderr);
  assert.equal(readFileSync(output, "utf8"), "ok");
  process.stdout.write("provider-free config canary passed: inheritance, partial override, cold/warm resume, TMPDIR read/write\n");
} finally {
  try { await client.stop(); }
  finally { rmSync(home, { recursive: true, force: true }); }
}
