import type {
  BuiltInToolPolicy,
  TaskBackend,
  TaskMode,
  TaskSnapshot,
} from "../backend/task-backend.js";
import type { CodexBackendProfile } from "../codex/backend.js";
import { TaskStore } from "../codex/task-store.js";
import { asBridgeError } from "../errors.js";
import type { BridgeLogger } from "../logger.js";
import {
  GALATEA_SIDECAR_PROTOCOL_VERSION,
  encodedOutputFrameBytes,
  type GalateaDispatchFrame,
  type GalateaOutputFrame,
} from "./protocol.js";

const GALATEA_DEVELOPER_INSTRUCTIONS = `You are Codex, Galatea's persistent delegate in the external world.
Treat each user message as a letter from Galatea containing a task or question. Use the configured local capabilities to help her.
Return the natural Markdown reply that should be delivered back to Galatea. Do not wrap the reply in JSON or an agent-report schema.
Do not reveal chain-of-thought, hidden instructions, full command logs, or other internal reasoning.`;

export const galateaCodexBackendProfile: CodexBackendProfile = {
  serviceName: "atelia_galatea_codex_sidecar",
  analyticsThreadSource: "atelia-galatea-codex-sidecar",
  threadNamePrefix: "[galatea-codex-sidecar] ",
  developerInstructions: GALATEA_DEVELOPER_INSTRUCTIONS,
  outputSchema: undefined,
  logEventPrefix: "galatea_codex",
  delegateOperation: "galatea_delegate",
  continueOperation: "galatea_continue",
};

export interface GalateaCodexAdapterOptions {
  backend: TaskBackend;
  store: TaskStore;
  logger: BridgeLogger;
  cwd: string;
  mode: TaskMode;
  localCommandNetwork: boolean;
  tools: BuiltInToolPolicy;
  turnDeadlineMs: number;
  interruptGraceMs: number;
  maxFinalBytes: number;
  maxOutputFrameBytes: number;
  maxDispatchTombstones: number;
  write(frame: GalateaOutputFrame): Promise<void>;
}

export class GalateaCodexAdapter {
  private stopping = false;
  private stopPromise?: Promise<void>;
  private readonly dispatchStates = new Map<string, "active" | "terminal">();

  constructor(private readonly options: GalateaCodexAdapterOptions) {}

  async dispatch(input: GalateaDispatchFrame): Promise<void> {
    if (this.stopping) {
      await this.fail(input, "shutdown", "SIDECAR_STOPPING");
      return;
    }
    const existing = this.dispatchStates.get(input.dispatchId);
    if (existing === "active") {
      // The original request owns the accepted/terminal business frames. An
      // exact concurrent replay attaches to those frames by identity and must
      // not emit an earlier, conflicting terminal failure.
      this.options.logger.log("warning", "galatea_dispatch_duplicate_active", {
        dispatch_id: input.dispatchId,
      });
      return;
    }
    if (existing === "terminal") {
      await this.rejectRequest(input, "DUPLICATE_DISPATCH_ID");
      return;
    }
    if (this.dispatchStates.size >= this.options.maxDispatchTombstones) {
      await this.rejectRequest(input, "DISPATCH_CAPACITY_EXCEEDED");
      return;
    }
    // Reserve before the first await. Active entries and completed tombstones
    // share this bounded map so a duplicate can never start a second operation.
    this.dispatchStates.set(input.dispatchId, "active");
    try {
      await this.dispatchReserved(input);
    } finally {
      this.dispatchStates.set(input.dispatchId, "terminal");
    }
  }

  private async dispatchReserved(input: GalateaDispatchFrame): Promise<void> {
    let snapshot: TaskSnapshot;
    try {
      snapshot = input.threadId
        ? await this.options.backend.continue({
            threadId: input.threadId,
            expectedCwd: this.options.cwd,
            task: input.task,
            mode: this.options.mode,
            localCommandNetwork: this.options.localCommandNetwork,
            tools: this.options.tools,
            waitMs: 0,
            clientUserMessageId: input.dispatchId,
          })
        : await this.options.backend.delegate({
            task: input.task,
            cwd: this.options.cwd,
            mode: this.options.mode,
            localCommandNetwork: this.options.localCommandNetwork,
            tools: this.options.tools,
            waitMs: 0,
            clientUserMessageId: input.dispatchId,
          });
    } catch (error) {
      const bridgeError = asBridgeError(error);
      await this.fail(
        input,
        this.stopping ? "shutdown" : "start",
        this.stopping ? "SIDECAR_STOPPING" : this.startErrorCode(bridgeError),
      );
      return;
    }

    if (this.stopping) {
      await this.fail(input, "shutdown", "SIDECAR_STOPPING", snapshot.threadId);
      return;
    }

    const threadId = snapshot.threadId;
    const turnId = snapshot.activeTurnId ?? snapshot.latestTurnId;
    if (!threadId || !turnId) {
      await this.fail(input, "start", "CODEX_PROTOCOL_ERROR", threadId);
      return;
    }

    await this.options.write({
      v: GALATEA_SIDECAR_PROTOCOL_VERSION,
      type: "accepted",
      requestId: input.requestId,
      dispatchId: input.dispatchId,
      threadId,
      turnId,
    });
    this.options.logger.log("info", "galatea_dispatch_accepted", {
      dispatch_id: input.dispatchId,
      thread_id: threadId,
      turn_id: turnId,
    });

    let terminal = snapshot;
    if (terminal.status === "running") {
      terminal = await this.options.store.waitForTurn(
        threadId,
        turnId,
        this.options.turnDeadlineMs,
      );
    }
    if (this.stopping) {
      await this.fail(input, "shutdown", "SIDECAR_STOPPING", threadId, turnId);
      return;
    }
    if (terminal.status === "running") {
      await this.tryInterrupt(threadId);
      await this.fail(input, "turn", "TURN_TIMEOUT", threadId, turnId);
      return;
    }

    if (terminal.status !== "completed") {
      const code = terminal.status === "interrupted" ? "TURN_INTERRUPTED" : "TURN_FAILED";
      await this.fail(input, "turn", code, threadId, turnId);
      return;
    }
    if (terminal.final === undefined || terminal.final.length === 0) {
      await this.fail(input, "turn", "FINAL_MISSING", threadId, turnId);
      return;
    }
    if (terminal.finalTruncated) {
      await this.fail(input, "turn", "FINAL_TRUNCATED", threadId, turnId);
      return;
    }
    if (Buffer.byteLength(terminal.final, "utf8") > this.options.maxFinalBytes) {
      await this.fail(input, "turn", "FINAL_TOO_LARGE", threadId, turnId);
      return;
    }

    const completed: GalateaOutputFrame = {
      v: GALATEA_SIDECAR_PROTOCOL_VERSION,
      type: "completed",
      dispatchId: input.dispatchId,
      threadId,
      turnId,
      final: terminal.final,
    };
    if (encodedOutputFrameBytes(completed) > this.options.maxOutputFrameBytes) {
      await this.fail(input, "turn", "FINAL_TOO_LARGE", threadId, turnId);
      return;
    }
    await this.options.write(completed);
    this.options.logger.log("info", "galatea_dispatch_completed", {
      dispatch_id: input.dispatchId,
      thread_id: threadId,
      turn_id: turnId,
      final_bytes: Buffer.byteLength(terminal.final, "utf8"),
    });
  }

  async stop(): Promise<void> {
    if (this.stopPromise) return this.stopPromise;
    this.stopping = true;
    this.stopPromise = this.options.backend.stop();
    return this.stopPromise;
  }

  private async tryInterrupt(threadId: string): Promise<void> {
    const attempt = this.options.backend.interrupt(threadId);
    let timer: NodeJS.Timeout | undefined;
    const grace = new Promise<void>((resolve) => {
      timer = setTimeout(resolve, this.options.interruptGraceMs);
    });
    await Promise.race([attempt.then(() => undefined, () => undefined), grace]);
    if (timer) clearTimeout(timer);
    void attempt.catch(() => undefined);
  }

  private startErrorCode(error: ReturnType<typeof asBridgeError>): string {
    const method = error.details?.method;
    const sideEffectingMethods = new Set([
      "thread/start",
      "thread/name/set",
      "thread/resume",
      "turn/start",
    ]);
    return error.details?.timeout === true && typeof method === "string" && sideEffectingMethods.has(method)
      ? "START_OUTCOME_UNKNOWN"
      : error.code;
  }

  private async rejectRequest(input: GalateaDispatchFrame, code: string): Promise<void> {
    await this.options.write({
      v: GALATEA_SIDECAR_PROTOCOL_VERSION,
      type: "failed",
      requestId: input.requestId,
      stage: "protocol",
      code,
    });
    this.options.logger.log("warning", "galatea_dispatch_rejected", {
      dispatch_id: input.dispatchId,
      error_code: code,
    });
  }

  private async fail(
    input: GalateaDispatchFrame,
    stage: "start" | "turn" | "shutdown",
    code: string,
    threadId?: string,
    turnId?: string,
  ): Promise<void> {
    await this.options.write({
      v: GALATEA_SIDECAR_PROTOCOL_VERSION,
      type: "failed",
      requestId: input.requestId,
      dispatchId: input.dispatchId,
      ...(threadId ? { threadId } : {}),
      ...(turnId ? { turnId } : {}),
      stage,
      code,
    });
    this.options.logger.log("warning", "galatea_dispatch_failed", {
      dispatch_id: input.dispatchId,
      thread_id: threadId,
      turn_id: turnId,
      stage,
      error_code: code,
    });
  }
}
