import assert from "node:assert/strict";
import test from "node:test";
import type { Thread } from "../schemas/v2/Thread.js";
import type { ThreadItem } from "../schemas/v2/ThreadItem.js";
import type { Turn } from "../schemas/v2/Turn.js";
import {
  classifyGalateaDispatch,
  type GalateaDispatchInspectionLimits,
} from "../src/codex/dispatch-inspection.js";

const limits: GalateaDispatchInspectionLimits = {
  maximumTurns: 10,
  maximumItems: 100,
  maximumFinalUtf8Bytes: 1_000,
};

function userMessage(
  clientId: string | null,
  text: string,
  id = `user-${clientId ?? "none"}`,
): ThreadItem {
  return {
    type: "userMessage",
    id,
    clientId,
    content: [{ type: "text", text, text_elements: [] }],
  };
}

function agentMessage(
  text: string,
  phase: "commentary" | "final_answer" | null = "final_answer",
): ThreadItem {
  return {
    type: "agentMessage",
    id: `agent-${phase ?? "legacy"}-${text}`,
    text,
    phase,
    memoryCitation: null,
  };
}

function turn(
  id: string,
  status: Turn["status"],
  items: ThreadItem[],
  itemsView: Turn["itemsView"] = "full",
): Turn {
  return {
    id,
    items,
    itemsView,
    status,
    error: status === "failed"
      ? { message: "failed", codexErrorInfo: null, additionalDetails: null }
      : null,
    startedAt: 1,
    completedAt: status === "inProgress" ? null : 2,
    durationMs: status === "inProgress" ? null : 1,
  };
}

function thread(turns: Turn[]): Thread {
  return {
    id: "thread-1",
    sessionId: "thread-1",
    forkedFromId: null,
    parentThreadId: null,
    preview: "",
    ephemeral: false,
    section: null,
    sectionEnteredAt: null,
    modelProvider: "openai",
    createdAt: 1,
    updatedAt: 2,
    recencyAt: 2,
    status: { type: turns.some((value) => value.status === "inProgress")
      ? "active"
      : "idle", ...(turns.some((value) => value.status === "inProgress")
      ? { activeFlags: [] }
      : {}) } as Thread["status"],
    path: null,
    cwd: "/workspace",
    cliVersion: "test",
    source: "appServer",
    threadSource: null,
    agentNickname: null,
    agentRole: null,
    gitInfo: null,
    name: "[galatea-codex-sidecar] thread-1",
    turns,
  };
}

test("dispatch inspection finds the exact non-latest clientId and classifies running", () => {
  const value = classifyGalateaDispatch(
    thread([
      turn("turn-1", "inProgress", [userMessage("mail-1", "exact task")]),
      turn("turn-2", "completed", [
        userMessage("mail-2", "later task"),
        agentMessage("later final"),
      ]),
    ]),
    "mail-1",
    "exact task",
    limits,
  );
  assert.deepEqual(value, {
    kind: "running",
    threadId: "thread-1",
    turnId: "turn-1",
  });
});

test("dispatch inspection returns an exact completed final with legacy fallback", () => {
  const explicit = classifyGalateaDispatch(
    thread([turn("turn-1", "completed", [
      userMessage("mail-1", "task"),
      agentMessage("progress", "commentary"),
      agentMessage("final"),
    ])]),
    "mail-1",
    "task",
    limits,
  );
  assert.deepEqual(explicit, {
    kind: "completed",
    threadId: "thread-1",
    turnId: "turn-1",
    final: "final",
  });

  const legacy = classifyGalateaDispatch(
    thread([turn("turn-1", "completed", [
      userMessage("mail-1", "task"),
      agentMessage("legacy final", null),
    ])]),
    "mail-1",
    "task",
    limits,
  );
  assert.equal(legacy.kind, "completed");
  if (legacy.kind === "completed") assert.equal(legacy.final, "legacy final");
});

test("dispatch inspection keeps not-found nonterminal and maps terminal failures", () => {
  assert.deepEqual(
    classifyGalateaDispatch(thread([]), "mail-1", "task", limits),
    { kind: "not-found", threadId: "thread-1" },
  );
  for (const [status, code] of [
    ["failed", "TURN_FAILED"],
    ["interrupted", "TURN_INTERRUPTED"],
  ] as const) {
    const value = classifyGalateaDispatch(
      thread([turn("turn-1", status, [userMessage("mail-1", "task")])]),
      "mail-1",
      "task",
      limits,
    );
    assert.equal(value.kind, "failed");
    if (value.kind === "failed") assert.equal(value.code, code);
  }
});

test("dispatch inspection fails closed on incomplete, duplicate, or mismatched evidence", () => {
  const cases: Array<[Thread, string]> = [
    [thread([turn("turn-1", "inProgress", [userMessage("mail-1", "task")], "summary")]),
      "TURN_ITEMS_INCOMPLETE"],
    [thread([
      turn("turn-1", "inProgress", [userMessage("mail-1", "task", "user-1")]),
      turn("turn-2", "inProgress", [userMessage("mail-1", "task", "user-2")]),
    ]), "DISPATCH_ID_NOT_UNIQUE"],
    [thread([turn("turn-1", "inProgress", [userMessage("mail-1", "different")])]),
      "DISPATCH_BODY_MISMATCH"],
    [thread([
      turn("turn-1", "completed", [userMessage("mail-1", "task"), agentMessage("a")]),
      turn("turn-1", "completed", [userMessage("mail-2", "other"), agentMessage("b")]),
    ]), "TURN_ID_NOT_UNIQUE"],
    [thread([turn("turn-1", "completed", [
      userMessage("mail-1", "task"),
      agentMessage("first"),
      agentMessage("second"),
    ])]), "FINAL_AMBIGUOUS"],
    [thread([turn("turn-1", "completed", [
      userMessage("mail-1", "task"),
      { ...agentMessage("first"), id: "duplicate-item" },
      { ...agentMessage("second"), id: "duplicate-item" },
    ])]), "ITEM_ID_NOT_UNIQUE"],
  ];
  for (const [source, code] of cases) {
    const value = classifyGalateaDispatch(source, "mail-1", "task", limits);
    assert.equal(value.kind, "ambiguous", code);
    if (value.kind === "ambiguous") assert.equal(value.code, code, code);
  }
});

test("dispatch inspection validates final text and operation bounds", () => {
  for (const [final, code] of [
    ["", "FINAL_BLANK"],
    ["   ", "FINAL_BLANK"],
    ["\ud800", "FINAL_INVALID_UNICODE"],
    ["123456", "FINAL_TOO_LARGE"],
  ] as const) {
    const value = classifyGalateaDispatch(
      thread([turn("turn-1", "completed", [
        userMessage("mail-1", "task"),
        agentMessage(final),
      ])]),
      "mail-1",
      "task",
      { ...limits, maximumFinalUtf8Bytes: 5 },
    );
    assert.equal(value.kind, "failed");
    if (value.kind === "failed") assert.equal(value.code, code);
  }

  assert.throws(() => classifyGalateaDispatch(
    thread([turn("turn-1", "inProgress", [userMessage("mail-1", "task")])]),
    "mail-1",
    "task",
    { ...limits, maximumTurns: 0 },
  ), RangeError);
  const limited = classifyGalateaDispatch(
    thread([
      turn("turn-1", "inProgress", [userMessage("mail-1", "task")]),
      turn("turn-2", "inProgress", [userMessage("mail-2", "other")]),
    ]),
    "mail-1",
    "task",
    { ...limits, maximumTurns: 1 },
  );
  assert.equal(limited.kind, "ambiguous");
  if (limited.kind === "ambiguous") {
    assert.equal(limited.code, "INSPECTION_LIMIT_EXCEEDED");
  }
});
