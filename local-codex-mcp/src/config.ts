import { BridgeError } from "./errors.js";

export type BridgeTransport = "stdio" | "http";

export interface BridgeConfig {
  allowedRoots: string[];
  defaultCwd?: string;
  codexCommand: string;
  codexArgs: string[];
  transport: BridgeTransport;
  httpHost: string;
  httpPort: number;
  defaultWaitMs: number;
  maxWaitMs: number;
  rpcTimeoutMs: number;
  maxResultChars: number;
  maxProgressChars: number;
  verbose: boolean;
}

function parseJsonStringArray(value: string | undefined, name: string, fallback?: string[]): string[] {
  if (value === undefined) {
    if (fallback !== undefined) return fallback;
    throw new BridgeError("INVALID_CONFIG", `${name} must be a JSON array of absolute paths.`);
  }

  try {
    const parsed: unknown = JSON.parse(value);
    if (!Array.isArray(parsed) || parsed.length === 0 || parsed.some((item) => typeof item !== "string")) {
      throw new Error("not a non-empty string array");
    }
    return parsed;
  } catch (error) {
    throw new BridgeError("INVALID_CONFIG", `${name} must be a non-empty JSON string array.`, {
      cause: error,
    });
  }
}

function parseInteger(
  value: string | undefined,
  fallback: number,
  name: string,
  minimum: number,
  maximum: number,
): number {
  const parsed = value === undefined ? fallback : Number(value);
  if (!Number.isInteger(parsed) || parsed < minimum || parsed > maximum) {
    throw new BridgeError("INVALID_CONFIG", `${name} must be an integer from ${minimum} to ${maximum}.`);
  }
  return parsed;
}

function parseBoolean(value: string | undefined, fallback: boolean): boolean {
  if (value === undefined) return fallback;
  if (value === "1" || value.toLowerCase() === "true") return true;
  if (value === "0" || value.toLowerCase() === "false") return false;
  throw new BridgeError("INVALID_CONFIG", `Invalid boolean value: ${value}`);
}

export function loadConfig(env: NodeJS.ProcessEnv = process.env): BridgeConfig {
  const transport = env.CODEX_BRIDGE_TRANSPORT ?? "stdio";
  if (transport !== "stdio" && transport !== "http") {
    throw new BridgeError("INVALID_CONFIG", "CODEX_BRIDGE_TRANSPORT must be stdio or http.");
  }

  const httpHost = env.CODEX_BRIDGE_HTTP_HOST ?? "127.0.0.1";
  const isLoopback = httpHost === "127.0.0.1" || httpHost === "localhost" || httpHost === "::1";
  if (!isLoopback && !parseBoolean(env.CODEX_BRIDGE_ALLOW_INSECURE_HTTP, false)) {
    throw new BridgeError(
      "INVALID_CONFIG",
      "Non-loopback HTTP requires CODEX_BRIDGE_ALLOW_INSECURE_HTTP=true. Prefer a protected reverse proxy.",
    );
  }

  const maxWaitMs = parseInteger(env.CODEX_BRIDGE_MAX_WAIT_MS, 60_000, "CODEX_BRIDGE_MAX_WAIT_MS", 1, 300_000);
  const defaultWaitMs = parseInteger(
    env.CODEX_BRIDGE_DEFAULT_WAIT_MS,
    20_000,
    "CODEX_BRIDGE_DEFAULT_WAIT_MS",
    0,
    maxWaitMs,
  );

  return {
    allowedRoots: parseJsonStringArray(env.CODEX_BRIDGE_ALLOWED_ROOTS, "CODEX_BRIDGE_ALLOWED_ROOTS"),
    defaultCwd: env.CODEX_BRIDGE_DEFAULT_CWD,
    codexCommand: env.CODEX_BRIDGE_CODEX_COMMAND ?? "codex",
    codexArgs: parseJsonStringArray(env.CODEX_BRIDGE_CODEX_ARGS, "CODEX_BRIDGE_CODEX_ARGS", [
      "app-server",
      "--stdio",
      "-c",
      "mcp_servers={}",
      "-c",
      "features.apps=false",
    ]),
    transport,
    httpHost,
    httpPort: parseInteger(env.CODEX_BRIDGE_HTTP_PORT, 3000, "CODEX_BRIDGE_HTTP_PORT", 1, 65_535),
    defaultWaitMs,
    maxWaitMs,
    rpcTimeoutMs: parseInteger(env.CODEX_BRIDGE_RPC_TIMEOUT_MS, 30_000, "CODEX_BRIDGE_RPC_TIMEOUT_MS", 100, 300_000),
    maxResultChars: parseInteger(env.CODEX_BRIDGE_MAX_RESULT_CHARS, 12_000, "CODEX_BRIDGE_MAX_RESULT_CHARS", 500, 100_000),
    maxProgressChars: parseInteger(env.CODEX_BRIDGE_MAX_PROGRESS_CHARS, 2_000, "CODEX_BRIDGE_MAX_PROGRESS_CHARS", 100, 20_000),
    verbose: parseBoolean(env.CODEX_BRIDGE_VERBOSE, false),
  };
}
