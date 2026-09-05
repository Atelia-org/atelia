import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import {
  installPinnedCodex,
  PINNED_CODEX_VERSION,
  pinnedCodexDirectory,
} from "../scripts/manage-pinned-codex.mjs";

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

test("pinned installer is idempotent for an exact existing installation", (t) => {
  const installRoot = mkdtempSync(join(tmpdir(), "atelia-pinned-codex-test-"));
  t.after(() => rmSync(installRoot, { recursive: true, force: true }));
  const fixture = createInstalledFixture(installRoot);

  const result = installPinnedCodex({ installRoot });

  assert.equal(result.installed, false);
  assert.equal(result.directory, fixture.directory);
  assert.equal(result.version, `codex-cli ${PINNED_CODEX_VERSION}`);
});

test("pinned installer refuses to replace a drifted version directory", (t) => {
  const installRoot = mkdtempSync(join(tmpdir(), "atelia-pinned-codex-test-"));
  t.after(() => rmSync(installRoot, { recursive: true, force: true }));
  createInstalledFixture(installRoot, "0.151.0");

  assert.throws(
    () => installPinnedCodex({ installRoot }),
    /Refusing to replace the existing pinned Codex directory/,
  );
});

test("pinned installer rejects a relative install root", () => {
  assert.throws(() => pinnedCodexDirectory("relative"), /must be absolute/);
});
