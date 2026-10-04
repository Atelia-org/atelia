export const bridgeErrorCodes = [
  "CODEX_NOT_FOUND",
  "CODEX_VERSION_MISMATCH",
  "CODEX_NOT_AUTHENTICATED",
  "CODEX_START_FAILED",
  "CODEX_PROTOCOL_ERROR",
  "THREAD_NOT_FOUND",
  "TURN_FAILED",
  "TURN_TIMEOUT",
  "INVALID_CWD",
  "CWD_NOT_ALLOWED",
  "CWD_MISMATCH",
  "SANDBOX_DENIED",
  "NETWORK_DENIED",
  "BRIDGE_BUSY",
  "INVALID_CONFIG",
] as const;

export type BridgeErrorCode = (typeof bridgeErrorCodes)[number];

export class BridgeError extends Error {
  readonly code: BridgeErrorCode;
  readonly details?: Record<string, unknown>;

  constructor(
    code: BridgeErrorCode,
    message: string,
    options?: { cause?: unknown; details?: Record<string, unknown> },
  ) {
    super(message, { cause: options?.cause });
    this.name = "BridgeError";
    this.code = code;
    this.details = options?.details;
  }
}

/** Content-free facts for fatal sidecar errors; remote messages are never logged. */
export function bridgeErrorDiagnostic(error: BridgeError): Record<string, unknown> {
  const fields: Record<string, unknown> = { error_code: error.code };
  const method = error.details?.method;
  if (typeof method === "string" && [
    "initialize", "account/read", "thread/start", "thread/name/set",
    "thread/read", "thread/resume", "thread/turns/list", "turn/start",
  ].includes(method)) fields.rpc_method = method;
  const rpcCode = error.details?.rpc_code;
  if (typeof rpcCode === "number" && Number.isInteger(rpcCode)
    && rpcCode >= -2_147_483_648 && rpcCode <= 2_147_483_647) fields.rpc_code = rpcCode;
  if (method === "account/read" && error.message === "workspace routing discovery unauthorized (401)") {
    fields.reason = "workspace-routing-unauthorized";
  }
  return fields;
}

export function asBridgeError(error: unknown): BridgeError {
  if (error instanceof BridgeError) {
    return error;
  }

  const message = error instanceof Error ? error.message : String(error);
  const normalized = message.toLowerCase();

  if (normalized.includes("no such file") || normalized.includes("enoent")) {
    return new BridgeError("CODEX_NOT_FOUND", "The configured Codex executable was not found.", {
      cause: error,
    });
  }
  if (normalized.includes("thread") && normalized.includes("not found")) {
    return new BridgeError("THREAD_NOT_FOUND", "The requested Codex thread was not found.", {
      cause: error,
    });
  }
  if (normalized.includes("sandbox")) {
    return new BridgeError("SANDBOX_DENIED", "Codex denied the operation under the configured sandbox.", {
      cause: error,
    });
  }
  if (normalized.includes("network") && (normalized.includes("denied") || normalized.includes("disabled"))) {
    return new BridgeError("NETWORK_DENIED", "Network access is disabled for this Codex turn.", {
      cause: error,
    });
  }

  return new BridgeError("CODEX_PROTOCOL_ERROR", "The Codex app-server request failed.", {
    cause: error,
  });
}
