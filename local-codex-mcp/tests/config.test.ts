import assert from "node:assert/strict";
import test from "node:test";
import { loadConfig } from "../src/config.js";

test("default Codex args use the supported stdio transport", () => {
  const config = loadConfig({ CODEX_BRIDGE_ALLOWED_ROOTS: "[\"/tmp\"]" });
  assert.deepEqual(config.codexArgs, [
    "app-server",
    "--listen",
    "stdio://",
    "-c",
    "mcp_servers={}",
    "-c",
    "features.apps=false",
  ]);
});

test("empty Codex command falls back to PATH lookup", () => {
  const config = loadConfig({
    CODEX_BRIDGE_ALLOWED_ROOTS: "[\"/tmp\"]",
    CODEX_BRIDGE_CODEX_COMMAND: "",
  });
  assert.equal(config.codexCommand, "codex");
});
