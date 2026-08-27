import { pathToFileURL } from "node:url";
import type { Readable, Writable } from "node:stream";
import { CodexBackend } from "./codex/backend.js";
import { CodexAppServerClient } from "./codex/client.js";
import { TaskStore } from "./codex/task-store.js";
import { loadConfig, type BridgeConfig } from "./config.js";
import { asBridgeError, BridgeError } from "./errors.js";
import {
  GalateaCodexAdapter,
  galateaCodexBackendProfile,
} from "./galatea/adapter.js";
import {
  DEFAULT_MAX_INPUT_FRAME_BYTES,
  DEFAULT_MAX_OUTPUT_FRAME_BYTES,
  DEFAULT_MAX_TASK_BYTES,
  DEFAULT_OUTPUT_WRITE_TIMEOUT_MS,
  GALATEA_SIDECAR_PROTOCOL_VERSION,
  JsonlFrameWriter,
  parseDispatchFrame,
  readBoundedJsonLines,
} from "./galatea/protocol.js";
import { JsonStderrLogger, type BridgeLogger } from "./logger.js";
import { PathPolicy } from "./security/paths.js";

const DEFAULT_TURN_DEADLINE_MS = 20 * 60 * 1000;
const DEFAULT_INTERRUPT_GRACE_MS = 2_000;
const DEFAULT_MAX_FINAL_BYTES = 128 * 1024;
const DEFAULT_MAX_DISPATCH_TOMBSTONES = 4_096;
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
  network: boolean;
  turnDeadlineMs: number;
  interruptGraceMs: number;
  maxInputFrameBytes: number;
  maxOutputFrameBytes: number;
  maxTaskBytes: number;
  maxFinalBytes: number;
  maxDispatchTombstones: number;
  outputWriteTimeoutMs: number;
}

export interface GalateaJsonlAdapter {
  dispatch(input: Parameters<GalateaCodexAdapter["dispatch"]>[0]): Promise<void>;
  stop(): Promise<void>;
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
    network: boolean(env.GALATEA_CODEX_NETWORK, false, "GALATEA_CODEX_NETWORK"),
    turnDeadlineMs: integer(
      env.GALATEA_CODEX_TURN_DEADLINE_MS,
      DEFAULT_TURN_DEADLINE_MS,
      "GALATEA_CODEX_TURN_DEADLINE_MS",
      100,
      24 * 60 * 60 * 1000,
    ),
    interruptGraceMs: integer(
      env.GALATEA_CODEX_INTERRUPT_GRACE_MS,
      DEFAULT_INTERRUPT_GRACE_MS,
      "GALATEA_CODEX_INTERRUPT_GRACE_MS",
      10,
      30_000,
    ),
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
    maxDispatchTombstones: integer(
      env.GALATEA_CODEX_MAX_DISPATCH_TOMBSTONES,
      DEFAULT_MAX_DISPATCH_TOMBSTONES,
      "GALATEA_CODEX_MAX_DISPATCH_TOMBSTONES",
      1,
      1_000_000,
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

export async function serveGalateaJsonl(
  input: Readable,
  adapter: GalateaJsonlAdapter,
  writer: JsonlFrameWriter,
  config: Pick<GalateaSidecarConfig, "maxInputFrameBytes" | "maxTaskBytes">,
  logger: BridgeLogger,
): Promise<void> {
  const active = new Set<Promise<void>>();
  let fatalError: unknown;
  const recordFatal = (error: unknown) => {
    fatalError ??= error;
    if (!input.destroyed) input.destroy();
  };
  try {
    for await (const line of readBoundedJsonLines(input, config.maxInputFrameBytes)) {
      if (!line.ok) {
        await writer.write({
          v: GALATEA_SIDECAR_PROTOCOL_VERSION,
          type: "failed",
          stage: "protocol",
          code: line.code,
        });
        continue;
      }
      const parsed = parseDispatchFrame(line.text, config.maxTaskBytes);
      if (!parsed.ok) {
        await writer.write({
          v: GALATEA_SIDECAR_PROTOCOL_VERSION,
          type: "failed",
          stage: "protocol",
          code: parsed.code,
        });
        continue;
      }

      let task!: Promise<void>;
      task = adapter.dispatch(parsed.frame)
        .catch((error: unknown) => {
          recordFatal(error);
          logger.log("error", "galatea_dispatch_transport_failed", {
            dispatch_id: parsed.frame.dispatchId,
            error_code: asBridgeError(error).code,
          });
        })
        .finally(() => active.delete(task));
      active.add(task);
    }
  } catch (error) {
    fatalError ??= error;
  } finally {
    try {
      await adapter.stop();
    } catch (error) {
      fatalError ??= error;
    }
    await Promise.allSettled([...active]);
    try {
      await writer.flush();
    } catch (error) {
      fatalError ??= error;
    }
  }
  if (fatalError !== undefined) throw fatalError;
}

export async function runGalateaSidecar(
  input: Readable = process.stdin,
  output: Writable = process.stdout,
  env: NodeJS.ProcessEnv = process.env,
): Promise<void> {
  const config = loadGalateaSidecarConfig(env);
  const logger = new JsonStderrLogger(config.bridge.verbose);
  const pathPolicy = await PathPolicy.create(config.bridge.allowedRoots, config.cwd);
  const client = new CodexAppServerClient({
    command: config.bridge.codexCommand,
    args: config.bridge.codexArgs,
    requestTimeoutMs: config.bridge.rpcTimeoutMs,
    logger,
    env: createGalateaCodexChildEnvironment(env),
  });
  const store = new TaskStore(config.maxFinalBytes, config.bridge.maxProgressChars);
  const backend = new CodexBackend({
    client,
    pathPolicy,
    store,
    logger,
    profile: galateaCodexBackendProfile,
  });
  const writer = new JsonlFrameWriter(
    output,
    config.maxOutputFrameBytes,
    config.outputWriteTimeoutMs,
  );
  const adapter = new GalateaCodexAdapter({
    backend,
    store,
    logger,
    cwd: config.cwd,
    mode: config.mode,
    network: config.network,
    turnDeadlineMs: config.turnDeadlineMs,
    interruptGraceMs: config.interruptGraceMs,
    maxFinalBytes: config.maxFinalBytes,
    maxOutputFrameBytes: config.maxOutputFrameBytes,
    maxDispatchTombstones: config.maxDispatchTombstones,
    write: (frame) => writer.write(frame),
  });

  try {
    await backend.start();
    await writer.write({ v: GALATEA_SIDECAR_PROTOCOL_VERSION, type: "ready" });
    logger.log("info", "galatea_sidecar_ready", {
      cwd: config.cwd,
      mode: config.mode,
      network: config.network,
    });
    await serveGalateaJsonl(input, adapter, writer, config, logger);
  } catch (error) {
    await adapter.stop().catch(() => undefined);
    throw error;
  }
}

async function main(): Promise<void> {
  const stopInput = () => process.stdin.destroy();
  process.once("SIGINT", stopInput);
  process.once("SIGTERM", stopInput);
  try {
    await runGalateaSidecar();
  } finally {
    process.off("SIGINT", stopInput);
    process.off("SIGTERM", stopInput);
  }
}

const entryPath = process.argv[1];
if (entryPath && import.meta.url === pathToFileURL(entryPath).href) {
  main().catch((error: unknown) => {
    const bridgeError = asBridgeError(error);
    process.stderr.write(
      `${JSON.stringify({
        timestamp: new Date().toISOString(),
        level: "error",
        event: "galatea_sidecar_failed",
        error_code: bridgeError.code,
      })}\n`,
    );
    process.exitCode = 1;
  });
}
