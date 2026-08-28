import type { BuiltInToolPolicy, WebSearchMode } from "../backend/task-backend.js";
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
  cwd: string;
  mode: "research" | "work";
  localCommandNetwork: boolean;
  tools: BuiltInToolPolicy;
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

function boolean(value: string | undefined, fallback: boolean, name: string): boolean {
  if (value === undefined) return fallback;
  if (value === "1" || value === "true") return true;
  if (value === "0" || value === "false") return false;
  throw new BridgeError("INVALID_CONFIG", `${name} must be true, false, 1, or 0.`);
}

function webSearchMode(value: string | undefined): WebSearchMode {
  const mode = value ?? "live";
  if (mode === "disabled" || mode === "cached" || mode === "indexed" || mode === "live") {
    return mode;
  }
  throw new BridgeError(
    "INVALID_CONFIG",
    "GALATEA_CODEX_WEB_SEARCH must be disabled, cached, indexed, or live.",
  );
}

export function loadGalateaSidecarConfig(env: NodeJS.ProcessEnv = process.env): GalateaSidecarConfig {
  const bridge = loadConfig(env);
  const cwd = bridge.defaultCwd;
  if (!cwd) {
    throw new BridgeError(
      "INVALID_CONFIG",
      "CODEX_BRIDGE_DEFAULT_CWD is required for the Galatea sidecar.",
    );
  }
  const mode = env.GALATEA_CODEX_MODE ?? "work";
  if (mode !== "research" && mode !== "work") {
    throw new BridgeError("INVALID_CONFIG", "GALATEA_CODEX_MODE must be research or work.");
  }
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
    cwd,
    mode,
    localCommandNetwork: boolean(
      env.GALATEA_CODEX_LOCAL_COMMAND_NETWORK,
      true,
      "GALATEA_CODEX_LOCAL_COMMAND_NETWORK",
    ),
    tools: {
      webSearch: webSearchMode(env.GALATEA_CODEX_WEB_SEARCH),
      imageGeneration: boolean(
        env.GALATEA_CODEX_IMAGE_GENERATION,
        true,
        "GALATEA_CODEX_IMAGE_GENERATION",
      ),
      viewImage: boolean(
        env.GALATEA_CODEX_VIEW_IMAGE,
        true,
        "GALATEA_CODEX_VIEW_IMAGE",
      ),
    },
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
