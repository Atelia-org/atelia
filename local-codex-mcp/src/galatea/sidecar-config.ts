import type { JsonValue } from "../../schemas/serde_json/JsonValue.js";
import { loadConfig, type BridgeConfig } from "../config.js";
import { BridgeError } from "../errors.js";
import {
  DEFAULT_MAX_INPUT_FRAME_BYTES,
  DEFAULT_MAX_FINAL_BYTES,
  DEFAULT_MAX_OUTPUT_FRAME_BYTES,
  DEFAULT_MAX_TASK_BYTES,
  DEFAULT_OUTPUT_WRITE_TIMEOUT_MS,
} from "./limits.js";

const GALATEA_PARENT_CODEX_CONTEXT_KEYS = [
  "CODEX_SESSION_ID",
  "CODEX_THREAD_ID",
  "CODEX_INTERNAL_ORIGINATOR_OVERRIDE",
  "CODEX_PERMISSION_PROFILE",
  "CODEX_CI",
] as const;

export interface GalateaSidecarConfig {
  bridge: BridgeConfig;
  codexConfig?: Record<string, JsonValue>;
  maxInputFrameBytes: number;
  maxOutputFrameBytes: number;
  maxTaskBytes: number;
  maxFinalBytes: number;
  outputWriteTimeoutMs: number;
}

export function createGalateaCodexChildEnvironment(
  inherited: NodeJS.ProcessEnv,
): NodeJS.ProcessEnv {
  const environment = { ...inherited };
  for (const key of GALATEA_PARENT_CODEX_CONTEXT_KEYS) delete environment[key];
  return environment;
}

function integer(
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

function readCodexConfig(value: string | undefined): Record<string, JsonValue> | undefined {
  if (value === undefined) return undefined;
  let parsed: unknown;
  try { parsed = JSON.parse(value); } catch {
    throw new BridgeError("INVALID_CONFIG", "GALATEA_CODEX_CONFIG must be a JSON object.");
  }
  if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) {
    throw new BridgeError("INVALID_CONFIG", "GALATEA_CODEX_CONFIG must be a JSON object.");
  }
  // Codex owns the configuration schema. Preserve explicit false values and
  // native nested tables; an empty object has the same semantics as omission.
  return Object.keys(parsed).length > 0 ? parsed as Record<string, JsonValue> : undefined;
}

export function loadGalateaSidecarConfig(env: NodeJS.ProcessEnv = process.env): GalateaSidecarConfig {
  const bridge = loadConfig({
    ...env,
    // The generic MCP bridge has its own restrictive defaults. Galatea only
    // adds native Codex overrides explicitly supplied by its operator.
    CODEX_BRIDGE_CODEX_ARGS: env.CODEX_BRIDGE_CODEX_ARGS ?? JSON.stringify(["app-server", "--listen", "stdio://"]),
  });
  const maxInputFrameBytes = integer(
    env.GALATEA_CODEX_MAX_INPUT_FRAME_BYTES,
    DEFAULT_MAX_INPUT_FRAME_BYTES,
    "GALATEA_CODEX_MAX_INPUT_FRAME_BYTES",
    1024,
    1024 * 1024,
  );
  const maxTaskBytes = integer(
    env.GALATEA_CODEX_MAX_TASK_BYTES,
    DEFAULT_MAX_TASK_BYTES,
    "GALATEA_CODEX_MAX_TASK_BYTES",
    1,
    maxInputFrameBytes,
  );

  return {
    bridge,
    codexConfig: readCodexConfig(env.GALATEA_CODEX_CONFIG),
    maxInputFrameBytes,
    maxOutputFrameBytes: integer(
      env.GALATEA_CODEX_MAX_OUTPUT_FRAME_BYTES,
      DEFAULT_MAX_OUTPUT_FRAME_BYTES,
      "GALATEA_CODEX_MAX_OUTPUT_FRAME_BYTES",
      1024,
      8 * 1024 * 1024,
    ),
    maxTaskBytes,
    maxFinalBytes: integer(
      env.GALATEA_CODEX_MAX_FINAL_BYTES,
      DEFAULT_MAX_FINAL_BYTES,
      "GALATEA_CODEX_MAX_FINAL_BYTES",
      1,
      1024 * 1024,
    ),
    outputWriteTimeoutMs: integer(
      env.GALATEA_CODEX_OUTPUT_WRITE_TIMEOUT_MS,
      DEFAULT_OUTPUT_WRITE_TIMEOUT_MS,
      "GALATEA_CODEX_OUTPUT_WRITE_TIMEOUT_MS",
      100,
      60_000,
    ),
  };
}
