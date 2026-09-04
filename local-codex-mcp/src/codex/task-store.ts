import type { Thread } from "../../schemas/v2/Thread.js";
import type { ThreadItem } from "../../schemas/v2/ThreadItem.js";
import type { Turn } from "../../schemas/v2/Turn.js";
import type { TaskSnapshot, TaskStatus } from "../backend/task-backend.js";
import type { JsonRpcNotification } from "./protocol.js";
import { formatAgentReport, parseAgentReport } from "./report.js";

interface RuntimeState extends TaskSnapshot {
  waiters: Map<string, Set<() => void>>;
}

function truncate(value: string, maximum: number): { value: string; truncated: boolean } {
  if (value.length <= maximum) return { value, truncated: false };
  return { value: `${value.slice(0, maximum - 1)}…`, truncated: true };
}

function turnStatus(status: Turn["status"]): TaskStatus {
  return status === "inProgress" ? "running" : status;
}

function latestTurn(thread: Thread): Turn | undefined {
  return thread.turns.at(-1);
}

function completedItems(turn: Turn | undefined): ThreadItem[] {
  return turn?.items ?? [];
}

export class TaskStore {
  private readonly threads = new Map<string, RuntimeState>();

  constructor(
    private readonly maxResultChars: number,
    private readonly maxProgressChars: number,
  ) {}

  get threadCountForTest(): number {
    return this.threads.size;
  }

  beginTurn(threadId: string, turnId: string): void {
    const state = this.getOrCreate(threadId);
    // turn/started or even turn/completed may arrive before the turn/start response.
    // Never erase already-observed state for the same turn when the caller registers it.
    if (state.latestTurnId === turnId) return;
    state.status = "running";
    state.activeTurnId = turnId;
    state.latestTurnId = turnId;
    state.result = undefined;
    state.final = undefined;
    state.finalTruncated = undefined;
    state.progress = undefined;
    state.changedFiles = [];
    state.validation = [];
    state.warnings = [];
    state.errorMessage = undefined;
  }

  hydrate(thread: Thread): TaskSnapshot {
    const state = this.getOrCreate(thread.id);
    const turn = latestTurn(thread);
    if (!turn) {
      state.status = "idle";
      state.activeTurnId = undefined;
      return this.snapshot(thread.id);
    }

    state.status = turnStatus(turn.status);
    state.activeTurnId = turn.status === "inProgress" ? turn.id : undefined;
    state.latestTurnId = turn.id;
    state.changedFiles = [];
    state.validation = [];
    state.warnings = [];
    state.errorMessage = turn.error?.message;
    state.result = undefined;
    state.final = undefined;
    state.finalTruncated = undefined;
    state.progress = undefined;

    for (const item of completedItems(turn)) this.consumeItem(state, item);
    return this.snapshot(thread.id);
  }

  handleNotification(notification: JsonRpcNotification): void {
    const params = notification.params;
    if (notification.method === "bridge/processExited") {
      for (const state of this.threads.values()) {
        if (state.status === "running") {
          state.status = "failed";
          state.errorMessage = "Codex app-server exited while the turn was running.";
          state.warnings.push(state.errorMessage);
          this.resolveWaiters(state, state.activeTurnId);
          state.activeTurnId = undefined;
        }
      }
      return;
    }

    if (typeof params !== "object" || params === null) return;
    const threadId = "threadId" in params && typeof params.threadId === "string" ? params.threadId : undefined;

    switch (notification.method) {
      case "turn/started": {
        if (!threadId || !("turn" in params) || typeof params.turn !== "object" || params.turn === null) return;
        const id = "id" in params.turn && typeof params.turn.id === "string" ? params.turn.id : undefined;
        if (id) this.beginTurn(threadId, id);
        return;
      }
      case "turn/completed": {
        if (!threadId || !("turn" in params) || typeof params.turn !== "object" || params.turn === null) return;
        const turn = params.turn as Turn;
        const state = this.getOrCreate(threadId);
        state.status = turnStatus(turn.status);
        state.latestTurnId = turn.id;
        state.activeTurnId = undefined;
        state.errorMessage = turn.error?.message;
        for (const item of turn.items) this.consumeItem(state, item);
        this.resolveWaiters(state, turn.id);
        return;
      }
      case "item/completed": {
        if (!threadId || !("item" in params)) return;
        const turnId = "turnId" in params && typeof params.turnId === "string" ? params.turnId : undefined;
        const state = this.getOrCreate(threadId);
        if (!turnId || (state.latestTurnId !== undefined && state.latestTurnId !== turnId)) return;
        this.consumeItem(state, params.item as ThreadItem);
        return;
      }
      case "item/agentMessage/delta": {
        if (!threadId || !("delta" in params) || typeof params.delta !== "string") return;
        const state = this.getOrCreate(threadId);
        state.progress = truncate(`${state.progress ?? ""}${params.delta}`, this.maxProgressChars).value;
        return;
      }
      case "warning": {
        if (!threadId || !("message" in params) || typeof params.message !== "string") return;
        this.addWarning(this.getOrCreate(threadId), params.message);
        return;
      }
      case "configWarning": {
        if (!("summary" in params) || typeof params.summary !== "string") return;
        for (const state of this.threads.values()) this.addWarning(state, params.summary);
        return;
      }
      case "bridge/serverRequestDeclined": {
        if (!threadId || !("method" in params) || typeof params.method !== "string") return;
        this.addWarning(this.getOrCreate(threadId), `Denied unsupported privilege request: ${params.method}`);
        return;
      }
    }
  }

  async waitForTurn(threadId: string, turnId: string, waitMs: number): Promise<TaskSnapshot> {
    const state = this.getOrCreate(threadId);
    if (state.latestTurnId === turnId && state.status !== "running") return this.snapshot(threadId);
    if (waitMs === 0) return this.snapshot(threadId);

    await new Promise<void>((resolve) => {
      const waiters = state.waiters.get(turnId) ?? new Set<() => void>();
      let timer: NodeJS.Timeout;
      const finish = () => {
        clearTimeout(timer);
        waiters.delete(finish);
        if (waiters.size === 0) state.waiters.delete(turnId);
        resolve();
      };
      waiters.add(finish);
      state.waiters.set(turnId, waiters);
      timer = setTimeout(finish, waitMs);
    });
    return this.snapshot(threadId);
  }

  hasRunning(threadId: string): boolean {
    return this.threads.get(threadId)?.status === "running";
  }

  snapshot(threadId: string): TaskSnapshot {
    const state = this.getOrCreate(threadId);
    return {
      threadId: state.threadId,
      status: state.status,
      ...(state.activeTurnId ? { activeTurnId: state.activeTurnId } : {}),
      ...(state.latestTurnId ? { latestTurnId: state.latestTurnId } : {}),
      ...(state.result ? { result: state.result } : {}),
      ...(state.final !== undefined
        ? { final: state.final, finalTruncated: state.finalTruncated ?? false }
        : {}),
      ...(state.progress ? { progress: state.progress } : {}),
      changedFiles: [...state.changedFiles],
      validation: [...state.validation],
      warnings: [...state.warnings],
      ...(state.errorMessage ? { errorMessage: state.errorMessage } : {}),
    };
  }

  private getOrCreate(threadId: string): RuntimeState {
    let state = this.threads.get(threadId);
    if (!state) {
      state = {
        threadId,
        status: "idle",
        changedFiles: [],
        validation: [],
        warnings: [],
        waiters: new Map(),
      };
      this.threads.set(threadId, state);
    }
    return state;
  }

  private consumeItem(state: RuntimeState, item: ThreadItem): void {
    if (item.type === "agentMessage") {
      if (item.phase === "commentary") {
        state.progress = truncate(item.text, this.maxProgressChars).value;
        return;
      }
      const final = truncate(item.text, this.maxResultChars);
      state.final = final.value;
      state.finalTruncated = final.truncated;
      const report = parseAgentReport(item.text);
      if (report) {
        state.result = truncate(formatAgentReport(report), this.maxResultChars).value;
        state.validation = report.validation;
        state.changedFiles = [...new Set([...state.changedFiles, ...report.changed_files])];
        for (const warning of report.warnings) this.addWarning(state, warning);
      } else {
        state.result = final.value;
      }
    } else if (item.type === "fileChange") {
      state.changedFiles = [
        ...new Set([...state.changedFiles, ...item.changes.map((change) => change.path)]),
      ].slice(0, 100);
    } else if (item.type === "imageGeneration" && item.savedPath) {
      state.changedFiles = [
        ...new Set([...state.changedFiles, item.savedPath]),
      ].slice(0, 100);
    } else if (item.type === "commandExecution" && item.status === "failed") {
      this.addWarning(
        state,
        `A command failed${item.exitCode === null ? "" : ` with exit code ${item.exitCode}`}.`,
      );
    }
  }

  private addWarning(state: RuntimeState, warning: string): void {
    const bounded = truncate(warning, 1000).value;
    if (!state.warnings.includes(bounded)) state.warnings.push(bounded);
    state.warnings = state.warnings.slice(-20);
  }

  private resolveWaiters(state: RuntimeState, turnId: string | undefined): void {
    if (!turnId) return;
    for (const resolve of [...(state.waiters.get(turnId) ?? [])]) resolve();
  }
}
