import assert from "node:assert/strict";
import test from "node:test";
import { encodeGalateaDurableOutputFrame, parseGalateaDurableFrame } from "../src/galatea/durable-protocol.js";

test("durable protocol accepts only exact V3 request shapes", () => {
  const frames = [
    { v: 3, type: "ensure-binding", requestId: "request-1", bindingOperationId: "binding-1" },
    { v: 3, type: "start-turn", requestId: "request-2", dispatchId: "dispatch-1", threadId: "thread-1", task: "exact\ntext" },
    { v: 3, type: "inspect-dispatch", requestId: "request-3", dispatchId: "dispatch-1", threadId: "thread-1", task: "exact\ntext", expectedTurnId: null },
    { v: 3, type: "inspect-dispatch", requestId: "request-4", dispatchId: "dispatch-1", threadId: "thread-1", task: "exact\ntext", expectedTurnId: "turn-1" },
  ] as const;
  for (const expected of frames) {
    const parsed = parseGalateaDurableFrame(JSON.stringify(expected));
    assert.equal(parsed.ok, true);
    if (parsed.ok) assert.deepEqual(parsed.frame, expected);
  }
  for (const invalid of [
    { ...frames[0], v: 2 },
    { ...frames[0], type: "Ensure-Binding" },
    { ...frames[0], cwd: "/tmp" },
    { ...frames[1], task: " " },
    { ...frames[1], expectedTurnId: null },
    { ...frames[2], expectedTurnId: undefined },
    { ...frames[2], expectedTurnId: "bad id" },
    { ...frames[2], expectedturnid: null },
  ]) {
    assert.deepEqual(parseGalateaDurableFrame(JSON.stringify(invalid)), { ok: false, code: "INVALID_FRAME" });
  }
  assert.deepEqual(
    parseGalateaDurableFrame('{"v":3,"type":"inspect-dispatch","requestId":"r","dispatchId":"d","dispatchId":"e","threadId":"t","task":"x","expectedTurnId":null}'),
    { ok: false, code: "INVALID_FRAME" },
  );
});

test("durable protocol preserves exact task and enforces UTF-8 bounds", () => {
  const source = { v: 3, type: "inspect-dispatch", requestId: "r", dispatchId: "d", threadId: "t", task: "你好", expectedTurnId: null };
  assert.deepEqual(parseGalateaDurableFrame(JSON.stringify(source), 5), { ok: false, code: "FRAME_TOO_LARGE" });
  const parsed = parseGalateaDurableFrame(JSON.stringify(source), 6);
  assert.equal(parsed.ok, true);
});

test("durable output encoder requires inspection source", () => {
  assert.equal(
    encodeGalateaDurableOutputFrame({
      v: 3,
      type: "dispatch-inspected",
      requestId: "r",
      dispatchId: "d",
      threadId: "t",
      outcome: "not-found",
      source: "persistent",
    }),
    '{"v":3,"type":"dispatch-inspected","requestId":"r","dispatchId":"d","threadId":"t","outcome":"not-found","source":"persistent"}\n',
  );
});
