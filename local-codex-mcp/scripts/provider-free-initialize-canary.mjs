#!/usr/bin/env node

import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { CodexAppServerClient } from "../dist/src/codex/client.js";
import { NullLogger } from "../dist/src/logger.js";
import { pinnedCodexEntrypoint, verifyPinnedCodex } from "./manage-pinned-codex.mjs";

const codexHome = mkdtempSync(join(tmpdir(), "atelia-codex-initialize-canary-"));
const verification = await verifyPinnedCodex();
const client = new CodexAppServerClient({
  command: process.execPath,
  args: [
    pinnedCodexEntrypoint(),
    "app-server",
    "--listen",
    "stdio://",
    "-c",
    "mcp_servers={}",
    "-c",
    "features.apps=false",
  ],
  env: {
    PATH: process.env.PATH,
    LANG: process.env.LANG,
    LC_ALL: process.env.LC_ALL,
    CODEX_HOME: codexHome,
  },
  requestTimeoutMs: 30_000,
  logger: new NullLogger(),
});

try {
  await client.start();
  if (!client.isRunning || client.generation !== 1) {
    throw new Error("Pinned app-server did not complete the production initialize handshake.");
  }
  process.stdout.write(`provider-free initialize canary passed\ncodex: ${verification.version}\n`);
} finally {
  try {
    await client.stop();
  } finally {
    rmSync(codexHome, { recursive: true, force: true });
  }
}
