import assert from "node:assert/strict";
import test from "node:test";
import { loadConfig } from "../src/config.js";
import { PINNED_CODEX_ENTRYPOINT } from "../src/codex/pinned-version.js";

test("default Codex command uses the repo-local exact pin and supported stdio transport", () => {
  const config = loadConfig({ CODEX_BRIDGE_ALLOWED_ROOTS: "[\"/tmp\"]" });
  assert.equal(config.codexCommand, process.execPath);
  assert.deepEqual(config.codexArgs, [
    PINNED_CODEX_ENTRYPOINT,
    "app-server",
    "--listen",
    "stdio://",
    "-c",
    "mcp_servers={}",
    "-c",
    "features.apps=false",
  ]);
});

test("empty Codex command keeps the repo-local exact pin", () => {
  const config = loadConfig({
    CODEX_BRIDGE_ALLOWED_ROOTS: "[\"/tmp\"]",
    CODEX_BRIDGE_CODEX_COMMAND: "",
  });
  assert.equal(config.codexCommand, process.execPath);
  assert.equal(config.codexArgs[0], PINNED_CODEX_ENTRYPOINT);
});

test("explicit Codex command does not prepend the repo-local wrapper", () => {
  const config = loadConfig({
    CODEX_BRIDGE_ALLOWED_ROOTS: "[\"/tmp\"]",
    CODEX_BRIDGE_CODEX_COMMAND: "/opt/codex",
  });
  assert.equal(config.codexCommand, "/opt/codex");
  assert.equal(config.codexArgs[0], "app-server");
});
