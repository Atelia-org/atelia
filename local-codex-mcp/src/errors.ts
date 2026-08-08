export const bridgeErrorCodes = [
  "CODEX_NOT_FOUND",
  "CODEX_NOT_AUTHENTICATED",
  "CODEX_START_FAILED",
  "CODEX_PROTOCOL_ERROR",
  "THREAD_NOT_FOUND",
  "TURN_FAILED",
  "TURN_TIMEOUT",
  "INVALID_CWD",
  "CWD_NOT_ALLOWED",
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

