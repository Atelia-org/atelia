#!/usr/bin/env node

import { spawn } from "node:child_process";
import {
  mkdirSync,
  lstatSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { basename, isAbsolute, join } from "node:path";
import readline from "node:readline";
import { pinnedCodexEntrypoint, verifyPinnedCodex } from "./manage-pinned-codex.mjs";

const sourcePath = process.argv[2];
if (!sourcePath) {
  throw new Error("Usage: npm run canary:projection -- /absolute/path/to/rollout.jsonl");
}
if (!isAbsolute(sourcePath)) throw new Error("Projection fixture path must be absolute.");
const sourceStat = lstatSync(sourcePath);
if (!sourceStat.isFile() || sourceStat.isSymbolicLink()) {
  throw new Error("Projection fixture must be a no-follow regular file.");
}

function parseFixture(path) {
  const records = readFileSync(path, "utf8")
    .split("\n")
    .filter((line) => line.length > 0)
    .map((line) => JSON.parse(line));
  const sessionMeta = records.find((record) => record.type === "session_meta")?.payload;
  if (typeof sessionMeta?.id !== "string") throw new Error("Fixture is missing session_meta.payload.id.");

  const duplicateIndexes = [];
  for (let index = 1; index < records.length; index += 1) {
    if (records[index].ordinal === records[index - 1].ordinal) duplicateIndexes.push(index);
  }
  if (duplicateIndexes.length !== 1) {
    throw new Error(`Fixture must contain exactly one adjacent duplicate ordinal; found ${duplicateIndexes.length}.`);
  }
  const duplicateIndex = duplicateIndexes[0];
  const before = records[duplicateIndex - 1];
  const duplicate = records[duplicateIndex];
  if (before.type !== "event_msg"
      || before.payload?.type !== "token_count"
      || duplicate.type !== "event_msg"
      || duplicate.payload?.type !== "thread_settings_applied") {
    throw new Error("Fixture duplicate must be token_count followed by thread_settings_applied.");
  }
  const targetTurn = records
    .slice(duplicateIndex + 1)
    .find((record) => record.type === "turn_context" && typeof record.payload?.turn_id === "string");
  if (!targetTurn) throw new Error("Fixture has no turn_context after the duplicate ordinal.");
  return {
    records,
    threadId: sessionMeta.id,
    targetTurnId: targetTurn.payload.turn_id,
    duplicateOrdinal: duplicate.ordinal,
  };
}

class AppServerProbe {
  constructor(entrypoint, codexHome) {
    const environment = {
      PATH: process.env.PATH,
      LANG: process.env.LANG,
      LC_ALL: process.env.LC_ALL,
      CODEX_HOME: codexHome,
    };
    this.child = spawn(process.execPath, [
      entrypoint,
      "app-server",
      "--listen",
      "stdio://",
      "-c",
      "mcp_servers={}",
      "-c",
      "features.apps=false",
    ], { env: environment, stdio: ["pipe", "pipe", "pipe"] });
    this.nextId = 1;
    this.pending = new Map();
    this.stderr = "";
    this.lines = readline.createInterface({ input: this.child.stdout });
    this.lines.on("line", (line) => {
      const message = JSON.parse(line);
      if (message.id === undefined || message.method !== undefined) return;
      const pending = this.pending.get(message.id);
      if (!pending) return;
      this.pending.delete(message.id);
      clearTimeout(pending.timer);
      if (message.error) pending.reject(new Error(message.error.message ?? "app-server request failed"));
      else pending.resolve(message.result);
    });
    this.child.stderr.setEncoding("utf8");
    this.child.stderr.on("data", (chunk) => {
      this.stderr = `${this.stderr}${chunk}`.slice(-16 * 1024);
    });
    this.child.once("error", (error) => this.rejectPending(error));
    this.child.once("exit", (code, signal) => {
      if (this.pending.size === 0) return;
      this.rejectPending(new Error(
        `app-server exited before replying (code=${String(code)}, signal=${String(signal)})`,
      ));
    });
    this.child.stdin.once("error", (error) => this.rejectPending(error));
  }

  rejectPending(error) {
    for (const pending of this.pending.values()) {
      clearTimeout(pending.timer);
      pending.reject(error);
    }
    this.pending.clear();
  }

  request(method, params) {
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`app-server ${method} timed out`));
      }, 30_000);
      this.pending.set(id, { resolve, reject, timer });
      this.child.stdin.write(`${JSON.stringify({ id, method, params })}\n`);
    });
  }

  notify(method) {
    this.child.stdin.write(`${JSON.stringify({ method })}\n`);
  }

  async stop() {
    this.lines.close();
    if (this.child.exitCode !== null) return;
    this.child.kill("SIGTERM");
    let timer;
    const exited = new Promise((resolve) => this.child.once("exit", resolve));
    const timedOut = new Promise((resolve) => {
      timer = setTimeout(() => resolve("timeout"), 2_000);
    });
    if (await Promise.race([exited, timedOut]) === "timeout" && this.child.exitCode === null) {
      this.child.kill("SIGKILL");
      await exited;
    }
    clearTimeout(timer);
  }
}

const fixture = parseFixture(sourcePath);
const verification = verifyPinnedCodex();
const codexHome = mkdtempSync(join(tmpdir(), "atelia-codex-projection-canary-"));
const workspace = join(codexHome, "workspace");
const sessionDirectory = join(codexHome, "sessions", "2000", "01", "01");
mkdirSync(workspace, { recursive: true });
mkdirSync(sessionDirectory, { recursive: true });
const sanitizedRecords = fixture.records.map((record) => {
  if (record.type !== "session_meta") return record;
  return { ...record, payload: { ...record.payload, cwd: workspace, git: null } };
});
writeFileSync(
  join(sessionDirectory, basename(sourcePath)),
  `${sanitizedRecords.map((record) => JSON.stringify(record)).join("\n")}\n`,
);

const probe = new AppServerProbe(pinnedCodexEntrypoint(), codexHome);
try {
  await probe.request("initialize", {
    clientInfo: { name: "atelia_projection_canary", version: "1" },
    capabilities: { experimentalApi: false },
  });
  probe.notify("initialized");
  await probe.request("thread/resume", { threadId: fixture.threadId, excludeTurns: true });
  const page = await probe.request("thread/turns/list", {
    threadId: fixture.threadId,
    cursor: null,
    limit: 100,
    sortDirection: "desc",
    itemsView: "full",
  });
  const target = page.data?.find((turn) => turn.id === fixture.targetTurnId);
  if (!target) throw new Error("Official thread/turns/list did not expose the post-duplicate turn.");
  if (target.status !== "completed") {
    throw new Error(`Post-duplicate turn was visible but not completed: ${String(target.status)}.`);
  }
  const final = target.items?.find(
    (item) => item.type === "agentMessage" && item.phase === "final_answer" && item.text.length > 0,
  );
  if (!final) throw new Error("Post-duplicate completed turn has no visible final agent message.");
  process.stdout.write(
    `provider-free projection canary passed\n`
    + `codex: ${verification.version}\n`
    + `duplicate ordinal: ${String(fixture.duplicateOrdinal)}\n`
    + `post-duplicate turn: completed with final answer\n`,
  );
} catch (error) {
  const message = error instanceof Error ? error.message : String(error);
  throw new Error(`${message}\napp-server stderr tail:\n${probe.stderr}`, { cause: error });
} finally {
  try {
    await probe.stop();
  } finally {
    rmSync(codexHome, { recursive: true, force: true });
  }
}
