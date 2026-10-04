import assert from "node:assert/strict";
import test from "node:test";
import { BridgeError, bridgeErrorDiagnostic } from "../src/errors.js";

test("fatal sidecar diagnostics identify account routing 401 without remote content", () => {
  assert.deepEqual(bridgeErrorDiagnostic(new BridgeError(
    "CODEX_PROTOCOL_ERROR", "workspace routing discovery unauthorized (401)",
    { details: { method: "account/read", rpc_code: -32603, body: "secret" } },
  )), {
    error_code: "CODEX_PROTOCOL_ERROR", rpc_method: "account/read", rpc_code: -32603,
    reason: "workspace-routing-unauthorized",
  });
});

test("fatal sidecar diagnostics do not forward unknown methods or arbitrary messages", () => {
  const fields = bridgeErrorDiagnostic(new BridgeError("CODEX_PROTOCOL_ERROR", "secret", {
    details: { method: "secret", rpc_code: "secret", body: "secret", stack: "secret" },
  }));
  assert.deepEqual(fields, { error_code: "CODEX_PROTOCOL_ERROR" });
});
