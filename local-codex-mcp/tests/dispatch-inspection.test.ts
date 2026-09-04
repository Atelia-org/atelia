import assert from "node:assert/strict";
import test from "node:test";
import type { ThreadItem } from "../schemas/v2/ThreadItem.js";
import type { Turn } from "../schemas/v2/Turn.js";
import { classifyTurnEvidence } from "../src/codex/dispatch-inspection.js";
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

test("live observations retain terminal across late running and collect item/completed final", () => {
  const observations = new LiveTurnObservations({ maximumObservations: 2, maximumFinalUtf8Bytes: 100 });
  const running = turn("t", "inProgress", [userMessage("d", "task")]);
  observations.observeTurn("thread", running);
  assert.equal(observations.inspect("thread", "t", "d", "task")?.kind, "running");
  observations.observeItem("thread", "t", agentMessage("final"));
  observations.observeTurn("thread", turn("t", "completed", [userMessage("d", "task")]));
  assert.deepEqual(observations.inspect("thread", "t", "d", "task"), {
    kind: "completed", threadId: "thread", turnId: "t", source: "live", final: "final",
  });
  observations.observeTurn("thread", running);
  assert.equal(observations.inspect("thread", "t", "d", "task")?.kind, "completed");
});

test("terminal and item notifications before an incomplete late start response still win", () => {
  const observations = new LiveTurnObservations({ maximumObservations: 2, maximumFinalUtf8Bytes: 100 });
  observations.observeTurn("thread", turn("t", "inProgress", [userMessage("d", "task")]));
  observations.observeItem("thread", "t", agentMessage("early final"));
  observations.observeTurn("thread", turn("t", "completed", []));
  observations.observeTurn("thread", turn("t", "inProgress", [userMessage("d", "task")]));
  assert.deepEqual(observations.inspect("thread", "t", "d", "task"), {
    kind: "completed", threadId: "thread", turnId: "t", source: "live", final: "early final",
  });
});

test("live observations are bounded, clearable, digest exact UTF-16, and conflict closed", () => {
  const observations = new LiveTurnObservations({ maximumObservations: 1, maximumFinalUtf8Bytes: 3 });
  observations.observeTurn("thread", turn("t1", "inProgress", [userMessage("d", "\ud800")]));
  observations.observeTurn("thread", turn("t2", "inProgress", [userMessage("d2", "task2")]));
  assert.equal(observations.inspect("thread", "t1", "d", "\ud800")?.kind, "running");
  assert.equal(observations.inspect("thread", "t1", "d", "\ufffd"), undefined);
  assert.equal(observations.inspect("thread", "t2", "d2", "task2"), undefined);
  observations.observeItem("thread", "t1", agentMessage("one", "same"));
  observations.observeItem("thread", "t1", agentMessage("two", "same"));
  assert.equal(observations.inspect("thread", "t1", "d", "\ud800")?.kind, "ambiguous");
  observations.clear();
  assert.equal(observations.inspect("thread", "t1", "d", "\ud800"), undefined);

  const boundedFinal = new LiveTurnObservations({ maximumObservations: 1, maximumFinalUtf8Bytes: 3 });
  boundedFinal.observeTurn("thread", turn("terminal", "inProgress", [userMessage("d", "task")]));
  boundedFinal.observeItem("thread", "terminal", agentMessage("oversize"));
  boundedFinal.observeTurn("thread", turn("terminal", "completed", [userMessage("d", "task")]));
  const result = boundedFinal.inspect("thread", "terminal", "d", "task");
  assert.equal(result?.kind, "failed");
  if (result?.kind === "failed") assert.equal(result.code, "FINAL_TOO_LARGE");
});

test("live final selection prefers a small explicit final over an oversize legacy candidate", () => {
  const observations = new LiveTurnObservations({ maximumObservations: 1, maximumFinalUtf8Bytes: 8 });
  observations.observeTurn("thread", turn("t", "inProgress", [userMessage("d", "task")]));
  observations.observeItem("thread", "t", agentMessage("legacy text is oversize", "legacy", null));
  observations.observeItem("thread", "t", agentMessage("final", "explicit", "final_answer"));
  observations.observeTurn("thread", turn("t", "completed", [userMessage("d", "task")]));
  assert.deepEqual(observations.inspect("thread", "t", "d", "task"), {
    kind: "completed", threadId: "thread", turnId: "t", source: "live", final: "final",
  });
});

test("live terminal evidence is first-wins, duplicate-idempotent, and conflicting closed", () => {
  const observations = new LiveTurnObservations({ maximumObservations: 1, maximumFinalUtf8Bytes: 100 });
  const running = turn("t", "inProgress", [userMessage("d", "task")]);
  const completed = turn("t", "completed", [userMessage("d", "task"), agentMessage("final")]);
  observations.observeTurn("thread", running);
  observations.observeTurn("thread", completed);
  observations.observeTurn("thread", completed);
  assert.equal(observations.inspect("thread", "t", "d", "task")?.kind, "completed");
  observations.observeTurn("thread", turn("t", "failed", [userMessage("d", "task")]));
  const conflict = observations.inspect("thread", "t", "d", "task");
  assert.equal(conflict?.kind, "ambiguous");
  if (conflict?.kind === "ambiguous") assert.equal(conflict.code, "LIVE_OBSERVATION_CONFLICT");
});

test("a new turn on the fixed thread replaces old terminal capacity", () => {
  const observations = new LiveTurnObservations({ maximumObservations: 1, maximumFinalUtf8Bytes: 100 });
  observations.observeTurn("thread", turn("old", "completed", [
    userMessage("old-dispatch", "old task"), agentMessage("old final"),
  ]));
  observations.observeTurn("thread", turn("new", "inProgress", [userMessage("new-dispatch", "new task")]));
  assert.equal(observations.inspect("thread", "old", "old-dispatch", "old task"), undefined);
  assert.deepEqual(observations.inspect("thread", "new", "new-dispatch", "new task"), {
    kind: "running", threadId: "thread", turnId: "new", source: "live",
  });
});

test("live semantic item identity metadata is bounded and duplicate IDs fail closed", () => {
  const duplicate = new LiveTurnObservations({ maximumObservations: 1, maximumFinalUtf8Bytes: 100 });
  duplicate.observeTurn("thread", turn("t", "inProgress", [userMessage("d", "task", "shared")]));
  duplicate.observeItem("thread", "t", agentMessage("final", "shared"));
  assert.equal(duplicate.inspect("thread", "t", "d", "task")?.kind, "ambiguous");

  const capped = new LiveTurnObservations({ maximumObservations: 1, maximumFinalUtf8Bytes: 100 });
  capped.observeTurn("thread", turn("t", "inProgress", [userMessage("d", "task")]));
  for (let index = 0; index < 64; index += 1) {
    capped.observeItem("thread", "t", agentMessage("ignored commentary", `commentary-${index}`, "commentary"));
  }
  const overflow = capped.inspect("thread", "t", "d", "task");
  assert.equal(overflow?.kind, "ambiguous");
  if (overflow?.kind === "ambiguous") assert.equal(overflow.code, "LIVE_OBSERVATION_CONFLICT");
});
