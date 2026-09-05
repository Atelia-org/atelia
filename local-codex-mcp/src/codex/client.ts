import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import readline from "node:readline";
import type { InitializeResponse } from "../../schemas/InitializeResponse.js";
import { BridgeError } from "../errors.js";
import type { BridgeLogger } from "../logger.js";
import { hasId, hasMethod, type JsonRpcId, type JsonRpcNotification } from "./protocol.js";
import { codexVersionFromUserAgent, PINNED_CODEX_VERSION } from "./pinned-version.js";

interface Connection {
  child: ChildProcessWithoutNullStreams;
  lines: readline.Interface;
  failed: boolean;
  expectedStop: boolean;
  stderrTail: string;
  closed: Promise<void>;
  resolveClosed(): void;
  writeRejectors: Set<(error: Error) => void>;
  termination?: Promise<void>;
}

interface PendingRequest {
  method: string;
  connection: Connection;
  resolve(value: unknown): void;
  reject(error: Error): void;
  timer: NodeJS.Timeout;
}

const maxStderrCaptureBytes = 8 * 1024;

function appendStderrTail(current: string, chunk: string): string {
  const combined = Buffer.concat([Buffer.from(current), Buffer.from(chunk)]);
  return combined.length <= maxStderrCaptureBytes
    ? combined.toString("utf8")
    : combined.subarray(-maxStderrCaptureBytes).toString("utf8");
}

export interface CodexClientOptions {
  command: string;
  args: string[];
  env?: NodeJS.ProcessEnv;
  requestTimeoutMs: number;
  logger: BridgeLogger;
  stopTimeoutMs?: number;
}

export type NotificationSubscriber = (notification: JsonRpcNotification) => void;

export class CodexAppServerClient {
  private connection?: Connection;
  private nextRequestId = 1;
  private readonly pending = new Map<JsonRpcId, PendingRequest>();
  private readonly subscribers = new Set<NotificationSubscriber>();
  private readonly terminations = new Set<Promise<void>>();
  private startPromise?: Promise<void>;
  private stopPromise?: Promise<void>;
  private initialized = false;
  private stopEpoch = 0;
  private appServerGeneration = 0;

  constructor(private readonly options: CodexClientOptions) {}

  get isRunning(): boolean {
    const child = this.connection?.child;
    return child !== undefined && child.exitCode === null && !child.killed && !this.connection?.failed;
  }

  /** Monotonic identity of the currently initialized app-server process. */
  get generation(): number {
    return this.appServerGeneration;
  }

  subscribe(subscriber: NotificationSubscriber): () => void {
    this.subscribers.add(subscriber);
    return () => this.subscribers.delete(subscriber);
  }

  async start(): Promise<void> {
    const startEpoch = this.stopEpoch;
    if (this.stopPromise) await this.stopPromise;
    if (this.terminations.size > 0) await Promise.all([...this.terminations]);
    if (this.stopEpoch !== startEpoch) {
      throw new BridgeError(
        "CODEX_PROTOCOL_ERROR",
        "Codex app-server startup was superseded by stop.",
      );
    }
    if (this.initialized && this.isRunning) return;
    if (this.startPromise) return this.startPromise;

    this.startPromise = this.spawnAndInitialize().finally(() => {
      this.startPromise = undefined;
    });
    return this.startPromise;
  }

  private async spawnAndInitialize(): Promise<void> {
    this.initialized = false;
    const child = spawn(this.options.command, this.options.args, {
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true,
      ...(this.options.env === undefined ? {} : { env: this.options.env }),
    });
    const connection = this.createConnection(child);
    this.connection = connection;

    try {
      const initializeResponse = await this.rawRequest<InitializeResponse>(connection, "initialize", {
        clientInfo: {
          name: "atelia_local_codex_mcp",
          title: "Atelia Local Codex MCP Bridge",
          version: "0.1.0",
        },
        capabilities: {
          experimentalApi: false,
          requestAttestation: false,
          mcpServerOpenaiFormElicitation: false,
          optOutNotificationMethods: [
            "item/reasoning/summaryTextDelta",
            "item/reasoning/summaryPartAdded",
            "item/reasoning/textDelta",
            "item/commandExecution/outputDelta",
            "item/fileChange/outputDelta",
            "item/plan/delta",
            "turn/diff/updated",
            "turn/plan/updated",
            "rawResponseItem/completed",
            "rawResponse/completed",
          ],
        },
      });
      const actualVersion = codexVersionFromUserAgent(initializeResponse.userAgent);
      if (actualVersion !== PINNED_CODEX_VERSION) {
        this.options.logger.log("error", "codex_version_mismatch", {
          expected_version: PINNED_CODEX_VERSION,
          actual_version: actualVersion ?? "unrecognized",
        });
        connection.expectedStop = true;
        throw new BridgeError(
          "CODEX_VERSION_MISMATCH",
          `Configured Codex version mismatch: expected ${PINNED_CODEX_VERSION}, found ${actualVersion ?? "an unrecognized user agent"}.`,
          {
            details: {
              expected_version: PINNED_CODEX_VERSION,
              actual_version: actualVersion ?? "unrecognized",
            },
          },
        );
      }
      await this.rawNotify(connection, "initialized");
      if (this.connection !== connection || connection.failed) {
        throw new BridgeError("CODEX_START_FAILED", "Codex app-server stopped during initialization.");
      }
      this.initialized = true;
      this.appServerGeneration += 1;
      this.options.logger.log("info", "codex_started", { pid: child.pid });
    } catch (error) {
      await this.beginTermination(connection, false);
      if (error instanceof BridgeError) throw error;
      throw new BridgeError("CODEX_START_FAILED", "Failed to start or initialize Codex app-server.", {
        cause: error,
      });
    }
  }

  private createConnection(child: ChildProcessWithoutNullStreams): Connection {
    let resolveClosed!: () => void;
    const closed = new Promise<void>((resolve) => {
      resolveClosed = resolve;
    });
    const connection: Connection = {
      child,
      lines: readline.createInterface({ input: child.stdout }),
      failed: false,
      expectedStop: false,
      stderrTail: "",
      closed,
      resolveClosed,
      writeRejectors: new Set(),
    };

    connection.lines.on("line", (line) => this.handleLine(connection, line));
    child.stderr.setEncoding("utf8");
    child.stderr.on("data", (chunk: string) => {
      connection.stderrTail = appendStderrTail(connection.stderrTail, chunk);
      this.options.logger.log("debug", "codex_stderr", { bytes: Buffer.byteLength(chunk) });
    });
    child.stdin.on("error", (error) => this.handleProcessFailure(connection, error, true));
    child.on("error", (error) => this.handleProcessFailure(connection, error, true));
    child.on("exit", (code, signal) => {
      this.handleProcessFailure(
        connection,
        new Error(`codex app-server exited (code=${String(code)}, signal=${String(signal)})`),
        false,
      );
    });
    child.on("close", () => {
      connection.resolveClosed();
      this.handleProcessFailure(
        connection,
        new Error("codex app-server closed its stdio streams unexpectedly."),
        false,
      );
    });
    return connection;
  }

  async stop(): Promise<void> {
    if (this.stopPromise) return this.stopPromise;
    this.stopEpoch += 1;
    const connection = this.connection;
    this.initialized = false;
    if (!connection && this.terminations.size === 0) return;

    const termination = connection ? this.beginTermination(connection, true) : Promise.resolve();
    this.stopPromise = Promise.all([termination, ...this.terminations])
      .then(() => undefined)
      .finally(() => {
        this.stopPromise = undefined;
      });
    return this.stopPromise;
  }

  async request<T>(method: string, params?: unknown): Promise<T> {
    await this.start();
    const connection = this.connection;
    if (!connection) {
      throw new BridgeError("CODEX_START_FAILED", "Codex app-server is not running.");
    }
    return this.rawRequest<T>(connection, method, params);
  }

  async notify(method: string, params?: unknown): Promise<void> {
    await this.start();
    const connection = this.connection;
    if (!connection) {
      throw new BridgeError("CODEX_START_FAILED", "Codex app-server is not running.");
    }
    await this.rawNotify(connection, method, params);
  }

  private rawRequest<T>(connection: Connection, method: string, params?: unknown): Promise<T> {
    const id = this.nextRequestId++;
    return new Promise<T>((resolve, reject) => {
      const timer = setTimeout(() => {
        const request = this.pending.get(id);
        if (request?.connection !== connection) return;
        this.pending.delete(id);
        reject(
          new BridgeError("CODEX_PROTOCOL_ERROR", `Codex RPC ${method} timed out.`, {
            details: { method, timeout: true },
          }),
        );
      }, this.options.requestTimeoutMs);
      this.pending.set(id, {
        method,
        connection,
        resolve: (value) => resolve(value as T),
        reject,
        timer,
      });

      void this.write(connection, { id, method, ...(params === undefined ? {} : { params }) }).catch(
        (error: unknown) => {
          const request = this.pending.get(id);
          if (request?.connection !== connection) return;
          clearTimeout(timer);
          this.pending.delete(id);
          reject(error instanceof Error ? error : new Error(String(error)));
        },
      );
    });
  }

  private rawNotify(connection: Connection, method: string, params?: unknown): Promise<void> {
    return this.write(connection, { method, ...(params === undefined ? {} : { params }) });
  }

  private write(connection: Connection, message: unknown): Promise<void> {
    const child = connection.child;
    if (connection.failed || child.exitCode !== null || child.killed) {
      throw new BridgeError("CODEX_START_FAILED", "Codex app-server is not running.");
    }

    const data = `${JSON.stringify(message)}\n`;
    return new Promise<void>((resolve, reject) => {
      let callbackComplete = false;
      let drainComplete = true;
      let settled = false;
      const rejectOnConnectionFailure = (error: Error) => finish(error);
      const finish = (error?: Error | null) => {
        if (settled) return;
        if (error) {
          settled = true;
          connection.writeRejectors.delete(rejectOnConnectionFailure);
          reject(error);
          this.handleProcessFailure(connection, error, true);
          return;
        }
        if (callbackComplete && drainComplete) {
          settled = true;
          connection.writeRejectors.delete(rejectOnConnectionFailure);
          resolve();
        }
      };
      connection.writeRejectors.add(rejectOnConnectionFailure);

      try {
        const accepted = child.stdin.write(data, (error?: Error | null) => {
          callbackComplete = true;
          finish(error);
        });
        if (!accepted) {
          drainComplete = false;
          child.stdin.once("drain", () => {
            drainComplete = true;
            finish();
          });
        }
      } catch (error) {
        finish(error instanceof Error ? error : new Error(String(error)));
      }
    });
  }

  private handleLine(connection: Connection, line: string): void {
    if (connection.failed || this.connection !== connection) return;
    let message: unknown;
    try {
      message = JSON.parse(line);
    } catch (error) {
      this.handleProcessFailure(
        connection,
        new BridgeError("CODEX_PROTOCOL_ERROR", "Codex app-server emitted invalid JSON.", {
          cause: error,
        }),
        true,
      );
      return;
    }

    if (hasId(message) && !hasMethod(message)) {
      const request = this.pending.get(message.id);
      if (!request || request.connection !== connection) return;
      clearTimeout(request.timer);
      this.pending.delete(message.id);
      const response = message as { result?: unknown; error?: { code?: number; message?: string; data?: unknown } };
      if (response.error) {
        const message = response.error.message ?? `Codex RPC ${request.method} failed.`;
        const normalized = message.toLowerCase();
        const code =
          request.method.startsWith("thread/") && normalized.includes("not found")
            ? "THREAD_NOT_FOUND"
            : normalized.includes("sandbox")
              ? "SANDBOX_DENIED"
              : normalized.includes("network") &&
                  (normalized.includes("denied") || normalized.includes("disabled"))
                ? "NETWORK_DENIED"
                : "CODEX_PROTOCOL_ERROR";
        request.reject(
          new BridgeError(code, message, {
            details: { method: request.method, rpc_code: response.error.code },
          }),
        );
      } else {
        request.resolve(response.result);
      }
      return;
    }

    if (hasMethod(message) && hasId(message)) {
      void this.handleServerRequest(connection, message.id, message.method, message.params);
      return;
    }

    if (hasMethod(message)) {
      for (const subscriber of this.subscribers) subscriber(message);
    }
  }

  private async handleServerRequest(
    connection: Connection,
    id: JsonRpcId,
    method: string,
    params: unknown,
  ): Promise<void> {
    let result: unknown;
    switch (method) {
      case "item/commandExecution/requestApproval":
      case "item/fileChange/requestApproval":
        result = { decision: "decline" };
        break;
      case "execCommandApproval":
      case "applyPatchApproval":
        result = { decision: { denied: { rejection: "The bridge never grants privilege escalation." } } };
        break;
      case "item/permissions/requestApproval":
        result = { permissions: {}, scope: "turn" };
        break;
      case "item/tool/requestUserInput":
        result = { answers: {} };
        break;
      case "mcpServer/elicitation/request":
        result = { action: "decline", content: null, _meta: null };
        break;
      default:
        await this.write(connection, { id, error: { code: -32601, message: "Unsupported server request" } });
        this.emitDeclinedServerRequest(method, params);
        return;
    }

    await this.write(connection, { id, result });
    this.emitDeclinedServerRequest(method, params);
  }

  private emitDeclinedServerRequest(method: string, params: unknown): void {
    const threadId =
      typeof params === "object" && params !== null && "threadId" in params
        ? (params as { threadId?: unknown }).threadId
        : undefined;
    const notification: JsonRpcNotification = {
      method: "bridge/serverRequestDeclined",
      params: { method, ...(typeof threadId === "string" ? { threadId } : {}) },
    };
    for (const subscriber of this.subscribers) subscriber(notification);
    this.options.logger.log("warning", "codex_server_request_declined", {
      method,
      thread_id: typeof threadId === "string" ? threadId : undefined,
    });
  }

  private handleProcessFailure(connection: Connection, error: unknown, terminate: boolean): void {
    if (connection.failed) return;
    connection.failed = true;
    connection.lines.close();
    const wasActive = this.connection === connection;
    if (wasActive) {
      this.connection = undefined;
      this.initialized = false;
    }

    const isNotFound =
      typeof error === "object" && error !== null && "code" in error && (error as { code?: unknown }).code === "ENOENT";
    const bridgeError =
      error instanceof BridgeError
        ? error
        : isNotFound
          ? new BridgeError("CODEX_NOT_FOUND", "The configured Codex executable was not found.", {
              cause: error,
            })
          : new BridgeError(
              connection.expectedStop ? "CODEX_PROTOCOL_ERROR" : "CODEX_START_FAILED",
              connection.expectedStop ? "Codex app-server stopped." : "Codex app-server exited unexpectedly.",
              { cause: error },
            );
    this.rejectForConnection(connection, bridgeError);
    for (const reject of [...connection.writeRejectors]) reject(bridgeError);
    connection.writeRejectors.clear();

    if (terminate) void this.beginTermination(connection, false);
    if (!connection.expectedStop) {
      this.options.logger.log("error", "codex_process_exit", { code: bridgeError.code });
      this.options.logger.log("debug", "codex_start_failed", {
        error_code: bridgeError.code,
        stderr_tail: connection.stderrTail || undefined,
      });
    }
    if (wasActive) {
      for (const subscriber of this.subscribers) {
        subscriber({ method: "bridge/processExited", params: { expected: connection.expectedStop } });
      }
    }
  }

  private beginTermination(connection: Connection, expected: boolean): Promise<void> {
    connection.expectedStop ||= expected;
    if (connection.termination) return connection.termination;
    let termination!: Promise<void>;
    termination = this.terminateConnection(connection).finally(() => {
      this.terminations.delete(termination);
    });
    connection.termination = termination;
    this.terminations.add(termination);
    return termination;
  }

  private async terminateConnection(connection: Connection): Promise<void> {
    this.handleProcessFailure(
      connection,
      new BridgeError("CODEX_PROTOCOL_ERROR", "Codex app-server stopped."),
      false,
    );
    if (connection.child.exitCode !== null) return;
    if (!connection.child.killed) connection.child.kill("SIGTERM");

    const timeoutMs = this.options.stopTimeoutMs ?? 2_000;
    if (await this.waitForClose(connection, timeoutMs)) return;
    if (connection.child.exitCode === null) connection.child.kill("SIGKILL");
    await this.waitForClose(connection, timeoutMs);
  }

  private async waitForClose(connection: Connection, timeoutMs: number): Promise<boolean> {
    let timer: NodeJS.Timeout | undefined;
    const timedOut = new Promise<false>((resolve) => {
      timer = setTimeout(() => resolve(false), timeoutMs);
    });
    const closed = connection.closed.then(() => true as const);
    const result = await Promise.race([closed, timedOut]);
    if (timer) clearTimeout(timer);
    return result;
  }

  private rejectForConnection(connection: Connection, error: Error): void {
    for (const [id, request] of this.pending) {
      if (request.connection !== connection) continue;
      clearTimeout(request.timer);
      request.reject(error);
      this.pending.delete(id);
    }
  }
}
