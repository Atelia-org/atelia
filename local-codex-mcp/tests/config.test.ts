import assert from "node:assert/strict";
import test from "node:test";
import { loadConfig } from "../src/config.js";
import { PINNED_CODEX_ENTRYPOINT } from "../src/codex/pinned-version.js";
import { loadGalateaSidecarConfig } from "../src/galatea/sidecar-config.js";

test("Galatea inherits Codex defaults without implicit process or native config overrides", () => {
  for (const configValue of [undefined, "{}"]) {
    const config = loadGalateaSidecarConfig({
      CODEX_BRIDGE_ALLOWED_ROOTS: '["/tmp"]',
      GALATEA_CODEX_CONFIG: configValue,
    });
    assert.equal(config.codexConfig, undefined);
    assert.deepEqual(config.bridge.codexArgs, [PINNED_CODEX_ENTRYPOINT, "app-server", "--listen", "stdio://"]);
  }
});

test("Galatea preserves native config values and rejects non-object overrides", () => {
  const explicit = { features: { apps: false }, sandbox_workspace_write: { writable_roots: ["/tmp/shared"] }, model_context_window: 100000 };
  assert.deepEqual(loadGalateaSidecarConfig({
    CODEX_BRIDGE_ALLOWED_ROOTS: '["/tmp"]',
    GALATEA_CODEX_CONFIG: JSON.stringify(explicit),
  }).codexConfig, explicit);
  for (const value of ["null", "[]", "true", '"text"', "{"]) {
    assert.throws(() => loadGalateaSidecarConfig({
      CODEX_BRIDGE_ALLOWED_ROOTS: '["/tmp"]', GALATEA_CODEX_CONFIG: value,
    }), /GALATEA_CODEX_CONFIG must be a JSON object/);
  }
});

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

test("args-only override retains the repo-local exact entrypoint", () => {
  const config = loadConfig({
    CODEX_BRIDGE_ALLOWED_ROOTS: "[\"/tmp\"]",
    CODEX_BRIDGE_CODEX_ARGS: "[\"app-server\",\"--listen\",\"stdio://\"]",
  });
  assert.equal(config.codexCommand, process.execPath);
  assert.deepEqual(config.codexArgs, [
    PINNED_CODEX_ENTRYPOINT,
    "app-server",
    "--listen",
    "stdio://",
  ]);
});
