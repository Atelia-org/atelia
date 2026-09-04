import assert from "node:assert/strict";
import test from "node:test";
import type { ThreadItem } from "../schemas/v2/ThreadItem.js";
import type { Turn } from "../schemas/v2/Turn.js";
import {
  classifyTurnEvidence,
  reconcilePendingLiveCompletion,
} from "../src/codex/dispatch-inspection.js";
import { LiveTurnObservations } from "../src/codex/live-turn-observations.js";

function userMessage(clientId: string, text: string, id = `user-${clientId}`): ThreadItem {
  return { type: "userMessage", id, clientId, content: [{ type: "text", text, text_elements: [] }] };
}

function agentMessage(text: string, id = "agent", phase: "commentary" | "final_answer" | null = "final_answer"): ThreadItem {
  return { type: "agentMessage", id, text, phase, memoryCitation: null, delivery: null };
}

function turn(id: string, status: Turn["status"], items: ThreadItem[]): Turn {
  return {
    id,
    status,
    items,
    itemsView: "full",
    error: null,
    startedAt: 1,
    completedAt: status === "inProgress" ? null : 2,
    durationMs: status === "inProgress" ? null : 1,
  };
}

test("exact turn evidence classifies all terminal outcomes and final bounds", () => {
  const cases = [
    [turn("t", "inProgress", []), "running"],
    [turn("t", "failed", []), "failed"],
    [turn("t", "interrupted", []), "failed"],
    [turn("t", "completed", []), "completed"],
  ] as const;
  for (const [target, kind] of cases) {
    const items = [userMessage("d", "task"), ...(target.status === "completed" ? [agentMessage("final")] : [])];
    assert.equal(classifyTurnEvidence("thread", target, items, "d", "task", 100, "persistent").kind, kind);
  }
  for (const [final, code] of [[" ", "FINAL_BLANK"], ["\ud800", "FINAL_INVALID_UNICODE"], ["123456", "FINAL_TOO_LARGE"]] as const) {
    const value = classifyTurnEvidence(
      "thread",
      turn("t", "completed", []),
      [userMessage("d", "task"), agentMessage(final)],
      "d",
      "task",
      5,
      "persistent",
    );
    assert.equal(value.kind, "failed");
    if (value.kind === "failed") assert.equal(value.code, code);
  }
});

test("exact turn evidence fails closed on selector, body, and final ambiguity", () => {
  const target = turn("t", "completed", []);
  const cases: Array<[ThreadItem[], string]> = [
    [[userMessage("other", "task")], "DISPATCH_TURN_MISMATCH"],
    [[userMessage("d", "other")], "DISPATCH_BODY_MISMATCH"],
    [[userMessage("d", "task", "u1"), userMessage("d", "task", "u2")], "DISPATCH_ID_NOT_UNIQUE"],
    [[userMessage("d", "task"), agentMessage("one", "a1"), agentMessage("two", "a2")], "FINAL_AMBIGUOUS"],
  ];
  for (const [items, code] of cases) {
    const value = classifyTurnEvidence("thread", target, items, "d", "task", 100, "persistent");
    assert.equal(value.kind, "ambiguous");
    if (value.kind === "ambiguous") assert.equal(value.code, code);
  }
});

test("pending live completion reconciles the complete cold evidence matrix", () => {
  const completed = { kind: "completed", threadId: "thread", turnId: "t", source: "persistent", final: "final" } as const;
  assert.equal(reconcilePendingLiveCompletion("thread", completed), completed);
  for (const code of ["FINAL_BLANK", "FINAL_INVALID_UNICODE", "FINAL_TOO_LARGE"] as const) {
    const failed = { kind: "failed", threadId: "thread", turnId: "t", source: "persistent", code } as const;
    assert.equal(reconcilePendingLiveCompletion("thread", failed), failed);
  }
  for (const code of ["TURN_FAILED", "TURN_INTERRUPTED"] as const) {
    assert.deepEqual(reconcilePendingLiveCompletion("thread", {
      kind: "failed", threadId: "thread", turnId: "t", source: "persistent", code,
    }), {
      kind: "ambiguous", threadId: "thread", source: "live", code: "LIVE_OBSERVATION_CONFLICT",
    });
  }
  const semantic = {
    kind: "ambiguous", threadId: "thread", source: "persistent", code: "DISPATCH_BODY_MISMATCH",
  } as const;
  assert.equal(reconcilePendingLiveCompletion("thread", semantic), semantic);
  for (const incomplete of [
    { kind: "running", threadId: "thread", turnId: "t", source: "persistent" },
    { kind: "unavailable", threadId: "thread", turnId: "t", source: "persistent", code: "ACCEPTED_TURN_NOT_VISIBLE" },
    { kind: "failed", threadId: "thread", turnId: "t", source: "persistent", code: "FINAL_MISSING" },
  ] as const) {
    assert.equal(reconcilePendingLiveCompletion("thread", incomplete), null);
  }
});

test("live observations retain terminal across late running and collect item/completed final", () => {
  const observations = new LiveTurnObservations({ maximumObservations: 2, maximumFinalUtf8Bytes: 100 });
  const running = turn("t", "inProgress", [userMessage("d", "task")]);
  observations.observeStartResponse("thread", running);
  assert.equal(observations.inspect("thread", "t", "d", "task")?.kind, "running");
  observations.observeItem("thread", "t", agentMessage("final"));
  observations.observeTurnCompleted("thread", turn("t", "completed", [userMessage("d", "task")]));
  assert.deepEqual(observations.inspect("thread", "t", "d", "task"), {
    kind: "completed", threadId: "thread", turnId: "t", source: "live", final: "final",
  });
  observations.observeStartResponse("thread", running);
  assert.equal(observations.inspect("thread", "t", "d", "task")?.kind, "completed");
});

test("terminal and item notifications before an incomplete late start response still win", () => {
  const observations = new LiveTurnObservations({ maximumObservations: 2, maximumFinalUtf8Bytes: 100 });
  const expectation = observations.beginStart("thread", "d", "task");
  observations.observeTurnStarted("thread", turn("t", "inProgress", [userMessage("d", "task")]));
  observations.observeItem("thread", "t", agentMessage("early final"));
  observations.observeTurnCompleted("thread", turn("t", "completed", []));
  observations.observeStartResponse("thread", turn("t", "inProgress", [userMessage("d", "task")]));
  observations.endStart(expectation);
  assert.deepEqual(observations.inspect("thread", "t", "d", "task"), {
    kind: "completed", threadId: "thread", turnId: "t", source: "live", final: "early final",
  });
});

test("a full exact terminal notification can establish before the start response", () => {
  const observations = new LiveTurnObservations({ maximumObservations: 1, maximumFinalUtf8Bytes: 100 });
  const expectation = observations.beginStart("thread", "d", "task");
  const completed = turn("t", "completed", [userMessage("d", "task"), agentMessage("final")]);
  observations.observeTurnCompleted("thread", completed);
  assert.deepEqual(observations.inspect("thread", "t", "d", "task"), {
    kind: "completed", threadId: "thread", turnId: "t", source: "live", final: "final",
  });
  assert.equal(observations.observeStartResponse(
    "thread",
    turn("t", "inProgress", [userMessage("d", "task")]),
    expectation,
  ), true);
  observations.endStart(expectation);
  assert.equal(observations.inspect("thread", "t", "d", "task")?.kind, "completed");
});

test("a trusted start response still must match its exact pending identity", () => {
  const observations = new LiveTurnObservations({ maximumObservations: 1, maximumFinalUtf8Bytes: 100 });
  const expectation = observations.beginStart("thread", "d", "task");
  assert.equal(observations.observeStartResponse(
    "thread",
    turn("wrong", "inProgress", [userMessage("different", "different task")]),
    expectation,
  ), false);
  observations.endStart(expectation);
  assert.equal(observations.inspect("thread", "wrong", "different", "different task"), undefined);
});

test("an unassociated incomplete terminal barrier binds to the later exact start response", () => {
  const observations = new LiveTurnObservations({ maximumObservations: 1, maximumFinalUtf8Bytes: 100 });
  const expectation = observations.beginStart("thread", "d", "task");
  observations.observeTurnCompleted("thread", {
    ...turn("t", "completed", []),
    itemsView: "summary",
  });
  assert.equal(observations.inspect("thread", "t", "d", "task"), undefined);
  assert.equal(observations.observeStartResponse(
    "thread",
    turn("t", "inProgress", [userMessage("d", "task")]),
    expectation,
  ), true);
  observations.endStart(expectation);
  assert.equal(observations.inspect("thread", "t", "d", "task"), undefined);
  assert.equal(observations.isAwaitingTerminalEvidence("thread", "t", "d", "task"), true);
  observations.observeItem("thread", "t", agentMessage("late final"));
  assert.equal(observations.inspect("thread", "t", "d", "task")?.kind, "completed");
});

for (const [status, code] of [
  ["failed", "TURN_FAILED"],
  ["interrupted", "TURN_INTERRUPTED"],
] as const) {
  test(`an unassociated ${status} barrier settles after exact response association`, () => {
    const observations = new LiveTurnObservations({ maximumObservations: 1, maximumFinalUtf8Bytes: 100 });
    const expectation = observations.beginStart("thread", "d", "task");
    observations.observeTurnCompleted("thread", {
      ...turn("t", status, []),
      itemsView: "notLoaded",
    });
    assert.equal(observations.observeStartResponse(
      "thread",
      turn("t", "inProgress", [userMessage("d", "task")]),
      expectation,
    ), true);
    observations.endStart(expectation);
    const terminal = observations.inspect("thread", "t", "d", "task");
    assert.equal(terminal?.kind, "failed");
    if (terminal?.kind === "failed") assert.equal(terminal.code, code);
  });
}

test("out-of-order unassociated terminal candidates are keyed and exact response selects only its turn", () => {
  const observations = new LiveTurnObservations({ maximumObservations: 1, maximumFinalUtf8Bytes: 100 });
  const expectation = observations.beginStart("thread", "d", "task");
  observations.observeTurnCompleted("thread", {
    ...turn("old", "completed", []),
    itemsView: "summary",
  });
  observations.observeTurnCompleted("thread", {
    ...turn("exact", "interrupted", []),
    itemsView: "notLoaded",
  });
  assert.equal(observations.observeStartResponse(
    "thread",
    turn("exact", "inProgress", [userMessage("d", "task")]),
    expectation,
  ), true);
  observations.endStart(expectation);
  const terminal = observations.inspect("thread", "exact", "d", "task");
  assert.equal(terminal?.kind, "failed");
  if (terminal?.kind === "failed") assert.equal(terminal.code, "TURN_INTERRUPTED");
  assert.equal(observations.inspect("thread", "old", "d", "task"), undefined);
});

test("pending capacity loss never bypasses start-response identity validation", () => {
  const observations = new LiveTurnObservations({ maximumObservations: 1, maximumFinalUtf8Bytes: 100 });
  const occupying = observations.beginStart("occupied", "d1", "task1");
  const untracked = observations.beginStart("thread", "d2", "task2");
  assert.equal(untracked.tracked, false);
  assert.equal(observations.observeStartResponse(
    "thread",
    turn("wrong", "inProgress", [userMessage("wrong", "wrong task")]),
    untracked,
  ), false);
  assert.equal(observations.observeStartResponse(
    "thread",
    turn("right", "inProgress", [userMessage("d2", "task2")]),
    untracked,
  ), true);
  assert.equal(observations.inspect("thread", "right", "d2", "task2"), undefined);
  observations.endStart(untracked);
  observations.endStart(occupying);
});

test("live observations are bounded, clearable, digest exact UTF-16, and conflict closed", () => {
  const observations = new LiveTurnObservations({ maximumObservations: 1, maximumFinalUtf8Bytes: 3 });
  observations.observeStartResponse("thread", turn("t1", "inProgress", [userMessage("d", "\ud800")]));
  observations.observeStartResponse("other-thread", turn("t2", "inProgress", [userMessage("d2", "task2")]));
  assert.equal(observations.inspect("thread", "t1", "d", "\ud800")?.kind, "running");
  assert.equal(observations.inspect("thread", "t1", "d", "\ufffd"), undefined);
  assert.equal(observations.inspect("other-thread", "t2", "d2", "task2"), undefined);
  observations.observeItem("thread", "t1", agentMessage("one", "same"));
  observations.observeItem("thread", "t1", agentMessage("two", "same"));
  assert.equal(observations.inspect("thread", "t1", "d", "\ud800")?.kind, "ambiguous");
  observations.clear();
  assert.equal(observations.inspect("thread", "t1", "d", "\ud800"), undefined);

  const boundedFinal = new LiveTurnObservations({ maximumObservations: 1, maximumFinalUtf8Bytes: 3 });
  boundedFinal.observeStartResponse("thread", turn("terminal", "inProgress", [userMessage("d", "task")]));
  boundedFinal.observeItem("thread", "terminal", agentMessage("oversize"));
  boundedFinal.observeTurnCompleted("thread", turn("terminal", "completed", [userMessage("d", "task")]));
  const result = boundedFinal.inspect("thread", "terminal", "d", "task");
  assert.equal(result?.kind, "failed");
  if (result?.kind === "failed") assert.equal(result.code, "FINAL_TOO_LARGE");
});

test("live final selection prefers a small explicit final over an oversize legacy candidate", () => {
  const observations = new LiveTurnObservations({ maximumObservations: 1, maximumFinalUtf8Bytes: 8 });
  observations.observeStartResponse("thread", turn("t", "inProgress", [userMessage("d", "task")]));
  observations.observeItem("thread", "t", agentMessage("legacy text is oversize", "legacy", null));
  observations.observeItem("thread", "t", agentMessage("final", "explicit", "final_answer"));
  observations.observeTurnCompleted("thread", turn("t", "completed", [userMessage("d", "task")]));
  assert.deepEqual(observations.inspect("thread", "t", "d", "task"), {
    kind: "completed", threadId: "thread", turnId: "t", source: "live", final: "final",
  });
});

test("live terminal evidence is first-wins, duplicate-idempotent, and conflicting closed", () => {
  const observations = new LiveTurnObservations({ maximumObservations: 1, maximumFinalUtf8Bytes: 100 });
  const running = turn("t", "inProgress", [userMessage("d", "task")]);
  const completed = turn("t", "completed", [userMessage("d", "task"), agentMessage("final")]);
  observations.observeStartResponse("thread", running);
  observations.observeTurnCompleted("thread", completed);
  observations.observeTurnCompleted("thread", completed);
  assert.equal(observations.inspect("thread", "t", "d", "task")?.kind, "completed");
  observations.observeTurnCompleted("thread", turn("t", "failed", [userMessage("d", "task")]));
  const conflict = observations.inspect("thread", "t", "d", "task");
  assert.equal(conflict?.kind, "ambiguous");
  if (conflict?.kind === "ambiguous") assert.equal(conflict.code, "LIVE_OBSERVATION_CONFLICT");
});

test("a new turn on the fixed thread replaces old terminal capacity", () => {
  const observations = new LiveTurnObservations({ maximumObservations: 1, maximumFinalUtf8Bytes: 100 });
  observations.observeStartResponse("thread", turn("old", "inProgress", [userMessage("old-dispatch", "old task")]));
  observations.observeTurnCompleted("thread", turn("old", "completed", [
    userMessage("old-dispatch", "old task"), agentMessage("old final"),
  ]));
  observations.observeStartResponse("thread", turn("new", "inProgress", [userMessage("new-dispatch", "new task")]));
  assert.equal(observations.inspect("thread", "old", "old-dispatch", "old task"), undefined);
  assert.deepEqual(observations.inspect("thread", "new", "new-dispatch", "new task"), {
    kind: "running", threadId: "thread", turnId: "new", source: "live",
  });
});

test("live semantic item identity metadata is bounded and duplicate IDs fail closed", () => {
  const duplicate = new LiveTurnObservations({ maximumObservations: 1, maximumFinalUtf8Bytes: 100 });
  duplicate.observeStartResponse("thread", turn("t", "inProgress", [userMessage("d", "task", "shared")]));
  duplicate.observeItem("thread", "t", agentMessage("final", "shared"));
  assert.equal(duplicate.inspect("thread", "t", "d", "task")?.kind, "ambiguous");

  const commentary = new LiveTurnObservations({ maximumObservations: 1, maximumFinalUtf8Bytes: 100 });
  commentary.observeStartResponse("thread", turn("t", "inProgress", [userMessage("d", "task")]));
  for (let index = 0; index < 1_000; index += 1) {
    commentary.observeItem("thread", "t", agentMessage("ignored commentary", `commentary-${index}`, "commentary"));
  }
  assert.equal(commentary.inspect("thread", "t", "d", "task")?.kind, "running");
});

test("late old terminal events cannot replace or corrupt the current fixed-thread turn", () => {
  const observations = new LiveTurnObservations({ maximumObservations: 1, maximumFinalUtf8Bytes: 100 });
  const oldRunning = turn("old", "inProgress", [userMessage("old-dispatch", "old task")]);
  const oldCompleted = turn("old", "completed", [
    userMessage("old-dispatch", "old task"), agentMessage("old final", "old-final"),
  ]);
  const newRunning = turn("new", "inProgress", [userMessage("new-dispatch", "new task")]);
  const newCompleted = turn("new", "completed", [
    userMessage("new-dispatch", "new task"), agentMessage("new final", "new-final"),
  ]);
  observations.observeStartResponse("thread", oldRunning);
  observations.observeTurnCompleted("thread", oldCompleted);
  observations.observeStartResponse("thread", newRunning);
  observations.observeTurnCompleted("thread", newCompleted);
  observations.observeTurnCompleted("thread", oldCompleted);
  observations.observeItem("thread", "old", agentMessage("late old final", "old-late"));
  observations.observeTurnStarted("thread", oldRunning);

  assert.equal(observations.inspect("thread", "old", "old-dispatch", "old task"), undefined);
  assert.deepEqual(observations.inspect("thread", "new", "new-dispatch", "new task"), {
    kind: "completed", threadId: "thread", turnId: "new", source: "live", final: "new final",
  });
});

for (const itemsView of ["summary", "notLoaded"] as const) {
  test(`incomplete ${itemsView} completion waits for a late final item`, () => {
    const observations = new LiveTurnObservations({ maximumObservations: 1, maximumFinalUtf8Bytes: 100 });
    observations.observeStartResponse("thread", turn("t", "inProgress", [userMessage("d", "task")]));
    observations.observeTurnCompleted("thread", {
      ...turn("t", "completed", []),
      itemsView,
    });
    assert.equal(observations.inspect("thread", "t", "d", "task"), undefined);
    assert.equal(observations.isAwaitingTerminalEvidence("thread", "t", "d", "task"), true);
    observations.observeItem("thread", "t", agentMessage("late final"));
    assert.equal(observations.isAwaitingTerminalEvidence("thread", "t", "d", "task"), false);
    assert.deepEqual(observations.inspect("thread", "t", "d", "task"), {
      kind: "completed", threadId: "thread", turnId: "t", source: "live", final: "late final",
    });
  });
}
