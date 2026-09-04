import path from "node:path";
import type { JsonValue } from "../../schemas/serde_json/JsonValue.js";
import type { Account } from "../../schemas/v2/Account.js";
import type { GetAccountResponse } from "../../schemas/v2/GetAccountResponse.js";
import type { SandboxMode } from "../../schemas/v2/SandboxMode.js";
import type { SandboxPolicy } from "../../schemas/v2/SandboxPolicy.js";
import type { Thread } from "../../schemas/v2/Thread.js";
import type { ThreadItem } from "../../schemas/v2/ThreadItem.js";
import type { ThreadItemEntry } from "../../schemas/v2/ThreadItemEntry.js";
import type { ThreadItemsListResponse } from "../../schemas/v2/ThreadItemsListResponse.js";
import type { ThreadReadResponse } from "../../schemas/v2/ThreadReadResponse.js";
import type { ThreadResumeResponse } from "../../schemas/v2/ThreadResumeResponse.js";
import type { ThreadStartResponse } from "../../schemas/v2/ThreadStartResponse.js";
import type { ThreadTurnsListResponse } from "../../schemas/v2/ThreadTurnsListResponse.js";
import type { Turn } from "../../schemas/v2/Turn.js";
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
import { classifyTurnEvidence, DefaultGalateaDispatchInspectionLimits, hasExactTaskBody } from "./dispatch-inspection.js";
import { LiveTurnObservations } from "./live-turn-observations.js";
import type { JsonRpcNotification } from "./protocol.js";
import { agentReportJsonSchema } from "./report.js";
import { TaskStore } from "./task-store.js";

const DEVELOPER_INSTRUCTIONS = `You are the local execution subagent behind an MCP bridge.
Complete the requested task inside the supplied cwd and sandbox. Do not request privilege escalation.
Keep the final report concise: outcome, important findings, changed file paths, validation results, and warnings.
Never include chain-of-thought, full command logs, large diffs, or full file contents in the final report.`;

const MAXIMUM_LIVE_TURN_OBSERVATIONS = 4_096;
const DEFAULT_MCP_LIVE_FINAL_UTF8_BYTES = 128 * 1024;
const INSPECTION_PAGE_SIZE = 100;

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
  galateaMaximumFinalUtf8Bytes?: number;
}

type InspectionThreadRead =
  | { ok: true; thread: Thread }
  | { ok: false; inspection: GalateaDispatchInspection };

type PersistentInspectionCode = Extract<
  GalateaDispatchInspection,
  { kind: "ambiguous" }
>["code"];

class PersistentInspectionError extends Error {
  constructor(readonly code: PersistentInspectionCode) {
    super(code);
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isThreadItem(value: unknown): value is ThreadItem {
  if (!isRecord(value) || typeof value.id !== "string" || typeof value.type !== "string") return false;
  if (value.type === "userMessage") {
    return (value.clientId === null || typeof value.clientId === "string")
      && Array.isArray(value.content);
  }
  if (value.type === "agentMessage") {
    return typeof value.text === "string"
      && (value.phase === null || value.phase === "commentary" || value.phase === "final_answer");
  }
  return true;
}

function isTurn(value: unknown): value is Turn {
  return isRecord(value)
    && typeof value.id === "string"
    && Array.isArray(value.items)
    && typeof value.itemsView === "string"
    && typeof value.status === "string";
}

function isItemEntry(value: unknown): value is ThreadItemEntry {
  return isRecord(value) && typeof value.turnId === "string" && value.turnId.length > 0
    && isThreadItem(value.item);
}

function validatePage<T>(value: unknown): { data: T[]; nextCursor: string | null } {
  if (!isRecord(value) || !Array.isArray(value.data)
      || (value.nextCursor !== null && typeof value.nextCursor !== "string")) {
    throw new PersistentInspectionError("PAGE_SHAPE_INVALID");
  }
  return { data: value.data as T[], nextCursor: value.nextCursor };
}

function nextCursor(
  value: string | null,
  pageLength: number,
  seen: Set<string>,
): string | null {
  if (value === null) return null;
  if (value.length === 0) throw new PersistentInspectionError("PAGINATION_CURSOR_INVALID");
  if (pageLength === 0) throw new PersistentInspectionError("PAGE_SHAPE_INVALID");
  if (seen.has(value)) throw new PersistentInspectionError("PAGINATION_CURSOR_LOOP");
  seen.add(value);
  return value;
}

export class CodexBackend implements TaskBackend, GalateaStagedBackend {
  private authenticated = false;
  private readonly continueReservations = new Set<string>();
  private readonly profile: CodexBackendProfile;
  private readonly liveObservations: LiveTurnObservations;
  private stopped = false;
  private stopPromise?: Promise<void>;

  constructor(private readonly options: CodexBackendOptions) {
    this.profile = { ...(options.profile ?? mcpCodexBackendProfile) };
    this.liveObservations = new LiveTurnObservations({
      maximumObservations: MAXIMUM_LIVE_TURN_OBSERVATIONS,
      maximumFinalUtf8Bytes: options.galateaMaximumFinalUtf8Bytes
        ?? DEFAULT_MCP_LIVE_FINAL_UTF8_BYTES,
    });
    this.options.client.subscribe((notification) => {
      if (notification.method === "bridge/processExited") {
        this.authenticated = false;
        this.liveObservations.clear();
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
    this.liveObservations.clear();
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
    const verified = await this.readOwnedThread(response.thread.id, false);
    await this.validateThreadCwd(verified, cwd);
    const existingTurns = await this.listTurns(response.thread.id, 1);
    if (existingTurns.length !== 0) {
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
    const metadata = await this.readInspectionThread(input.threadId, expectedCwd);
    if (!metadata.ok) return metadata.inspection;

    if (input.expectedTurnId !== null) {
      const live = this.liveObservations.inspect(
        input.threadId,
        input.expectedTurnId,
        input.dispatchId,
        input.task,
      );
      if (live) return live;
    }

    try {
      return input.expectedTurnId === null
        ? await this.inspectUnknownDispatch(input)
        : await this.inspectAcceptedTurn(input, input.expectedTurnId);
    } catch (error) {
      if (error instanceof PersistentInspectionError) {
        return { kind: "ambiguous", threadId: input.threadId, source: "persistent", code: error.code };
      }
      throw error;
    }
  }

  private async inspectAcceptedTurn(
    input: InspectGalateaDispatchInput,
    expectedTurnId: string,
  ): Promise<GalateaDispatchInspection> {
    const turns = await this.listTurns(input.threadId, DefaultGalateaDispatchInspectionLimits.maximumTurns);
    const matches = turns.filter((turn) => turn.id === expectedTurnId);
    if (matches.length === 0) {
      return {
        kind: "unavailable",
        threadId: input.threadId,
        turnId: expectedTurnId,
        source: "persistent",
        code: "ACCEPTED_TURN_NOT_VISIBLE",
      };
    }
    if (matches.length !== 1) throw new PersistentInspectionError("TURN_ID_NOT_UNIQUE");
    const items = await this.listItems(
      input.threadId,
      expectedTurnId,
      DefaultGalateaDispatchInspectionLimits.maximumItems,
    );
    return classifyTurnEvidence(
      input.threadId,
      matches[0]!,
      items.map((entry) => entry.item),
      input.dispatchId,
      input.task,
      input.maximumFinalUtf8Bytes,
      "persistent",
    );
  }

  private async inspectUnknownDispatch(
    input: InspectGalateaDispatchInput,
  ): Promise<GalateaDispatchInspection> {
    const entries = await this.listItems(
      input.threadId,
      null,
      DefaultGalateaDispatchInspectionLimits.maximumItems,
    );
    const matches = entries.filter(
      (entry) => entry.item.type === "userMessage" && entry.item.clientId === input.dispatchId,
    );
    if (matches.length === 0) {
      return { kind: "not-found", threadId: input.threadId, source: "persistent" };
    }
    if (matches.length !== 1) throw new PersistentInspectionError("DISPATCH_ID_NOT_UNIQUE");
    const match = matches[0]!;
    if (!hasExactTaskBody(match.item, input.task)) {
      throw new PersistentInspectionError("DISPATCH_BODY_MISMATCH");
    }
    const turns = await this.listTurns(input.threadId, DefaultGalateaDispatchInspectionLimits.maximumTurns);
    const turnMatches = turns.filter((turn) => turn.id === match.turnId);
    if (turnMatches.length !== 1) {
      throw new PersistentInspectionError(
        turnMatches.length === 0 ? "DISPATCH_TURN_MISMATCH" : "TURN_ID_NOT_UNIQUE",
      );
    }
    const targetItems = entries
      .filter((entry) => entry.turnId === match.turnId)
      .map((entry) => entry.item);
    return classifyTurnEvidence(
      input.threadId,
      turnMatches[0]!,
      targetItems,
      input.dispatchId,
      input.task,
      input.maximumFinalUtf8Bytes,
      "persistent",
    );
  }

  private async listTurns(threadId: string, maximumTurns: number): Promise<Turn[]> {
    const generation = this.options.client.generation;
    const turns: Turn[] = [];
    const ids = new Set<string>();
    const cursors = new Set<string>();
    let cursor: string | null = null;
    do {
      const requestedLimit = Math.min(INSPECTION_PAGE_SIZE, maximumTurns - turns.length);
      const response = await this.options.client.request<ThreadTurnsListResponse>("thread/turns/list", {
        threadId,
        cursor,
        limit: requestedLimit,
        sortDirection: "desc",
        itemsView: "notLoaded",
      });
      this.assertSameGeneration(generation);
      const page = validatePage(response);
      if (page.data.length > requestedLimit) {
        throw new PersistentInspectionError("INSPECTION_LIMIT_EXCEEDED");
      }
      for (const turn of page.data) {
        if (!isTurn(turn)) throw new PersistentInspectionError("PAGE_SHAPE_INVALID");
        if (!turn.id) throw new PersistentInspectionError("TURN_ID_INVALID");
        if (turn.itemsView !== "notLoaded" || turn.items.length !== 0) {
          throw new PersistentInspectionError("PAGE_SHAPE_INVALID");
        }
        if (ids.has(turn.id)) throw new PersistentInspectionError("TURN_ID_NOT_UNIQUE");
        ids.add(turn.id);
        turns.push(turn);
        if (turns.length > maximumTurns) throw new PersistentInspectionError("INSPECTION_LIMIT_EXCEEDED");
      }
      if (turns.length === maximumTurns && page.nextCursor !== null) {
        throw new PersistentInspectionError("INSPECTION_LIMIT_EXCEEDED");
      }
      cursor = nextCursor(page.nextCursor, page.data.length, cursors);
    } while (cursor !== null);
    return turns;
  }

  private async listItems(
    threadId: string,
    turnId: string | null,
    maximumItems: number,
  ): Promise<ThreadItemEntry[]> {
    const generation = this.options.client.generation;
    const entries: ThreadItemEntry[] = [];
    const ids = new Set<string>();
    const cursors = new Set<string>();
    let cursor: string | null = null;
    do {
      const requestedLimit = Math.min(INSPECTION_PAGE_SIZE, maximumItems - entries.length);
      const response = await this.options.client.request<ThreadItemsListResponse>("thread/items/list", {
        threadId,
        turnId,
        cursor,
        limit: requestedLimit,
        sortDirection: "asc",
      });
      this.assertSameGeneration(generation);
      const page = validatePage(response);
      if (page.data.length > requestedLimit) {
        throw new PersistentInspectionError("INSPECTION_LIMIT_EXCEEDED");
      }
      for (const entry of page.data) {
        if (!isItemEntry(entry)) throw new PersistentInspectionError("PAGE_SHAPE_INVALID");
        if (turnId !== null && entry.turnId !== turnId) {
          throw new PersistentInspectionError("DISPATCH_TURN_MISMATCH");
        }
        if (!entry.item.id) throw new PersistentInspectionError("ITEM_ID_INVALID");
        if (ids.has(entry.item.id)) throw new PersistentInspectionError("ITEM_ID_NOT_UNIQUE");
        ids.add(entry.item.id);
        entries.push(entry);
        if (entries.length > maximumItems) throw new PersistentInspectionError("INSPECTION_LIMIT_EXCEEDED");
      }
      if (entries.length === maximumItems && page.nextCursor !== null) {
        throw new PersistentInspectionError("INSPECTION_LIMIT_EXCEEDED");
      }
      cursor = nextCursor(page.nextCursor, page.data.length, cursors);
    } while (cursor !== null);
    return entries;
  }

  private assertSameGeneration(expected: number): void {
    if (this.options.client.generation !== expected) {
      throw new PersistentInspectionError("PAGINATION_CURSOR_INVALID");
    }
  }

  private observeLiveTurnNotification(notification: JsonRpcNotification): void {
    const params = notification.params;
    if (typeof params !== "object" || params === null) return;
    if (notification.method === "turn/started" || notification.method === "turn/completed") {
      if ("threadId" in params && typeof params.threadId === "string"
          && "turn" in params && isTurn(params.turn)) {
        this.liveObservations.observeTurn(params.threadId, params.turn);
      }
    } else if (notification.method === "item/completed") {
      if ("threadId" in params && typeof params.threadId === "string"
          && "turnId" in params && typeof params.turnId === "string"
          && "item" in params && isThreadItem(params.item)) {
        this.liveObservations.observeItem(params.threadId, params.turnId, params.item);
      }
    }
  }

  private async readInspectionThread(
    threadId: string,
    expectedCwd: string,
  ): Promise<InspectionThreadRead> {
    let response: ThreadReadResponse;
    try {
      response = await this.options.client.request<ThreadReadResponse>("thread/read", {
        threadId,
        includeTurns: false,
      });
    } catch (error) {
      const bridgeError = asBridgeError(error);
      if (bridgeError.code === "THREAD_NOT_FOUND") {
        return {
          ok: false,
          inspection: {
            kind: "ambiguous",
            threadId,
            source: "persistent",
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
          source: "persistent",
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
          source: "persistent",
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
          source: "persistent",
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
          source: "persistent",
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
    if (!isTurn(response.turn) || response.turn.id.length === 0) {
      throw new BridgeError("CODEX_PROTOCOL_ERROR", "Codex returned an invalid turn identity.");
    }
    const turnId = response.turn.id;
    this.liveObservations.observeTurn(threadId, response.turn);
    this.options.store.beginTurn(threadId, turnId);
    return { threadId, turnId };
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
