import assert from "node:assert/strict";
import { mkdtemp, mkdir, symlink, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { BridgeError } from "../src/errors.js";
import { PathPolicy } from "../src/security/paths.js";

test("allowed-root policy accepts descendants and rejects traversal, siblings, files, and symlink escapes", async (t) => {
  const base = await mkdtemp(path.join(os.tmpdir(), "codex-bridge-paths-"));
  const root = path.join(base, "root");
  const child = path.join(root, "child");
  const sibling = path.join(base, "root-sibling");
  await mkdir(child, { recursive: true });
  await mkdir(sibling);
  await writeFile(path.join(root, "file.txt"), "x");
  await symlink(sibling, path.join(root, "escape"), "dir");
  const policy = await PathPolicy.create([root], child);

  assert.equal(await policy.resolveCwd(root), root);
  assert.equal(await policy.resolveCwd(), child);
  await assert.rejects(policy.resolveCwd("relative"), (error: unknown) => error instanceof BridgeError && error.code === "INVALID_CWD");
  await assert.rejects(policy.resolveCwd(sibling), (error: unknown) => error instanceof BridgeError && error.code === "CWD_NOT_ALLOWED");
  await assert.rejects(policy.resolveCwd(path.join(root, "escape")), (error: unknown) => error instanceof BridgeError && error.code === "CWD_NOT_ALLOWED");
  await assert.rejects(policy.resolveCwd(path.join(root, "file.txt")), (error: unknown) => error instanceof BridgeError && error.code === "INVALID_CWD");
  await assert.rejects(policy.resolveCwd(path.join(root, "missing")), (error: unknown) => error instanceof BridgeError && error.code === "INVALID_CWD");

  t.after(async () => {
    const { rm } = await import("node:fs/promises");
    await rm(base, { recursive: true });
  });
});

