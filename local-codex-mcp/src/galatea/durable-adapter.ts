import type {
  BuiltInToolPolicy,
  TaskMode,
} from "../backend/task-backend.js";
import type {
  GalateaDispatchInspection,
  GalateaStagedBackend,
} from "../backend/galatea-staged-backend.js";
import { asBridgeError } from "../errors.js";
import type { BridgeLogger } from "../logger.js";
import {
  encodedGalateaDurableOutputFrameBytes,
  GALATEA_DURABLE_SIDECAR_PROTOCOL_VERSION,
  type GalateaDurableFailureFrame,
  type GalateaDurableInputFrame,
  type GalateaDurableOutputFrame,
  type GalateaEnsureBindingFrame,
  type GalateaInspectDispatchFrame,
  type GalateaStartTurnFrame,
} from "./durable-protocol.js";

export interface GalateaDurableAdapterOptions {
  backend: GalateaStagedBackend;
  logger: BridgeLogger;
  cwd: string;
  mode: TaskMode;
  localCommandNetwork: boolean;
  tools: BuiltInToolPolicy;
  maximumFinalUtf8Bytes: number;
  maximumOutputFrameBytes: number;
  write(frame: GalateaDurableOutputFrame): Promise<void>;
}

export class GalateaDurableAdapter {
  private readonly activeDispatches = new Set<string>();
  private stopping = false;
  private stopPromise?: Promise<void>;

  constructor(private readonly options: GalateaDurableAdapterOptions) {}

  async handle(frame: GalateaDurableInputFrame): Promise<void> {
    if (this.stopping) {
      await this.writeShutdown(frame);
      return;
    }
    switch (frame.type) {
      case "ensure-binding":
        await this.ensureBinding(frame);
        return;
      case "start-turn":
        await this.startTurn(frame);
        return;
      case "inspect-dispatch":
        await this.inspectDispatch(frame);
        return;
    }
  }

  async stop(): Promise<void> {
    if (this.stopPromise) return this.stopPromise;
    this.stopping = true;
    this.stopPromise = this.options.backend.stop();
    return this.stopPromise;
  }

  private async ensureBinding(frame: GalateaEnsureBindingFrame): Promise<void> {
    try {
      const bound = await this.options.backend.ensureBinding({
        cwd: this.options.cwd,
        mode: this.options.mode,
        tools: this.options.tools,
      });
      await this.options.write({
        v: GALATEA_DURABLE_SIDECAR_PROTOCOL_VERSION,
        type: "binding-established",
        requestId: frame.requestId,
        bindingOperationId: frame.bindingOperationId,
        threadId: bound.threadId,
      });
    } catch (error) {
      const bridgeError = asBridgeError(error);
      await this.options.write({
        v: GALATEA_DURABLE_SIDECAR_PROTOCOL_VERSION,
        type: "failed",
        stage: "ensure-binding",
        requestId: frame.requestId,
        bindingOperationId: frame.bindingOperationId,
        code: this.bindingErrorCode(bridgeError),
      });
    }
  }

  private async startTurn(frame: GalateaStartTurnFrame): Promise<void> {
    if (this.activeDispatches.has(frame.dispatchId)) {
      await this.options.write({
        v: GALATEA_DURABLE_SIDECAR_PROTOCOL_VERSION,
        type: "failed",
        stage: "start-turn",
        requestId: frame.requestId,
        dispatchId: frame.dispatchId,
        threadId: frame.threadId,
        code: "DISPATCH_ALREADY_ACTIVE",
      });
      return;
    }
    this.activeDispatches.add(frame.dispatchId);
    try {
      const accepted = await this.options.backend.startBoundTurn({
        threadId: frame.threadId,
        expectedCwd: this.options.cwd,
        dispatchId: frame.dispatchId,
        task: frame.task,
        mode: this.options.mode,
        localCommandNetwork: this.options.localCommandNetwork,
        tools: this.options.tools,
      });
      await this.options.write({
        v: GALATEA_DURABLE_SIDECAR_PROTOCOL_VERSION,
        type: "turn-accepted",
        requestId: frame.requestId,
        dispatchId: frame.dispatchId,
        threadId: accepted.threadId,
        turnId: accepted.turnId,
      });
    } catch (error) {
      const bridgeError = asBridgeError(error);
      await this.options.write({
        v: GALATEA_DURABLE_SIDECAR_PROTOCOL_VERSION,
        type: "failed",
        stage: "start-turn",
        requestId: frame.requestId,
        dispatchId: frame.dispatchId,
        threadId: frame.threadId,
        code: this.startErrorCode(bridgeError),
      });
    } finally {
      this.activeDispatches.delete(frame.dispatchId);
    }
  }

  private async inspectDispatch(
    frame: GalateaInspectDispatchFrame,
  ): Promise<void> {
    try {
      const inspection = await this.options.backend.inspectDispatch({
        threadId: frame.threadId,
        expectedCwd: this.options.cwd,
        dispatchId: frame.dispatchId,
        task: frame.task,
        expectedTurnId: frame.expectedTurnId,
        maximumFinalUtf8Bytes: this.options.maximumFinalUtf8Bytes,
      });
      await this.writeInspection(frame, this.correlateInspection(frame, inspection));
    } catch (error) {
      const bridgeError = asBridgeError(error);
      await this.options.write({
        v: GALATEA_DURABLE_SIDECAR_PROTOCOL_VERSION,
        type: "failed",
        stage: "inspect-dispatch",
        requestId: frame.requestId,
        dispatchId: frame.dispatchId,
        threadId: frame.threadId,
        code: bridgeError.code === "CODEX_PROTOCOL_ERROR"
          || bridgeError.code === "CODEX_START_FAILED"
          ? "INSPECTION_UNAVAILABLE"
          : bridgeError.code,
      });
    }
  }

  private async writeInspection(
    frame: GalateaInspectDispatchFrame,
    inspection: GalateaDispatchInspection,
  ): Promise<void> {
    const base = {
      v: GALATEA_DURABLE_SIDECAR_PROTOCOL_VERSION,
      type: "dispatch-inspected" as const,
      requestId: frame.requestId,
      dispatchId: frame.dispatchId,
      threadId: frame.threadId,
    };
    let output: GalateaDurableOutputFrame;
    switch (inspection.kind) {
      case "not-found":
        output = { ...base, outcome: "not-found", source: inspection.source };
        break;
      case "unavailable":
        output = {
          ...base,
          outcome: "unavailable",
          source: inspection.source,
          turnId: inspection.turnId,
          code: inspection.code,
        };
        break;
      case "running":
        output = { ...base, outcome: "running", turnId: inspection.turnId, source: inspection.source };
        break;
      case "completed":
        output = {
          ...base,
          outcome: "completed",
          turnId: inspection.turnId,
          final: inspection.final,
          source: inspection.source,
        };
        if (encodedGalateaDurableOutputFrameBytes(output)
            > this.options.maximumOutputFrameBytes) {
          output = {
            ...base,
            outcome: "failed",
            turnId: inspection.turnId,
            code: "FINAL_TOO_LARGE",
            source: inspection.source,
          };
        }
        break;
      case "failed":
        output = {
          ...base,
          outcome: "failed",
          turnId: inspection.turnId,
          code: inspection.code,
          source: inspection.source,
        };
        break;
      case "ambiguous":
        output = { ...base, outcome: "ambiguous", code: inspection.code, source: inspection.source };
        break;
    }
    await this.options.write(output);
  }

  private correlateInspection(
    frame: GalateaInspectDispatchFrame,
    inspection: GalateaDispatchInspection,
  ): GalateaDispatchInspection {
    if (inspection.threadId !== frame.threadId) {
      return {
        kind: "ambiguous",
        threadId: frame.threadId,
        source: inspection.source,
        code: "THREAD_ID_MISMATCH",
      };
    }
    if (frame.expectedTurnId === null || inspection.kind === "ambiguous") return inspection;
    if (inspection.kind === "not-found") {
      return {
        kind: "ambiguous",
        threadId: frame.threadId,
        source: "persistent",
        code: "DISPATCH_TURN_MISMATCH",
      };
    }
    if (inspection.turnId !== frame.expectedTurnId) {
      return {
        kind: "ambiguous",
        threadId: frame.threadId,
        source: inspection.source,
        code: "DISPATCH_TURN_MISMATCH",
      };
    }
    return inspection;
  }

  private async writeShutdown(frame: GalateaDurableInputFrame): Promise<void> {
    let failure: GalateaDurableFailureFrame;
    if (frame.type === "ensure-binding") {
      failure = {
        v: GALATEA_DURABLE_SIDECAR_PROTOCOL_VERSION,
        type: "failed",
        stage: "ensure-binding",
        requestId: frame.requestId,
        bindingOperationId: frame.bindingOperationId,
        code: "SIDECAR_STOPPING",
      };
    } else {
      failure = {
        v: GALATEA_DURABLE_SIDECAR_PROTOCOL_VERSION,
        type: "failed",
        stage: "shutdown",
        requestId: frame.requestId,
        dispatchId: frame.dispatchId,
        threadId: frame.threadId,
        code: "SIDECAR_STOPPING",
      };
    }
    await this.options.write(failure);
  }

  private bindingErrorCode(error: ReturnType<typeof asBridgeError>): string {
    return error.code === "CODEX_PROTOCOL_ERROR"
      || error.code === "CODEX_START_FAILED"
      ? "BINDING_OUTCOME_UNKNOWN"
      : error.code;
  }

  private startErrorCode(error: ReturnType<typeof asBridgeError>): string {
    return error.code === "THREAD_NOT_FOUND"
      || error.code === "CWD_MISMATCH"
      || error.code === "BRIDGE_BUSY"
      ? error.code
      : "START_OUTCOME_UNKNOWN";
  }
}
