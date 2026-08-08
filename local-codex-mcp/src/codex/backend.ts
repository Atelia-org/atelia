import path from "node:path";
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
  TaskBackend,
  TaskMode,
  TaskSnapshot,
} from "../backend/task-backend.js";
import { BridgeError, asBridgeError } from "../errors.js";
import type { BridgeLogger } from "../logger.js";
import { PathPolicy } from "../security/paths.js";
import { CodexAppServerClient } from "./client.js";
import { agentReportJsonSchema } from "./report.js";
import { TaskStore } from "./task-store.js";

const BRIDGE_THREAD_SOURCE = "atelia-local-codex-mcp";
const BRIDGE_THREAD_NAME_PREFIX = "[local-codex-mcp] ";
const SERVICE_NAME = "atelia_local_codex_mcp";

const DEVELOPER_INSTRUCTIONS = `You are the local execution subagent behind an MCP bridge.
Complete the requested task inside the supplied cwd and sandbox. Do not request privilege escalation.
Keep the final report concise: outcome, important findings, changed file paths, validation results, and warnings.
Never include chain-of-thought, full command logs, large diffs, or full file contents in the final report.`;

function coarseSandbox(mode: TaskMode): SandboxMode {
  return mode === "research" ? "read-only" : "workspace-write";
}

function preciseSandbox(mode: TaskMode, cwd: string, network: boolean): SandboxPolicy {
  if (mode === "research") {
    return { type: "readOnly", networkAccess: network };
  }
  return {
    type: "workspaceWrite",
    writableRoots: [cwd],
    networkAccess: network,
    excludeTmpdirEnvVar: true,
    excludeSlashTmp: true,
  };
}

function threadConfig(network: boolean): Record<string, unknown> {
  return { web_search: network ? "live" : "disabled" };
}

function ownershipName(threadId: string): string {
  return `${BRIDGE_THREAD_NAME_PREFIX}${threadId}`;
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
}

export class CodexBackend implements TaskBackend {
  private authenticated = false;
  private readonly continueReservations = new Set<string>();

  constructor(private readonly options: CodexBackendOptions) {
    this.options.client.subscribe((notification) => {
      if (notification.method === "bridge/processExited") this.authenticated = false;
      this.options.store.handleNotification(notification);
    });
  }

  async start(): Promise<void> {
    await this.ensureReady();
  }

  async stop(): Promise<void> {
    this.authenticated = false;
    await this.options.client.stop();
  }

  async delegate(input: DelegateTaskInput): Promise<TaskSnapshot> {
    const cwd = await this.options.pathPolicy.resolveCwd(input.cwd);
    await this.ensureReady();
    const startedAt = Date.now();
    try {
      const response = await this.options.client.request<ThreadStartResponse>("thread/start", {
        cwd,
        approvalPolicy: "never",
        approvalsReviewer: "user",
        sandbox: coarseSandbox(input.mode),
        config: threadConfig(input.network),
        serviceName: SERVICE_NAME,
        developerInstructions: DEVELOPER_INSTRUCTIONS,
        ephemeral: false,
        threadSource: BRIDGE_THREAD_SOURCE,
      });
      await this.validateStartedThread(response.thread, cwd);
      await this.options.client.request("thread/name/set", {
        threadId: response.thread.id,
        name: ownershipName(response.thread.id),
      });
      const snapshot = await this.startTurn(
        response.thread.id,
        input.task,
        cwd,
        input.mode,
        input.network,
        input.waitMs,
      );
      this.logTask("codex_delegate", cwd, snapshot, Date.now() - startedAt);
      return this.sanitizeSnapshot(snapshot, cwd);
    } catch (error) {
      const bridgeError = asBridgeError(error);
      this.logFailure("codex_delegate", cwd, bridgeError, Date.now() - startedAt);
      throw bridgeError;
    }
  }

  async continue(input: ContinueTaskInput): Promise<TaskSnapshot> {
    await this.ensureReady();
    if (this.continueReservations.has(input.threadId)) {
      throw new BridgeError("BRIDGE_BUSY", "This Codex thread already has an active turn.");
    }
    this.continueReservations.add(input.threadId);
    const startedAt = Date.now();
    let cwd: string | undefined;
    try {
      const preflight = await this.readOwnedThread(input.threadId, false);
      cwd = await this.options.pathPolicy.resolveCwd(preflight.cwd);
      if (this.options.store.hasRunning(input.threadId) || preflight.status.type === "active") {
        throw new BridgeError("BRIDGE_BUSY", "This Codex thread already has an active turn.");
      }

      const resumed = await this.options.client.request<ThreadResumeResponse>("thread/resume", {
        threadId: input.threadId,
        cwd,
        approvalPolicy: "never",
        approvalsReviewer: "user",
        sandbox: coarseSandbox(input.mode),
        config: threadConfig(input.network),
        developerInstructions: DEVELOPER_INSTRUCTIONS,
      });
      await this.validatePersistedThread(resumed.thread, cwd);
      const snapshot = await this.startTurn(
        input.threadId,
        input.task,
        cwd,
        input.mode,
        input.network,
        input.waitMs,
      );
      this.logTask("codex_continue", cwd, snapshot, Date.now() - startedAt);
      return this.sanitizeSnapshot(snapshot, cwd);
    } catch (error) {
      const bridgeError = asBridgeError(error);
      this.logFailure("codex_continue", cwd, bridgeError, Date.now() - startedAt, input.threadId);
      throw bridgeError;
    } finally {
      this.continueReservations.delete(input.threadId);
    }
  }

  async status(threadId: string): Promise<TaskSnapshot> {
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
    const startedAt = Date.now();
    let cwd: string | undefined;
    try {
      const loaded = await this.loadSnapshot(threadId);
      cwd = loaded.cwd;
      let snapshot = loaded.snapshot;
      if (snapshot.activeTurnId) {
        const turnId = snapshot.activeTurnId;
        await this.options.client.request<TurnInterruptResponse>("turn/interrupt", { threadId, turnId });
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
    await this.options.client.start();
    if (this.authenticated) return;
    const response = await this.options.client.request<GetAccountResponse>("account/read", {
      refreshToken: false,
    });
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
    network: boolean,
    waitMs: number,
  ): Promise<TaskSnapshot> {
    const response = await this.options.client.request<TurnStartResponse>("turn/start", {
      threadId,
      input: [{ type: "text", text: task, text_elements: [] }],
      cwd,
      approvalPolicy: "never",
      approvalsReviewer: "user",
      sandboxPolicy: preciseSandbox(mode, cwd, network),
      summary: "concise",
      outputSchema: agentReportJsonSchema,
    });
    this.options.store.beginTurn(threadId, response.turn.id);
    return this.options.store.waitForTurn(threadId, response.turn.id, waitMs);
  }

  private async readOwnedThread(threadId: string, includeTurns: boolean): Promise<Thread> {
    const response = await this.options.client.request<ThreadReadResponse>("thread/read", {
      threadId,
      includeTurns,
    });
    if (response.thread.id !== threadId || response.thread.name !== ownershipName(threadId)) {
      throw new BridgeError("THREAD_NOT_FOUND", "The requested thread is not owned by this bridge.");
    }
    await this.options.pathPolicy.resolveCwd(response.thread.cwd);
    return response.thread;
  }

  private async loadSnapshot(threadId: string): Promise<{ snapshot: TaskSnapshot; cwd: string }> {
    await this.ensureReady();
    const preflight = await this.readOwnedThread(threadId, false);
    const cwd = await this.options.pathPolicy.resolveCwd(preflight.cwd);
    const thread = await this.readOwnedThread(threadId, true);
    return { snapshot: this.options.store.hydrate(thread), cwd };
  }

  private async validateStartedThread(thread: Thread, expectedCwd: string): Promise<void> {
    if (thread.threadSource !== BRIDGE_THREAD_SOURCE) {
      throw new BridgeError("CODEX_PROTOCOL_ERROR", "Codex did not preserve the bridge thread ownership tag.");
    }
    await this.validateThreadCwd(thread, expectedCwd);
  }

  private async validatePersistedThread(thread: Thread, expectedCwd: string): Promise<void> {
    if (thread.name !== ownershipName(thread.id)) {
      throw new BridgeError("THREAD_NOT_FOUND", "The requested thread is not owned by this bridge.");
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

  private logTask(tool: string, cwd: string, snapshot: TaskSnapshot, durationMs: number): void {
    this.options.logger.log("info", "mcp_tool_complete", {
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
    this.options.logger.log("error", "mcp_tool_failed", {
      tool,
      thread_id: threadId,
      cwd,
      duration_ms: durationMs,
      status: "failed",
      error_code: error.code,
    });
  }
}
