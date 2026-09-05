import readline from "node:readline";
import {
  appendFileSync,
  closeSync,
  existsSync,
  readFileSync,
  writeFileSync,
} from "node:fs";

type Message = { id?: string | number; method?: string; params?: Record<string, unknown>; result?: unknown; error?: unknown };

interface PersistedFixtureState {
  nextThread: number;
  nextTurn: number;
  threads: Array<[string, Record<string, unknown>]>;
}

const stateFileArgument = process.argv.find((argument) => argument.startsWith("--state-file="));
const stateFile = stateFileArgument?.slice("--state-file=".length);
const restored: PersistedFixtureState | undefined = stateFile && existsSync(stateFile)
  ? JSON.parse(readFileSync(stateFile, "utf8")) as PersistedFixtureState
  : undefined;
const pageSizeArgument = process.argv.find((argument) => argument.startsWith("--inspection-page-size="));
const forcedInspectionPageSize = pageSizeArgument
  ? Number(pageSizeArgument.slice("--inspection-page-size=".length))
  : undefined;
const inspectionFixtureArgument = process.argv.find((argument) => argument.startsWith("--inspection-fixture="));
const inspectionFixture = inspectionFixtureArgument
  ? JSON.parse(readFileSync(inspectionFixtureArgument.slice("--inspection-fixture=".length), "utf8")) as {
      officialTurnsPages: Array<Record<string, unknown>>;
    }
  : undefined;
const userAgentArgument = process.argv.find((argument) => argument.startsWith("--user-agent="));
const userAgent = userAgentArgument?.slice("--user-agent=".length)
  ?? "codex_vscode/0.154.0-alpha.3 (fixture)";

let initialized = false;
let initializeCount = 0;
let nextThread = restored?.nextThread ?? 1;
let nextTurn = restored?.nextTurn ?? 1;
let nextServerRequest = 10_000;
const pendingServerRequests = new Map<string | number, string | number>();
const threads = new Map<string, Record<string, unknown>>(restored?.threads ?? []);
const itemProjectionVisibleAt = new Map<string, number>();
let lastTurnParams: Record<string, unknown> | undefined;
let lastResumeParams: Record<string, unknown> | undefined;
let lastThreadStartParams: Record<string, unknown> | undefined;
const allTurnParams: Record<string, unknown>[] = [];
let threadStartCount = 0;
let threadReadCount = 0;
const threadReadIncludeTurns: boolean[] = [];
let threadTurnsListCount = 0;
let threadItemsListCount = 0;
let threadNameSetCount = 0;
let threadResumeCount = 0;
let turnStartCount = 0;
let keepAliveAfterStdinClose = false;
let resumeResponseThreadIdOverride: string | undefined;
let blockNextMetadataRead = false;
let blockedMetadataRead: { id: string | number; result: unknown } | undefined;
const metadataReadBarrierWaiters: Array<string | number> = [];

const lifecycleFileArgument = process.argv.find((argument) => argument.startsWith("--lifecycle-file="));
const lifecycleFile = lifecycleFileArgument?.slice("--lifecycle-file=".length);
if (lifecycleFile) {
  appendFileSync(lifecycleFile, `start:${process.pid}\n`);
  process.on("exit", () => appendFileSync(lifecycleFile, `exit:${process.pid}\n`));
}

if (process.argv.includes("--ignore-sigterm")) {
  process.on("SIGTERM", () => undefined);
}

function persistState(): void {
  if (!stateFile) return;
  writeFileSync(stateFile, JSON.stringify({
    nextThread,
    nextTurn,
    threads: [...threads.entries()],
  } satisfies PersistedFixtureState));
}

function send(message: unknown): void {
  process.stdout.write(`${JSON.stringify(message)}\n`);
}

function makeThread(id: string, cwd: string, threadSource: string | null): Record<string, unknown> {
  const now = Math.floor(Date.now() / 1000);
  return {
    id,
    sessionId: id,
    forkedFromId: null,
    parentThreadId: null,
    preview: "",
    ephemeral: false,
    section: null,
    sectionEnteredAt: null,
    modelProvider: "openai",
    createdAt: now,
    updatedAt: now,
    recencyAt: now,
    status: { type: "idle" },
    path: null,
    cwd,
    cliVersion: "fake",
    source: "appServer",
    threadSource,
    agentNickname: null,
    agentRole: null,
    gitInfo: null,
    name: null,
    turns: [],
  };
}

function completeTurn(
  threadId: string,
  turnId: string,
  status: "completed" | "failed" | "interrupted" = "completed",
  behavior = "",
): void {
  const thread = threads.get(threadId);
  if (!thread) return;
  const turns = thread.turns as Array<Record<string, unknown>>;
  const turn = turns.find((item) => item.id === turnId);
  if (!turn) return;
  const report = JSON.stringify({
    summary: status === "interrupted" ? "Interrupted." : "Fake task completed.",
    findings: status === "completed" ? ["Fake evidence"] : [],
    changed_files: status === "completed" ? ["hello.txt", "../outside.txt"] : [],
    validation: status === "completed" ? ["fake check passed"] : [],
    warnings: [],
  });
  const natural = "Galatea，事情已经办妥。\n\n```ts\nconst answer = `含有 ~~~ fence`;\n```";
  const agentItem = {
    type: "agentMessage",
    id: `item-${turnId}`,
    text: behavior.includes("[NATURAL]") ? natural : behavior.includes("[OVERSIZE]") ? "x".repeat(10_000) : report,
    phase: behavior.includes("[LEGACY]") ? null : "final_answer",
    memoryCitation: null,
    delivery: null,
  };
  const fileItem = {
    type: "fileChange",
    id: `file-${turnId}`,
    changes: [{ path: "hello.txt", kind: "add", diff: "secret diff must not escape" }],
    status: "completed",
  };
  turn.status = status;
  turn.error = status === "failed"
    ? { message: "fake failure", codexErrorInfo: null, additionalDetails: null, misalignment: null }
    : null;
  turn.completedAt = Math.floor(Date.now() / 1000);
  turn.durationMs = 10;
  const userItems = (turn.items as Array<Record<string, unknown>>).filter(
    (item) => item.type === "userMessage",
  );
  turn.items = status === "completed" && !behavior.includes("[MISSING]")
    ? [...userItems, agentItem, fileItem]
    : userItems;
  thread.status = { type: "idle" };
  persistState();
  if (behavior.includes("[DROP_TERMINAL_SIGNALS]")) return;
  if (status === "completed" && (behavior.includes("[SUMMARY_BEFORE_FINAL]")
      || behavior.includes("[SUMMARY_DROP_SIGNAL]"))) {
    itemProjectionVisibleAt.set(
      turnId,
      behavior.includes("[SUMMARY_DROP_SIGNAL]") ? Date.now() + 70 : Number.POSITIVE_INFINITY,
    );
    send({ method: "turn/completed", params: {
      threadId,
      turn: { ...turn, items: [], itemsView: "summary" },
    } });
    if (behavior.includes("[SUMMARY_BEFORE_FINAL]")) {
      setTimeout(() => {
        itemProjectionVisibleAt.delete(turnId);
        send({ method: "item/completed", params: { threadId, turnId, item: agentItem, completedAtMs: Date.now() } });
      }, 50);
    }
    return;
  }
  if (status === "completed" && !behavior.includes("[MISSING]")) {
    send({ method: "item/completed", params: { threadId, turnId, item: agentItem, completedAtMs: Date.now() } });
    send({ method: "item/completed", params: { threadId, turnId, item: fileItem, completedAtMs: Date.now() } });
  }
  send({ method: "turn/completed", params: { threadId, turn } });
}

function responseForThread(thread: Record<string, unknown>) {
  return {
    thread,
    model: "fake-model",
    modelProvider: "openai",
    serviceTier: null,
    cwd: thread.cwd,
    instructionSources: [],
    approvalPolicy: "never",
    approvalsReviewer: "user",
    sandbox: { type: "workspaceWrite", writableRoots: [thread.cwd], networkAccess: false },
    reasoningEffort: null,
  };
}

function turnStartResult(turn: Record<string, unknown>): { turn: Record<string, unknown> } {
  const returned = structuredClone(turn);
  if (process.argv.includes("--mismatch-turn-start-response")) {
    const user = (returned.items as Array<Record<string, unknown>>)[0];
    if (user) user.clientId = "mismatched-client";
  }
  return { turn: returned };
}

const lines = readline.createInterface({ input: process.stdin });
lines.on("line", (line) => {
  const message = JSON.parse(line) as Message;
  if (lifecycleFile && message.id !== undefined && message.method) {
    appendFileSync(lifecycleFile, `rpc:${process.pid}:${message.method}\n`);
  }

  if (message.id !== undefined && message.method === undefined) {
    const original = pendingServerRequests.get(message.id);
    if (original !== undefined) {
      pendingServerRequests.delete(message.id);
      send({ id: original, result: { response: message.result, error: message.error } });
    }
    return;
  }

  if (message.method === "initialize") {
    initializeCount += 1;
    send({ id: message.id, result: {
      userAgent,
      codexHome: "/tmp/fake-codex-home",
      platformFamily: "unix",
      platformOs: "linux",
    } });
    return;
  }
  if (message.method === "initialized") {
    initialized = true;
    return;
  }
  if (!initialized) {
    send({ id: message.id, error: { code: -32000, message: "Not initialized" } });
    return;
  }

  switch (message.method) {
    case "account/read":
      send({
        id: message.id,
        result: process.argv.includes("--unauth")
          ? { account: null, requiresOpenaiAuth: true }
          : { account: { type: "chatgpt", email: "fake@example.com", planType: "plus" }, requiresOpenaiAuth: true },
      });
      break;
    case "test/state":
      send({ id: message.id, result: { initialized, initializeCount, pid: process.pid } });
      break;
    case "test/delay": {
      const delay = Number(message.params?.delay ?? 0);
      setTimeout(() => send({ id: message.id, result: { value: message.params?.value } }), delay);
      break;
    }
    case "test/notify":
      send({ method: "warning", params: { threadId: null, message: "fake warning" } });
      send({ id: message.id, result: {} });
      break;
    case "test/lastRequests":
      send({
        id: message.id,
        result: {
          lastTurnParams,
          lastResumeParams,
          lastThreadStartParams,
          allTurnParams,
          threadStartCount,
          threadReadCount,
          threadReadIncludeTurns,
          threadTurnsListCount,
          threadItemsListCount,
          threadNameSetCount,
          threadResumeCount,
          turnStartCount,
        },
      });
      break;
    case "test/environment": {
      const keys = Array.isArray(message.params?.keys)
        ? message.params.keys.filter((key): key is string => typeof key === "string")
        : [];
      send({
        id: message.id,
        result: { values: Object.fromEntries(keys.map((key) => [key, process.env[key] ?? null])) },
      });
      break;
    }
    case "test/setResumeResponseThreadId":
      resumeResponseThreadIdOverride = String(message.params?.threadId);
      send({ id: message.id, result: {} });
      break;
    case "test/blockNextMetadataRead":
      if (blockNextMetadataRead || blockedMetadataRead !== undefined) {
        send({ id: message.id, error: { code: -32000, message: "Metadata read barrier already armed" } });
        break;
      }
      blockNextMetadataRead = true;
      send({ id: message.id, result: {} });
      break;
    case "test/waitForMetadataReadBarrier":
      if (blockedMetadataRead !== undefined) {
        send({ id: message.id, result: {} });
      } else {
        metadataReadBarrierWaiters.push(message.id!);
      }
      break;
    case "test/releaseMetadataRead": {
      const blocked = blockedMetadataRead;
      if (blocked === undefined) {
        send({ id: message.id, error: { code: -32000, message: "Metadata read barrier is not blocked" } });
        break;
      }
      blockedMetadataRead = undefined;
      send({ id: blocked.id, result: blocked.result });
      send({ id: message.id, result: {} });
      break;
    }
    case "test/setThreadCwd": {
      const thread = threads.get(String(message.params?.threadId));
      if (!thread) send({ id: message.id, error: { code: -32001, message: "Thread not found" } });
      else {
        thread.cwd = message.params?.cwd;
        persistState();
        send({ id: message.id, result: {} });
      }
      break;
    }
    case "test/setThreadName": {
      const thread = threads.get(String(message.params?.threadId));
      if (!thread) send({ id: message.id, error: { code: -32001, message: "Thread not found" } });
      else {
        thread.name = message.params?.name;
        persistState();
        send({ id: message.id, result: {} });
      }
      break;
    }
    case "test/serverRequest": {
      const serverId = nextServerRequest++;
      pendingServerRequests.set(serverId, message.id!);
      send({ id: serverId, method: message.params?.method, params: { threadId: "thread-fake" } });
      break;
    }
    case "test/hang":
      break;
    case "test/crash":
      if (process.argv.includes("--stderr-on-crash")) {
        process.stderr.write(`${"x".repeat(10_000)} fake stderr tail`);
      }
      process.exit(23);
      break;
    case "test/malformed":
      process.stdout.write("{malformed-json\n");
      break;
    case "test/closeStdin":
      send({ id: message.id, result: {} });
      setTimeout(() => {
        keepAliveAfterStdinClose = true;
        closeSync(0);
        setInterval(() => undefined, 1_000);
      }, 5);
      break;
    case "thread/start": {
      threadStartCount += 1;
      lastThreadStartParams = message.params;
      const id = `thread-${nextThread++}`;
      const thread = makeThread(
        id,
        String(message.params?.cwd),
        typeof message.params?.threadSource === "string" ? message.params.threadSource : null,
      );
      threads.set(id, thread);
      persistState();
      send({ id: message.id, result: responseForThread(thread) });
      break;
    }
    case "thread/read": {
      threadReadCount += 1;
      threadReadIncludeTurns.push(message.params?.includeTurns === true);
      const thread = threads.get(String(message.params?.threadId));
      if (!thread) {
        send({ id: message.id, error: { code: -32001, message: "Thread not found" } });
      } else {
        const returned = structuredClone(thread);
        if (!message.params?.includeTurns) {
          returned.turns = [];
          if (process.argv.includes("--drop-persisted-thread-source")) {
            returned.status = { type: "notLoaded" };
          }
        }
        const result = { thread: returned };
        if (process.argv.includes("--signal-generation-change-after-metadata")
            && !message.params?.includeTurns && (thread.turns as unknown[]).length > 0) {
          send({ method: "test/generationChanged", params: {} });
        }
        if (!message.params?.includeTurns && blockNextMetadataRead) {
          blockNextMetadataRead = false;
          blockedMetadataRead = { id: message.id!, result };
          for (const waiter of metadataReadBarrierWaiters.splice(0)) {
            send({ id: waiter, result: {} });
          }
        } else {
          send({ id: message.id, result });
        }
      }
      break;
    }
    case "thread/turns/list": {
      threadTurnsListCount += 1;
      const thread = threads.get(String(message.params?.threadId));
      if (!thread) {
        send({ id: message.id, error: { code: -32001, message: "Thread not found" } });
        break;
      }
      if (inspectionFixture && (thread.turns as unknown[]).length > 0) {
        const pageIndex = Number(message.params?.cursor ?? 0);
        const page = inspectionFixture.officialTurnsPages[pageIndex];
        if (!page) {
          send({ id: message.id, result: { data: [], nextCursor: null, backwardsCursor: null } });
        } else {
          send({ id: message.id, result: page });
        }
        break;
      }
      const all = (process.argv.includes("--omit-turns-list")
          || (process.argv.includes("--hide-all-nonempty-turns")
            && (thread.turns as unknown[]).length > 0))
        ? []
        : structuredClone(thread.turns as Array<Record<string, unknown>>).reverse();
      if (process.argv.includes("--empty-turn-page-with-next")
          && (thread.turns as unknown[]).length > 0) {
        send({ id: message.id, result: { data: [], nextCursor: "again", backwardsCursor: null } });
        break;
      }
      if (process.argv.includes("--loop-turn-cursor")
          && (thread.turns as unknown[]).length > 0
          && message.params?.cursor === "loop") {
        const source = structuredClone((thread.turns as Array<Record<string, unknown>>)[0]!);
        source.id = "synthetic-loop-turn";
        source.items = [];
        source.itemsView = "notLoaded";
        send({ id: message.id, result: { data: [source], nextCursor: "loop", backwardsCursor: "loop" } });
        break;
      }
      const offset = Number(message.params?.cursor ?? 0);
      const limit = Math.max(1, forcedInspectionPageSize ?? Number(message.params?.limit ?? 100));
      const data = all.slice(offset, offset + limit).map((turn) => ({
        ...turn,
        items: [],
        itemsView: "notLoaded",
      }));
      const nextCursor = process.argv.includes("--loop-turn-cursor") && all.length > 0
        ? "loop"
        : offset + data.length < all.length ? String(offset + data.length) : null;
      const turnPage = { data, nextCursor, backwardsCursor: data.length ? String(offset) : null } as Record<string, unknown>;
      if (process.argv.includes("--missing-backwards-cursor") && data.length > 0) {
        delete turnPage.backwardsCursor;
      }
      send({ id: message.id, result: turnPage });
      break;
    }
    case "thread/items/list": {
      threadItemsListCount += 1;
      const thread = threads.get(String(message.params?.threadId));
      if (!thread) {
        send({ id: message.id, error: { code: -32001, message: "Thread not found" } });
        break;
      }
      const requestedTurnId = typeof message.params?.turnId === "string"
        ? message.params.turnId
        : null;
      const all = (thread.turns as Array<Record<string, unknown>>)
        .filter((turn) => requestedTurnId === null || turn.id === requestedTurnId)
        .flatMap((turn) => {
          const visibleAt = itemProjectionVisibleAt.get(String(turn.id));
          const items = visibleAt !== undefined && Date.now() < visibleAt
            ? (turn.items as Array<Record<string, unknown>>).filter((item) => item.type === "userMessage")
            : turn.items as unknown[];
          return items.map((item) => ({ turnId: turn.id, item }));
        });
      if (process.argv.includes("--empty-filtered-items") && requestedTurnId !== null) {
        send({ id: message.id, result: { data: [], nextCursor: null, backwardsCursor: null } });
        break;
      }
      const offset = Number(message.params?.cursor ?? 0);
      const limit = Math.max(1, forcedInspectionPageSize ?? Number(message.params?.limit ?? 100));
      let data = structuredClone(all.slice(offset, offset + limit));
      if (process.argv.includes("--wrong-filtered-turn") && requestedTurnId !== null && data[0]) {
        data[0].turnId = "wrong-turn";
      }
      if (process.argv.includes("--duplicate-item-entry") && data[0]) {
        data = [data[0], structuredClone(data[0]), ...data.slice(1)];
      }
      if (process.argv.includes("--unknown-item") && requestedTurnId !== null) {
        data = [{ turnId: requestedTurnId, item: { type: "futureItem", id: "future-item" } }];
      }
      if (process.argv.includes("--agent-missing-delivery") && requestedTurnId !== null) {
        data = [{ turnId: requestedTurnId, item: {
          type: "agentMessage", id: "bad-agent", text: "final", phase: "final_answer", memoryCitation: null,
        } }];
      }
      if (process.argv.includes("--file-change-missing-fields") && requestedTurnId !== null) {
        data = [{ turnId: requestedTurnId, item: { type: "fileChange", id: "bad-file-change" } }];
      }
      const nextCursor = offset + data.length < all.length ? String(offset + data.length) : null;
      send({ id: message.id, result: { data, nextCursor, backwardsCursor: data.length ? String(offset) : null } });
      break;
    }
    case "thread/name/set": {
      threadNameSetCount += 1;
      const thread = threads.get(String(message.params?.threadId));
      if (!thread) send({ id: message.id, error: { code: -32001, message: "Thread not found" } });
      else {
        thread.name = message.params?.name ?? null;
        if (process.argv.includes("--drop-persisted-thread-source")) {
          thread.threadSource = null;
          thread.source = "vscode";
        }
        persistState();
        send({ id: message.id, result: {} });
      }
      break;
    }
    case "thread/resume": {
      threadResumeCount += 1;
      lastResumeParams = message.params;
      const thread = threads.get(String(message.params?.threadId));
      if (!thread) send({ id: message.id, error: { code: -32001, message: "Thread not found" } });
      else {
        thread.cwd = message.params?.cwd ?? thread.cwd;
        persistState();
        const returned = structuredClone(thread);
        if (resumeResponseThreadIdOverride) {
          returned.id = resumeResponseThreadIdOverride;
          resumeResponseThreadIdOverride = undefined;
        }
        if (process.argv.includes("--drop-name-on-resume")) {
          returned.name = null;
        }
        send({ id: message.id, result: responseForThread(returned) });
      }
      break;
    }
    case "turn/start": {
      turnStartCount += 1;
      lastTurnParams = message.params;
      allTurnParams.push(message.params ?? {});
      const threadId = String(message.params?.threadId);
      const thread = threads.get(threadId);
      if (!thread) {
        send({ id: message.id, error: { code: -32001, message: "Thread not found" } });
        break;
      }
      const turnId = `turn-${nextTurn++}`;
      const turn = {
        id: turnId,
        items: [{
          type: "userMessage",
          id: `user-${turnId}`,
          clientId: typeof message.params?.clientUserMessageId === "string"
            ? message.params.clientUserMessageId
            : null,
          content: structuredClone(message.params?.input ?? []),
        }],
        itemsView: "full",
        status: "inProgress",
        error: null,
        startedAt: Math.floor(Date.now() / 1000),
        completedAt: null,
        durationMs: null,
      };
      (thread.turns as unknown[]).push(turn);
      thread.status = { type: "active", activeFlags: ["waitingOnModel"] };
      persistState();
      const input = JSON.stringify(message.params?.input ?? []);
      if (input.includes("[HANG_TURN_START]")) {
        send({ method: "turn/started", params: { threadId, turn } });
        setTimeout(() => completeTurn(threadId, turnId, "completed", input), 10);
      } else if (input.includes("[STARTED_BEFORE_RESPONSE]")) {
        send({ method: "turn/started", params: { threadId, turn } });
        send({ id: message.id, result: turnStartResult(turn) });
        if (!input.includes("[LONG]")) {
          setTimeout(() => completeTurn(threadId, turnId, "completed", input), 10);
        }
      } else if (input.includes("[EARLY]")) {
        send({ method: "turn/started", params: { threadId, turn } });
        completeTurn(threadId, turnId, "completed", input);
        send({ id: message.id, result: turnStartResult(turn) });
      } else {
        send({ id: message.id, result: turnStartResult(turn) });
        send({ method: "turn/started", params: { threadId, turn } });
        if (input.includes("[CRASH]")) {
          setTimeout(() => process.exit(24), 5);
        } else if (!input.includes("[LONG]")) {
          const status = input.includes("[FAIL]") ? "failed" : "completed";
          setTimeout(() => completeTurn(threadId, turnId, status, input), 10);
        }
      }
      break;
    }
    case "turn/interrupt":
      send({ id: message.id, result: {} });
      setTimeout(
        () => completeTurn(String(message.params?.threadId), String(message.params?.turnId), "interrupted"),
        5,
      );
      break;
    default:
      send({ id: message.id, error: { code: -32601, message: `Unknown method ${String(message.method)}` } });
  }
});

lines.on("close", () => {
  if (!keepAliveAfterStdinClose) process.exit(0);
});
