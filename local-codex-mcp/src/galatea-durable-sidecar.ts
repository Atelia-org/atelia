import { pathToFileURL } from "node:url";
import type { Readable, Writable } from "node:stream";
import { CodexBackend } from "./codex/backend.js";
import { CodexAppServerClient } from "./codex/client.js";
import { TaskStore } from "./codex/task-store.js";
import { asBridgeError } from "./errors.js";
import {
  createGalateaCodexChildEnvironment,
  loadGalateaSidecarConfig,
  type GalateaSidecarConfig,
} from "./galatea/sidecar-config.js";
import {
  GalateaDurableAdapter,
} from "./galatea/durable-adapter.js";
import {
  encodeGalateaDurableOutputFrame,
  GALATEA_DURABLE_SIDECAR_PROTOCOL_VERSION,
  parseGalateaDurableFrame,
  type GalateaDurableInputFrame,
  type GalateaDurableOutputFrame,
} from "./galatea/durable-protocol.js";
import {
  galateaCodexBackendProfile,
} from "./galatea/backend-profile.js";
import {
  JsonlFrameWriter,
  readBoundedJsonLines,
} from "./galatea/jsonl.js";
import { JsonStderrLogger, type BridgeLogger } from "./logger.js";
import { PathPolicy } from "./security/paths.js";

export interface GalateaDurableJsonlAdapter {
  handle(input: GalateaDurableInputFrame): Promise<void>;
  stop(): Promise<void>;
}

export async function serveGalateaDurableJsonl(
  input: Readable,
  adapter: GalateaDurableJsonlAdapter,
  writer: JsonlFrameWriter<GalateaDurableOutputFrame>,
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
    for await (const line of readBoundedJsonLines(
      input,
      config.maxInputFrameBytes,
    )) {
      if (!line.ok) {
        await writer.write({
          v: GALATEA_DURABLE_SIDECAR_PROTOCOL_VERSION,
          type: "failed",
          stage: "protocol",
          code: line.code,
        });
        continue;
      }
      const parsed = parseGalateaDurableFrame(
        line.text,
        config.maxTaskBytes,
      );
      if (!parsed.ok) {
        await writer.write({
          v: GALATEA_DURABLE_SIDECAR_PROTOCOL_VERSION,
          type: "failed",
          stage: "protocol",
          code: parsed.code,
        });
        continue;
      }

      let task!: Promise<void>;
      task = adapter.handle(parsed.frame)
        .catch((error: unknown) => {
          recordFatal(error);
          logger.log("error", "galatea_durable_transport_failed", {
            request_id: parsed.frame.requestId,
            operation: parsed.frame.type,
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

export async function runGalateaDurableSidecar(
  input: Readable = process.stdin,
  output: Writable = process.stdout,
  env: NodeJS.ProcessEnv = process.env,
): Promise<void> {
  const config = loadGalateaSidecarConfig(env);
  const logger = new JsonStderrLogger(config.bridge.verbose);
  const pathPolicy = await PathPolicy.create(
    config.bridge.allowedRoots,
    config.cwd,
  );
  const client = new CodexAppServerClient({
    command: config.bridge.codexCommand,
    args: config.bridge.codexArgs,
    requestTimeoutMs: config.bridge.rpcTimeoutMs,
    logger,
    env: createGalateaCodexChildEnvironment(env),
  });
  const store = new TaskStore(
    config.maxFinalBytes,
    config.bridge.maxProgressChars,
  );
  const backend = new CodexBackend({
    client,
    pathPolicy,
    store,
    logger,
    profile: galateaCodexBackendProfile,
  });
  const writer = new JsonlFrameWriter<GalateaDurableOutputFrame>(
    output,
    config.maxOutputFrameBytes,
    config.outputWriteTimeoutMs,
    encodeGalateaDurableOutputFrame,
  );
  const adapter = new GalateaDurableAdapter({
    backend,
    logger,
    cwd: config.cwd,
    mode: config.mode,
    localCommandNetwork: config.localCommandNetwork,
    tools: config.tools,
    maximumFinalUtf8Bytes: config.maxFinalBytes,
    maximumOutputFrameBytes: config.maxOutputFrameBytes,
    write: (frame) => writer.write(frame),
  });

  try {
    await backend.start();
    await writer.write({
      v: GALATEA_DURABLE_SIDECAR_PROTOCOL_VERSION,
      type: "ready",
    });
    logger.log("info", "galatea_durable_sidecar_ready", {
      cwd: config.cwd,
      mode: config.mode,
      local_command_network: config.localCommandNetwork,
      web_search: config.tools.webSearch,
      image_generation: config.tools.imageGeneration,
      view_image: config.tools.viewImage,
    });
    await serveGalateaDurableJsonl(
      input,
      adapter,
      writer,
      config,
      logger,
    );
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
    await runGalateaDurableSidecar();
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
        event: "galatea_durable_sidecar_failed",
        error_code: bridgeError.code,
      })}\n`,
    );
    process.exitCode = 1;
  });
}
