import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { PINNED_CODEX_VERSION as RUNTIME_PINNED_CODEX_VERSION } from "../dist/src/codex/pinned-version.js";
import {
  installPinnedCodex,
  PINNED_CODEX_VERSION,
  pinnedCodexDirectory,
  verifyContentTree,
} from "../scripts/manage-pinned-codex.mjs";

test("installer and runtime use one exact Codex version contract", () => {
  assert.equal(RUNTIME_PINNED_CODEX_VERSION, PINNED_CODEX_VERSION);
});

function createInstalledFixture(installRoot, version = PINNED_CODEX_VERSION) {
  const directory = pinnedCodexDirectory(installRoot);
  const rootPackage = join(directory, "node_modules", "@openai", "codex");
  const platformSuffix = `${process.platform}-${process.arch}`;
  const platformPackage = join(directory, "node_modules", "@openai", `codex-${platformSuffix}`);
  mkdirSync(join(rootPackage, "bin"), { recursive: true });
  mkdirSync(platformPackage, { recursive: true });
  writeFileSync(join(rootPackage, "package.json"), JSON.stringify({ version }));
  writeFileSync(
    join(platformPackage, "package.json"),
    JSON.stringify({ version: `${PINNED_CODEX_VERSION}-${platformSuffix}` }),
  );
  writeFileSync(
    join(rootPackage, "bin", "codex.js"),
    `process.stdout.write("codex-cli ${PINNED_CODEX_VERSION}\\n");\n`,
  );
  return { directory, rootPackage };
}

test("pinned installer refuses a fabricated same-version package tree", async (t) => {
  const installRoot = mkdtempSync(join(tmpdir(), "atelia-pinned-codex-test-"));
  t.after(() => rmSync(installRoot, { recursive: true, force: true }));
  createInstalledFixture(installRoot);

  await assert.rejects(
    installPinnedCodex({ installRoot }),
    /Refusing to replace the existing pinned Codex directory/,
  );
});

function sha256(value) {
  return createHash("sha256").update(value).digest("hex");
}

function createContentFixture(root) {
  const wrapper = "reviewed wrapper";
  const native = "reviewed native";
  mkdirSync(join(root, "bin"), { recursive: true });
  writeFileSync(join(root, "bin", "codex.js"), wrapper);
  writeFileSync(join(root, "bin", "codex"), native);
  return [
    { path: "bin/codex", bytes: Buffer.byteLength(native), sha256: sha256(native) },
    { path: "bin/codex.js", bytes: Buffer.byteLength(wrapper), sha256: sha256(wrapper) },
  ];
}

for (const [name, mutate] of [
  ["wrapper mutation", (root) => writeFileSync(join(root, "bin", "codex.js"), "changed wrapper")],
  ["native mutation", (root) => writeFileSync(join(root, "bin", "codex"), "changed native")],
  ["added file", (root) => writeFileSync(join(root, "bin", "extra"), "extra")],
  ["removed file", (root) => rmSync(join(root, "bin", "codex"))],
]) {
  test(`content verifier rejects ${name}`, async (t) => {
    const root = mkdtempSync(join(tmpdir(), "atelia-pinned-content-test-"));
    t.after(() => rmSync(root, { recursive: true, force: true }));
    const expected = createContentFixture(root);
    await verifyContentTree(root, expected);
    mutate(root);
    await assert.rejects(verifyContentTree(root, expected), /content|file set/);
  });
}

test("pinned installer rejects a relative install root", () => {
  assert.throws(() => pinnedCodexDirectory("relative"), /must be absolute/);
});
