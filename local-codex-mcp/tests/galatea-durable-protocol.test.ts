import { taskCommitment } from "../src/galatea/task-commitment.js";
import assert from "node:assert/strict";
import test from "node:test";
import { encodeGalateaDurableOutputFrame, parseGalateaDurableFrame } from "../src/galatea/durable-protocol.js";

test("durable protocol accepts only exact V6 request shapes", () => {
  const frames = [
    { v: 6, type: "ensure-binding", cwd: "/workspace", requestId: "request-1", bindingOperationId: "binding-1" },
    { v: 6, type: "start-turn", cwd: "/workspace", requestId: "request-2", dispatchId: "dispatch-1", threadId: "thread-1", task: "exact\ntext" },
    { v: 6, type: "inspect-dispatch", requestId: "request-3", dispatchId: "dispatch-1", threadId: "thread-1", ...taskCommitment("exact\ntext"), expectedTurnId: null },
    { v: 6, type: "inspect-dispatch", requestId: "request-4", dispatchId: "dispatch-1", threadId: "thread-1", ...taskCommitment("exact\ntext"), expectedTurnId: "turn-1" },
  ] as const;
  for (const expected of frames) {
    const parsed = parseGalateaDurableFrame(JSON.stringify(expected));
    assert.equal(parsed.ok, true);
    if (parsed.ok) assert.deepEqual(parsed.frame, expected);
  }
  for (const invalid of [
    { ...frames[0], v: 2 },
    { ...frames[0], type: "Ensure-Binding" },
    { ...frames[0], cwd: undefined },
    { ...frames[0], v: 3 },
    { ...frames[0], v: 4 },
    { ...frames[0], v: 5 },
    { ...frames[1], cwd: undefined },
    { ...frames[1], cwd: "" },
    { ...frames[1], cwd: "relative" },
    { ...frames[1], cwd: "/nul\0path" },
    { ...frames[2], cwd: "/tmp" },
    { ...frames[1], task: " " },
    { ...frames[1], task: "\ud800" },
    { ...frames[1], task: "a\udc00" },
    { ...frames[1], expectedTurnId: null },
    { ...frames[2], expectedTurnId: undefined },
    { ...frames[2], expectedTurnId: "bad id" },
    { ...frames[2], expectedturnid: null },
    { ...frames[2], task: "exact\ntext" },
    { ...frames[2], taskSha256: "a".repeat(63) },
    { ...frames[2], taskSha256: "A".repeat(64) },
    { ...frames[2], taskSha256: "g".repeat(64) },
    { ...frames[2], taskUtf8Bytes: 0 },
    { ...frames[2], taskUtf8Bytes: -1 },
    { ...frames[2], taskUtf8Bytes: 1.5 },
    { ...frames[2], taskUtf8Bytes: "10" },
    { ...frames[2], taskUtf8Bytes: 2_147_483_648 },
  ]) {
    assert.deepEqual(parseGalateaDurableFrame(JSON.stringify(invalid)), { ok: false, code: "INVALID_FRAME" });
  }
  assert.deepEqual(
    parseGalateaDurableFrame(JSON.stringify(frames[2]).replace(
      '"dispatchId":"dispatch-1"', '"dispatchId":"dispatch-1","dispatchId":"dispatch-2"',
    )),
    { ok: false, code: "INVALID_FRAME" },
  );
});

test("Inspect accepts original task commitments beyond the current Start cap", () => {
  for (const taskUtf8Bytes of [6, 2_147_483_647]) {
    const frame = {
      v: 6, type: "inspect-dispatch", requestId: "r", dispatchId: "d", threadId: "t",
      taskSha256: "a".repeat(64), taskUtf8Bytes, expectedTurnId: null,
    };
    assert.deepEqual(parseGalateaDurableFrame(JSON.stringify(frame), 1), { ok: true, frame });
  }
});

test("durable protocol preserves exact task and enforces UTF-8 bounds", () => {
  const source = { v: 6, type: "start-turn", cwd: "/workspace", requestId: "r", dispatchId: "d", threadId: "t", task: "你好" };
  assert.deepEqual(parseGalateaDurableFrame(JSON.stringify(source), 5), { ok: false, code: "FRAME_TOO_LARGE" });
  const parsed = parseGalateaDurableFrame(JSON.stringify(source), 6);
  assert.equal(parsed.ok, true);
});

test("durable output encoder requires inspection source", () => {
  assert.equal(
    encodeGalateaDurableOutputFrame({
      v: 6,
      type: "dispatch-inspected",
      requestId: "r",
      dispatchId: "d",
      threadId: "t",
      outcome: "not-found",
      source: "persistent",
    }),
    '{"v":6,"type":"dispatch-inspected","requestId":"r","dispatchId":"d","threadId":"t","outcome":"not-found","source":"persistent"}\n',
  );
});
