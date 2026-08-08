import readline from "node:readline";
import { closeSync } from "node:fs";

type Message = { id?: string | number; method?: string; params?: Record<string, unknown>; result?: unknown; error?: unknown };

let initialized = false;
let initializeCount = 0;
let nextThread = 1;
let nextTurn = 1;
let nextServerRequest = 10_000;
const pendingServerRequests = new Map<string | number, string | number>();
const threads = new Map<string, Record<string, unknown>>();
let lastTurnParams: Record<string, unknown> | undefined;
let lastResumeParams: Record<string, unknown> | undefined;
let keepAliveAfterStdinClose = false;

if (process.argv.includes("--ignore-sigterm")) {
  process.on("SIGTERM", () => undefined);
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

function completeTurn(threadId: string, turnId: string, status: "completed" | "interrupted" = "completed"): void {
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
  const agentItem = {
    type: "agentMessage",
    id: `item-${turnId}`,
    text: report,
    phase: "final_answer",
    memoryCitation: null,
  };
  const fileItem = {
    type: "fileChange",
    id: `file-${turnId}`,
    changes: [{ path: "hello.txt", kind: "add", diff: "secret diff must not escape" }],
    status: "completed",
  };
  turn.status = status;
  turn.completedAt = Math.floor(Date.now() / 1000);
  turn.durationMs = 10;
  turn.items = status === "completed" ? [agentItem, fileItem] : [];
  thread.status = { type: "idle" };
  if (status === "completed") {
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

const lines = readline.createInterface({ input: process.stdin });
lines.on("line", (line) => {
  const message = JSON.parse(line) as Message;

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
    send({ id: message.id, result: { userAgent: "fake", platformFamily: "unix", platformOs: "linux" } });
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
      send({ id: message.id, result: { lastTurnParams, lastResumeParams } });
      break;
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
      const id = `thread-${nextThread++}`;
      const thread = makeThread(
        id,
        String(message.params?.cwd),
        typeof message.params?.threadSource === "string" ? message.params.threadSource : null,
      );
      threads.set(id, thread);
      send({ id: message.id, result: responseForThread(thread) });
      break;
    }
    case "thread/read": {
      const thread = threads.get(String(message.params?.threadId));
      if (!thread) {
        send({ id: message.id, error: { code: -32001, message: "Thread not found" } });
      } else {
        const returned = structuredClone(thread);
        if (!message.params?.includeTurns) returned.turns = [];
        send({ id: message.id, result: { thread: returned } });
      }
      break;
    }
    case "thread/name/set": {
      const thread = threads.get(String(message.params?.threadId));
      if (!thread) send({ id: message.id, error: { code: -32001, message: "Thread not found" } });
      else {
        thread.name = message.params?.name ?? null;
        send({ id: message.id, result: {} });
      }
      break;
    }
    case "thread/resume": {
      lastResumeParams = message.params;
      const thread = threads.get(String(message.params?.threadId));
      if (!thread) send({ id: message.id, error: { code: -32001, message: "Thread not found" } });
      else {
        thread.cwd = message.params?.cwd ?? thread.cwd;
        send({ id: message.id, result: responseForThread(thread) });
      }
      break;
    }
    case "turn/start": {
      lastTurnParams = message.params;
      const threadId = String(message.params?.threadId);
      const thread = threads.get(threadId);
      if (!thread) {
        send({ id: message.id, error: { code: -32001, message: "Thread not found" } });
        break;
      }
      const turnId = `turn-${nextTurn++}`;
      const turn = {
        id: turnId,
        items: [],
        itemsView: { type: "full" },
        status: "inProgress",
        error: null,
        startedAt: Math.floor(Date.now() / 1000),
        completedAt: null,
        durationMs: null,
      };
      (thread.turns as unknown[]).push(turn);
      thread.status = { type: "active", activeFlags: ["waitingOnModel"] };
      send({ id: message.id, result: { turn } });
      send({ method: "turn/started", params: { threadId, turn } });
      const input = JSON.stringify(message.params?.input ?? []);
      if (!input.includes("[LONG]")) setTimeout(() => completeTurn(threadId, turnId), 10);
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
