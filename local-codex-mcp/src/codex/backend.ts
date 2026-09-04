import path from "node:path";
import type { JsonValue } from "../../schemas/serde_json/JsonValue.js";
import type { Account } from "../../schemas/v2/Account.js";
import type { GetAccountResponse } from "../../schemas/v2/GetAccountResponse.js";
import type { SandboxMode } from "../../schemas/v2/SandboxMode.js";
import type { SandboxPolicy } from "../../schemas/v2/SandboxPolicy.js";
import type { Thread } from "../../schemas/v2/Thread.js";
import type { ThreadReadResponse } from "../../schemas/v2/ThreadReadResponse.js";
import type { ThreadResumeResponse } from "../../schemas/v2/ThreadResumeResponse.js";
import type { ThreadStartResponse } from "../../schemas/v2/ThreadStartResponse.js";
import type { TurnInterruptResponse } from "../../schemas/v2/TurnInterruptResponse.js";
import type { TurnStartResponse } from "../../schemas/v2/TurnStartResponse.js";
import type {
  ContinueTaskInput,
  DelegateTaskInput,
  BuiltInToolPolicy,
  TaskBackend,
  TaskMode,
  TaskSnapshot,
} from "../backend/task-backend.js";
import type {
  EnsureGalateaBindingInput,
  GalateaBoundThread,
  GalateaDispatchInspection,
  GalateaStagedBackend,
  GalateaStartedTurn,
  InspectGalateaDispatchInput,
  StartGalateaBoundTurnInput,
} from "../backend/galatea-staged-backend.js";
import { BridgeError, asBridgeError } from "../errors.js";
import type { BridgeLogger } from "../logger.js";
import { PathPolicy } from "../security/paths.js";
import { CodexAppServerClient } from "./client.js";
import {
  classifyGalateaDispatch,
  DefaultGalateaDispatchInspectionLimits,
} from "./dispatch-inspection.js";
import type { JsonRpcNotification } from "./protocol.js";
import { agentReportJsonSchema } from "./report.js";
import { TaskStore } from "./task-store.js";

const DEVELOPER_INSTRUCTIONS = `You are the local execution subagent behind an MCP bridge.
Complete the requested task inside the supplied cwd and sandbox. Do not request privilege escalation.
Keep the final report concise: outcome, important findings, changed file paths, validation results, and warnings.
Never include chain-of-thought, full command logs, large diffs, or full file contents in the final report.`;

const MAXIMUM_VERIFIED_RUNNING_DISPATCHES = 32;
const MAXIMUM_VERIFIED_RUNNING_TASK_UTF8_BYTES = 128 * 1024;
const MAXIMUM_LIVE_RUNNING_TURN_PROOFS = 4_096;
const MAXIMUM_PENDING_START_TERMINAL_TOMBSTONES = 16;

export interface CodexBackendProfile {
  serviceName: string;
  /** Optional analytics hint sent only on thread/start; never durable ownership. */
  analyticsThreadSource: string;
  threadNamePrefix: string;
  developerInstructions: string;
  outputSchema?: JsonValue;
  logEventPrefix: string;
  delegateOperation: string;
  continueOperation: string;
}

export const mcpCodexBackendProfile: CodexBackendProfile = {
  serviceName: "atelia_local_codex_mcp",
  analyticsThreadSource: "atelia-local-codex-mcp",
  threadNamePrefix: "[local-codex-mcp] ",
  developerInstructions: DEVELOPER_INSTRUCTIONS,
  outputSchema: agentReportJsonSchema as unknown as JsonValue,
  logEventPrefix: "mcp_tool",
  delegateOperation: "codex_delegate",
  continueOperation: "codex_continue",
};

function coarseSandbox(mode: TaskMode): SandboxMode {
  return mode === "research" ? "read-only" : "workspace-write";
}

function preciseSandbox(mode: TaskMode, cwd: string, localCommandNetwork: boolean): SandboxPolicy {
  if (mode === "research") {
    return { type: "readOnly", networkAccess: localCommandNetwork };
  }
  return {
    type: "workspaceWrite",
    writableRoots: [cwd],
    networkAccess: localCommandNetwork,
    excludeTmpdirEnvVar: true,
    excludeSlashTmp: true,
  };
}

function threadConfig(tools: BuiltInToolPolicy): Record<string, unknown> {
  return {
    web_search: tools.webSearch,
    features: { image_generation: tools.imageGeneration },
    tools: { view_image: tools.viewImage },
  };
}

function sanitizeChangedFiles(files: readonly string[], cwd: string): string[] {
  const results: string[] = [];
  for (const file of files) {
    const absolute = path.isAbsolute(file) ? path.normalize(file) : path.resolve(cwd, file);
    const relative = path.relative(cwd, absolute);
    if (
      relative !== "" &&
      relative !== ".." &&
      !relative.startsWith(`..${path.sep}`) &&
      !path.isAbsolute(relative)
    ) {
      results.push(relative);
    }
  }
  return [...new Set(results)].slice(0, 100);
}

export interface CodexBackendOptions {
  client: CodexAppServerClient;
  pathPolicy: PathPolicy;
  store: TaskStore;
  logger: BridgeLogger;
  profile?: CodexBackendProfile;
}

interface VerifiedRunningDispatch {
  threadId: string;
  dispatchId: string;
  task: string;
  turnId: string;
}

interface PendingTurnStart {
  generation: number;
  terminalTurnIds: Set<string>;
  terminalOverflow: boolean;
}

type InspectionThreadRead =
  | { ok: true; thread: Thread }
  | { ok: false; inspection: GalateaDispatchInspection };

export class CodexBackend implements TaskBackend, GalateaStagedBackend {
  private authenticated = false;
  private readonly continueReservations = new Set<string>();
  private readonly profile: CodexBackendProfile;
  // This is only a warm-process optimization. Each thread can retain at most
  // its current fully verified running dispatch; terminal/cold paths always
  // return to durable full-history inspection.
  private readonly verifiedRunningDispatches = new Map<
    string,
    VerifiedRunningDispatch
  >();
  private readonly liveRunningTurnProofs = new Map<string, string>();
  private readonly pendingTurnStarts = new Map<string, PendingTurnStart>();
  private liveGeneration = 0;
  private stopped = false;
  private stopPromise?: Promise<void>;

  constructor(private readonly options: CodexBackendOptions) {
    this.profile = { ...(options.profile ?? mcpCodexBackendProfile) };
    this.options.client.subscribe((notification) => {
      if (notification.method === "bridge/processExited") {
        this.authenticated = false;
        this.resetLiveRunningState();
      } else {
        this.observeLiveTurnNotification(notification);
      }
      this.options.store.handleNotification(notification);
    });
  }

  async start(): Promise<void> {
    this.throwIfStopped();
    await this.ensureReady();
  }

  async stop(): Promise<void> {
    if (this.stopPromise) return this.stopPromise;
    this.stopped = true;
    this.authenticated = false;
    this.resetLiveRunningState();
    this.stopPromise = this.options.client.stop();
    return this.stopPromise;
  }

  async ensureBinding(
    input: EnsureGalateaBindingInput,
  ): Promise<GalateaBoundThread> {
    this.throwIfStopped();
    const cwd = await this.options.pathPolicy.resolveCwd(input.cwd);
    this.throwIfStopped();
    await this.ensureReady();
    this.throwIfStopped();
    const response = await this.options.client.request<ThreadStartResponse>("thread/start", {
      cwd,
      approvalPolicy: "never",
      approvalsReviewer: "user",
      sandbox: coarseSandbox(input.mode),
      config: threadConfig(input.tools),
      serviceName: this.profile.serviceName,
      developerInstructions: this.profile.developerInstructions,
      ephemeral: false,
      threadSource: this.profile.analyticsThreadSource,
    });
    this.throwIfStopped();
    await this.validateStartedThread(response.thread, cwd);
    this.throwIfStopped();
    await this.options.client.request("thread/name/set", {
      threadId: response.thread.id,
      name: this.ownershipName(response.thread.id),
    });
    this.throwIfStopped();
    const verified = await this.readOwnedThread(response.thread.id, true);
    await this.validateThreadCwd(verified, cwd);
    if (!Array.isArray(verified.turns) || verified.turns.length !== 0) {
      throw new BridgeError(
        "CODEX_PROTOCOL_ERROR",
        "A newly established Galatea binding must be an empty owned thread.",
      );
    }
    return { threadId: verified.id };
  }

  async startBoundTurn(
    input: StartGalateaBoundTurnInput,
  ): Promise<GalateaStartedTurn> {
    this.throwIfStopped();
    await this.ensureReady();
    this.throwIfStopped();
    if (this.continueReservations.has(input.threadId)) {
      throw new BridgeError("BRIDGE_BUSY", "This Codex thread already has an active turn.");
    }
    this.continueReservations.add(input.threadId);
    try {
      const preflight = await this.readOwnedThread(input.threadId, false);
      this.throwIfStopped();
      const persistedCwd = await this.options.pathPolicy.resolveCwd(preflight.cwd);
      const expectedCwd = await this.options.pathPolicy.resolveCwd(input.expectedCwd);
      this.throwIfStopped();
      if (persistedCwd !== expectedCwd) {
        throw new BridgeError("CWD_MISMATCH", "The Codex thread cwd differs from the configured cwd.");
      }
      if (this.options.store.hasRunning(input.threadId) || preflight.status.type === "active") {
        throw new BridgeError("BRIDGE_BUSY", "This Codex thread already has an active turn.");
      }

      const resumed = await this.options.client.request<ThreadResumeResponse>("thread/resume", {
        threadId: input.threadId,
        cwd: expectedCwd,
        approvalPolicy: "never",
        approvalsReviewer: "user",
        sandbox: coarseSandbox(input.mode),
        config: threadConfig(input.tools),
        developerInstructions: this.profile.developerInstructions,
      });
      this.throwIfStopped();
      await this.validateBoundGalateaThread(resumed.thread, input.threadId, expectedCwd);
      this.throwIfStopped();
      return await this.startTurnAccepted(
        input.threadId,
        input.task,
        expectedCwd,
        input.mode,
        input.localCommandNetwork,
        input.dispatchId,
      );
    } finally {
      this.continueReservations.delete(input.threadId);
    }
  }

  async inspectDispatch(
    input: InspectGalateaDispatchInput,
  ): Promise<GalateaDispatchInspection> {
    this.throwIfStopped();
    await this.ensureReady();
    this.throwIfStopped();
    const expectedCwd = await this.options.pathPolicy.resolveCwd(input.expectedCwd);
    const verified = this.verifiedRunningDispatches.get(input.threadId);
    if (
      verified?.threadId === input.threadId
      && verified.dispatchId === input.dispatchId
      && verified.task === input.task
      && this.hasLiveRunningTurnProof(input.threadId, verified.turnId)
    ) {
      const metadata = await this.readInspectionThread(
        input.threadId,
        expectedCwd,
        false,
      );
      if (!metadata.ok) {
        this.evictVerifiedRunningDispatch(input.threadId);
        return metadata.inspection;
      }
      if (
        metadata.thread.status.type === "active"
        && this.hasLiveRunningTurnProof(input.threadId, verified.turnId)
        && this.verifiedRunningDispatches.get(input.threadId) === verified
      ) {
        return {
          kind: "running",
          threadId: input.threadId,
          turnId: verified.turnId,
        };
      }
      this.evictVerifiedRunningDispatch(input.threadId);
    } else if (verified !== undefined) {
      this.evictVerifiedRunningDispatch(input.threadId);
    }

    const full = await this.readInspectionThread(
      input.threadId,
      expectedCwd,
      true,
    );
    if (!full.ok) return full.inspection;
    const inspection = classifyGalateaDispatch(
      full.thread,
      input.dispatchId,
      input.task,
      {
        ...DefaultGalateaDispatchInspectionLimits,
        maximumFinalUtf8Bytes: input.maximumFinalUtf8Bytes,
      },
    );
    if (
      inspection.kind === "running"
      && this.hasLiveRunningTurnProof(input.threadId, inspection.turnId)
    ) {
      this.tryCacheVerifiedRunningDispatch(input, inspection.turnId);
    } else {
      this.evictVerifiedRunningDispatch(input.threadId);
    }
    return inspection;
  }

  private tryCacheVerifiedRunningDispatch(
    input: InspectGalateaDispatchInput,
    turnId: string,
  ): void {
    this.evictVerifiedRunningDispatch(input.threadId);
    if (
      this.verifiedRunningDispatches.size
        >= MAXIMUM_VERIFIED_RUNNING_DISPATCHES
      || Buffer.byteLength(input.task, "utf8")
        > MAXIMUM_VERIFIED_RUNNING_TASK_UTF8_BYTES
    ) {
      return;
    }
    this.verifiedRunningDispatches.set(input.threadId, {
      threadId: input.threadId,
      dispatchId: input.dispatchId,
      task: input.task,
      turnId,
    });
  }

  private evictVerifiedRunningDispatch(
    threadId: string,
    expectedTurnId?: string,
  ): void {
    const verified = this.verifiedRunningDispatches.get(threadId);
    if (
      verified !== undefined
      && (expectedTurnId === undefined || verified.turnId === expectedTurnId)
    ) {
      this.verifiedRunningDispatches.delete(threadId);
    }
  }

  private clearVerifiedRunningDispatches(): void {
    this.verifiedRunningDispatches.clear();
  }

  private hasLiveRunningTurnProof(threadId: string, turnId: string): boolean {
    return this.liveRunningTurnProofs.get(threadId) === turnId;
  }

  private establishLiveRunningTurnProof(threadId: string, turnId: string): void {
    const current = this.liveRunningTurnProofs.get(threadId);
    if (current === turnId) return;
    this.evictVerifiedRunningDispatch(threadId);
    if (
      current === undefined
      && this.liveRunningTurnProofs.size >= MAXIMUM_LIVE_RUNNING_TURN_PROOFS
    ) {
      return;
    }
    this.liveRunningTurnProofs.set(threadId, turnId);
  }

  private evictLiveRunningTurnProof(
    threadId: string,
    expectedTurnId: string,
  ): void {
    if (this.liveRunningTurnProofs.get(threadId) === expectedTurnId) {
      this.liveRunningTurnProofs.delete(threadId);
    }
  }

  private resetLiveRunningState(): void {
    this.liveGeneration += 1;
    this.liveRunningTurnProofs.clear();
    this.pendingTurnStarts.clear();
    this.clearVerifiedRunningDispatches();
  }

  private observeLiveTurnNotification(
    notification: JsonRpcNotification,
  ): void {
    if (
      notification.method !== "turn/started"
      && notification.method !== "turn/completed"
    ) {
      return;
    }
    const params = notification.params;
    if (
      typeof params !== "object"
      || params === null
      || !("threadId" in params)
      || typeof params.threadId !== "string"
      || !("turn" in params)
      || typeof params.turn !== "object"
      || params.turn === null
      || !("id" in params.turn)
      || typeof params.turn.id !== "string"
    ) {
      return;
    }
    const threadId = params.threadId;
    const turnId = params.turn.id;
    if (notification.method === "turn/completed") {
      const pending = this.pendingTurnStarts.get(threadId);
      if (pending !== undefined) {
        if (
          pending.terminalTurnIds.size
            < MAXIMUM_PENDING_START_TERMINAL_TOMBSTONES
        ) {
          pending.terminalTurnIds.add(turnId);
        } else {
          pending.terminalOverflow = true;
        }
      }
      this.evictVerifiedRunningDispatch(params.threadId, params.turn.id);
      this.evictLiveRunningTurnProof(threadId, turnId);
      return;
    }
    const pending = this.pendingTurnStarts.get(threadId);
    if (
      pending?.terminalOverflow
      || pending?.terminalTurnIds.has(turnId)
    ) {
      return;
    }
    this.establishLiveRunningTurnProof(threadId, turnId);
  }

  private async readInspectionThread(
    threadId: string,
    expectedCwd: string,
    includeTurns: boolean,
  ): Promise<InspectionThreadRead> {
    let response: ThreadReadResponse;
    try {
      response = await this.options.client.request<ThreadReadResponse>("thread/read", {
        threadId,
        includeTurns,
      });
    } catch (error) {
      const bridgeError = asBridgeError(error);
      if (bridgeError.code === "THREAD_NOT_FOUND") {
        return {
          ok: false,
          inspection: {
            kind: "ambiguous",
            threadId,
            code: "THREAD_NOT_FOUND",
          },
        };
      }
      throw bridgeError;
    }
    this.throwIfStopped();
    const thread = response.thread;
    if (!thread || thread.id !== threadId) {
      return {
        ok: false,
        inspection: {
          kind: "ambiguous",
          threadId,
          code: "THREAD_ID_MISMATCH",
        },
      };
    }
    if (thread.name !== this.ownershipName(threadId)) {
      return {
        ok: false,
        inspection: {
          kind: "ambiguous",
          threadId,
          code: "THREAD_OWNERSHIP_MISMATCH",
        },
      };
    }
    let actualCwd: string;
    try {
      actualCwd = await this.options.pathPolicy.resolveCwd(thread.cwd);
    } catch {
      return {
        ok: false,
        inspection: {
          kind: "ambiguous",
          threadId,
          code: "THREAD_CWD_MISMATCH",
        },
      };
    }
    this.throwIfStopped();
    if (actualCwd !== expectedCwd) {
      return {
        ok: false,
        inspection: {
          kind: "ambiguous",
          threadId,
          code: "THREAD_CWD_MISMATCH",
        },
      };
    }
    return { ok: true, thread };
  }

  async delegate(input: DelegateTaskInput): Promise<TaskSnapshot> {
    this.throwIfStopped();
    const cwd = await this.options.pathPolicy.resolveCwd(input.cwd);
    this.throwIfStopped();
    await this.ensureReady();
    this.throwIfStopped();
    const startedAt = Date.now();
    try {
      const response = await this.options.client.request<ThreadStartResponse>("thread/start", {
        cwd,
        approvalPolicy: "never",
        approvalsReviewer: "user",
        sandbox: coarseSandbox(input.mode),
        config: threadConfig(input.tools),
        serviceName: this.profile.serviceName,
        developerInstructions: this.profile.developerInstructions,
        ephemeral: false,
        threadSource: this.profile.analyticsThreadSource,
      });
      this.throwIfStopped();
      await this.validateStartedThread(response.thread, cwd);
      this.throwIfStopped();
      await this.options.client.request("thread/name/set", {
        threadId: response.thread.id,
        name: this.ownershipName(response.thread.id),
      });
      this.throwIfStopped();
      const snapshot = await this.startTurn(
        response.thread.id,
        input.task,
        cwd,
        input.mode,
        input.localCommandNetwork,
        input.waitMs,
        input.clientUserMessageId,
      );
      this.logTask(this.profile.delegateOperation, cwd, snapshot, Date.now() - startedAt);
      return this.sanitizeSnapshot(snapshot, cwd);
    } catch (error) {
      const bridgeError = asBridgeError(error);
      this.logFailure(this.profile.delegateOperation, cwd, bridgeError, Date.now() - startedAt);
      throw bridgeError;
    }
  }

  async continue(input: ContinueTaskInput): Promise<TaskSnapshot> {
    this.throwIfStopped();
    await this.ensureReady();
    this.throwIfStopped();
    if (this.continueReservations.has(input.threadId)) {
      throw new BridgeError("BRIDGE_BUSY", "This Codex thread already has an active turn.");
    }
    this.continueReservations.add(input.threadId);
    const startedAt = Date.now();
    let cwd: string | undefined;
    try {
      const preflight = await this.readOwnedThread(input.threadId, false);
      this.throwIfStopped();
      const persistedCwd = await this.options.pathPolicy.resolveCwd(preflight.cwd);
      this.throwIfStopped();
      cwd = input.expectedCwd === undefined
        ? persistedCwd
        : await this.options.pathPolicy.resolveCwd(input.expectedCwd);
      this.throwIfStopped();
      if (persistedCwd !== cwd) {
        throw new BridgeError("CWD_MISMATCH", "The Codex thread cwd differs from the configured cwd.");
      }
      if (this.options.store.hasRunning(input.threadId) || preflight.status.type === "active") {
        throw new BridgeError("BRIDGE_BUSY", "This Codex thread already has an active turn.");
      }

      const resumed = await this.options.client.request<ThreadResumeResponse>("thread/resume", {
        threadId: input.threadId,
        cwd,
        approvalPolicy: "never",
        approvalsReviewer: "user",
        sandbox: coarseSandbox(input.mode),
        config: threadConfig(input.tools),
        developerInstructions: this.profile.developerInstructions,
      });
      this.throwIfStopped();
      await this.validatePersistedThread(
        resumed.thread,
        input.threadId,
        cwd,
      );
      this.throwIfStopped();
      const snapshot = await this.startTurn(
        input.threadId,
        input.task,
        cwd,
        input.mode,
        input.localCommandNetwork,
        input.waitMs,
        input.clientUserMessageId,
      );
      this.logTask(this.profile.continueOperation, cwd, snapshot, Date.now() - startedAt);
      return this.sanitizeSnapshot(snapshot, cwd);
    } catch (error) {
      const bridgeError = asBridgeError(error);
      this.logFailure(this.profile.continueOperation, cwd, bridgeError, Date.now() - startedAt, input.threadId);
      throw bridgeError;
    } finally {
      this.continueReservations.delete(input.threadId);
    }
  }

  async status(threadId: string): Promise<TaskSnapshot> {
    this.throwIfStopped();
    const startedAt = Date.now();
    let cwd: string | undefined;
    try {
      const loaded = await this.loadSnapshot(threadId);
      cwd = loaded.cwd;
      const snapshot = this.sanitizeSnapshot(loaded.snapshot, cwd);
      this.logTask("codex_status", cwd, snapshot, Date.now() - startedAt);
      return snapshot;
    } catch (error) {
      const bridgeError = asBridgeError(error);
      this.logFailure("codex_status", cwd, bridgeError, Date.now() - startedAt, threadId);
      throw bridgeError;
    }
  }

  async read(threadId: string, detail: "summary" | "final"): Promise<TaskSnapshot> {
    this.throwIfStopped();
    const startedAt = Date.now();
    let cwd: string | undefined;
    try {
      const loaded = await this.loadSnapshot(threadId);
      cwd = loaded.cwd;
      const sanitized = this.sanitizeSnapshot(loaded.snapshot, cwd);
      const snapshot = detail === "summary" ? { ...sanitized, final: undefined } : sanitized;
      this.logTask("codex_read", cwd, snapshot, Date.now() - startedAt);
      return snapshot;
    } catch (error) {
      const bridgeError = asBridgeError(error);
      this.logFailure("codex_read", cwd, bridgeError, Date.now() - startedAt, threadId);
      throw bridgeError;
    }
  }

  async interrupt(threadId: string): Promise<TaskSnapshot> {
    this.throwIfStopped();
    const startedAt = Date.now();
    let cwd: string | undefined;
    try {
      const loaded = await this.loadSnapshot(threadId);
      this.throwIfStopped();
      cwd = loaded.cwd;
      let snapshot = loaded.snapshot;
      if (snapshot.activeTurnId) {
        const turnId = snapshot.activeTurnId;
        this.throwIfStopped();
        await this.options.client.request<TurnInterruptResponse>("turn/interrupt", { threadId, turnId });
        this.throwIfStopped();
        snapshot = await this.options.store.waitForTurn(threadId, turnId, 3_000);
      }
      const sanitized = this.sanitizeSnapshot(snapshot, cwd);
      this.logTask("codex_interrupt", cwd, sanitized, Date.now() - startedAt);
      return sanitized;
    } catch (error) {
      const bridgeError = asBridgeError(error);
      this.logFailure("codex_interrupt", cwd, bridgeError, Date.now() - startedAt, threadId);
      throw bridgeError;
    }
  }

  private async ensureReady(): Promise<void> {
    this.throwIfStopped();
    await this.options.client.start();
    this.throwIfStopped();
    if (this.authenticated) return;
    const response = await this.options.client.request<GetAccountResponse>("account/read", {
      refreshToken: false,
    });
    this.throwIfStopped();
    if (!this.isAuthenticated(response.account)) {
      throw new BridgeError(
        "CODEX_NOT_AUTHENTICATED",
        "Codex is not authenticated. Run `codex login` locally, then retry.",
      );
    }
    this.authenticated = true;
  }

  private isAuthenticated(account: Account | null): account is Account {
    return account !== null;
  }

  private async startTurn(
    threadId: string,
    task: string,
    cwd: string,
    mode: TaskMode,
    localCommandNetwork: boolean,
    waitMs: number,
    clientUserMessageId?: string,
  ): Promise<TaskSnapshot> {
    const accepted = await this.startTurnAccepted(
      threadId,
      task,
      cwd,
      mode,
      localCommandNetwork,
      clientUserMessageId,
    );
    return this.options.store.waitForTurn(
      accepted.threadId,
      accepted.turnId,
      waitMs,
    );
  }

  private async startTurnAccepted(
    threadId: string,
    task: string,
    cwd: string,
    mode: TaskMode,
    localCommandNetwork: boolean,
    clientUserMessageId?: string,
  ): Promise<GalateaStartedTurn> {
    this.throwIfStopped();
    if (this.pendingTurnStarts.has(threadId)) {
      throw new BridgeError("BRIDGE_BUSY", "This Codex thread already has a pending turn start.");
    }
    const pending: PendingTurnStart = {
      generation: this.liveGeneration,
      terminalTurnIds: new Set<string>(),
      terminalOverflow: false,
    };
    this.pendingTurnStarts.set(threadId, pending);
    try {
      const response = await this.options.client.request<TurnStartResponse>("turn/start", {
        threadId,
        ...(clientUserMessageId === undefined ? {} : { clientUserMessageId }),
        input: [{ type: "text", text: task, text_elements: [] }],
        cwd,
        approvalPolicy: "never",
        approvalsReviewer: "user",
        sandboxPolicy: preciseSandbox(mode, cwd, localCommandNetwork),
        summary: "concise",
        ...(this.profile.outputSchema === undefined ? {} : { outputSchema: this.profile.outputSchema }),
      });
      this.throwIfStopped();
      if (!response.turn || typeof response.turn.id !== "string" || response.turn.id.length === 0) {
        throw new BridgeError("CODEX_PROTOCOL_ERROR", "Codex returned an invalid turn identity.");
      }
      const turnId = response.turn.id;
      const currentProof = this.liveRunningTurnProofs.get(threadId);
      if (
        pending.generation === this.liveGeneration
        && this.pendingTurnStarts.get(threadId) === pending
        && !pending.terminalOverflow
        && !pending.terminalTurnIds.has(turnId)
        && (currentProof === undefined || currentProof === turnId)
      ) {
        this.establishLiveRunningTurnProof(threadId, turnId);
      }
      this.options.store.beginTurn(threadId, turnId);
      return { threadId, turnId };
    } finally {
      if (this.pendingTurnStarts.get(threadId) === pending) {
        this.pendingTurnStarts.delete(threadId);
      }
    }
  }

  private async readOwnedThread(threadId: string, includeTurns: boolean): Promise<Thread> {
    this.throwIfStopped();
    const response = await this.options.client.request<ThreadReadResponse>("thread/read", {
      threadId,
      includeTurns,
    });
    this.throwIfStopped();
    if (
      response.thread.id !== threadId ||
      response.thread.name !== this.ownershipName(threadId)
    ) {
      throw new BridgeError("THREAD_NOT_FOUND", "The requested thread is not owned by this bridge.");
    }
    await this.options.pathPolicy.resolveCwd(response.thread.cwd);
    this.throwIfStopped();
    return response.thread;
  }

  private async loadSnapshot(threadId: string): Promise<{ snapshot: TaskSnapshot; cwd: string }> {
    this.throwIfStopped();
    await this.ensureReady();
    this.throwIfStopped();
    const preflight = await this.readOwnedThread(threadId, false);
    this.throwIfStopped();
    const cwd = await this.options.pathPolicy.resolveCwd(preflight.cwd);
    this.throwIfStopped();
    const thread = await this.readOwnedThread(threadId, true);
    this.throwIfStopped();
    return { snapshot: this.options.store.hydrate(thread), cwd };
  }

  private async validateStartedThread(thread: Thread, expectedCwd: string): Promise<void> {
    if (!thread || typeof thread.id !== "string" || thread.id.length === 0) {
      throw new BridgeError("CODEX_PROTOCOL_ERROR", "Codex returned an invalid thread identity.");
    }
    await this.validateThreadCwd(thread, expectedCwd);
  }

  private async validatePersistedThread(
    thread: Thread,
    expectedThreadId: string,
    expectedCwd: string,
  ): Promise<void> {
    if (
      thread.id !== expectedThreadId ||
      thread.name !== this.ownershipName(expectedThreadId)
    ) {
      throw new BridgeError("THREAD_NOT_FOUND", "The requested thread is not owned by this bridge.");
    }
    await this.validateThreadCwd(thread, expectedCwd);
  }

  private async validateBoundGalateaThread(
    thread: Thread,
    expectedThreadId: string,
    expectedCwd: string,
  ): Promise<void> {
    // A resume response may omit the persisted user-facing name. The binding's
    // ownership marker was verified by the immediately preceding thread/read;
    // this response only establishes that Codex resumed that exact thread/cwd.
    if (thread.id !== expectedThreadId) {
      throw new BridgeError("THREAD_NOT_FOUND", "The requested Galatea binding was not found.");
    }
    await this.validateThreadCwd(thread, expectedCwd);
  }

  private async validateThreadCwd(thread: Thread, expectedCwd: string): Promise<void> {
    const actualCwd = await this.options.pathPolicy.resolveCwd(thread.cwd);
    if (actualCwd !== expectedCwd) {
      throw new BridgeError("CODEX_PROTOCOL_ERROR", "Codex returned a different cwd than requested.");
    }
  }

  private sanitizeSnapshot(snapshot: TaskSnapshot, cwd: string): TaskSnapshot {
    return { ...snapshot, changedFiles: sanitizeChangedFiles(snapshot.changedFiles, cwd) };
  }

  private ownershipName(threadId: string): string {
    return `${this.profile.threadNamePrefix}${threadId}`;
  }

  private throwIfStopped(): void {
    if (this.stopped) {
      throw new BridgeError("CODEX_PROTOCOL_ERROR", "The Codex backend has stopped.");
    }
  }

  private logTask(tool: string, cwd: string, snapshot: TaskSnapshot, durationMs: number): void {
    this.options.logger.log("info", `${this.profile.logEventPrefix}_complete`, {
      tool,
      thread_id: snapshot.threadId,
      turn_id: snapshot.activeTurnId ?? snapshot.latestTurnId,
      cwd,
      duration_ms: durationMs,
      status: snapshot.status,
    });
  }

  private logFailure(
    tool: string,
    cwd: string | undefined,
    error: BridgeError,
    durationMs: number,
    threadId?: string,
  ): void {
    this.options.logger.log("error", `${this.profile.logEventPrefix}_failed`, {
      tool,
      thread_id: threadId,
      cwd,
      duration_ms: durationMs,
      status: "failed",
      error_code: error.code,
    });
  }
}
