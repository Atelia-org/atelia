import assert from "node:assert/strict";
import test from "node:test";
import {
  encodeGalateaDurableOutputFrame,
  parseGalateaDurableFrame,
} from "../src/galatea/durable-protocol.js";

test("durable protocol accepts only the three exact V2 request shapes", () => {
  const frames = [
    {
      v: 2,
      type: "ensure-binding",
      requestId: "request-1",
      bindingOperationId: "binding-1",
    },
    {
      v: 2,
      type: "start-turn",
      requestId: "request-2",
      dispatchId: "dispatch-1",
      threadId: "thread-1",
      task: "exact\ntext",
    },
    {
      v: 2,
      type: "inspect-dispatch",
      requestId: "request-3",
      dispatchId: "dispatch-1",
      threadId: "thread-1",
      task: "exact\ntext",
    },
  ];
  for (const expected of frames) {
    const parsed = parseGalateaDurableFrame(JSON.stringify(expected));
    assert.equal(parsed.ok, true);
    if (parsed.ok) assert.deepEqual(parsed.frame, expected);
  }

  for (const invalid of [
    { ...frames[0], v: 1 },
    { ...frames[0], type: "Ensure-Binding" },
    { ...frames[0], cwd: "/tmp" },
    { ...frames[1], task: " " },
    { ...frames[1], threadId: null },
    { ...frames[2], dispatchId: "bad id" },
  ]) {
    assert.deepEqual(
      parseGalateaDurableFrame(JSON.stringify(invalid)),
      { ok: false, code: "INVALID_FRAME" },
    );
  }
});

test("durable protocol preserves exact task and enforces UTF-8 task bounds", () => {
  const source = {
    v: 2,
    type: "inspect-dispatch",
    requestId: "r",
    dispatchId: "d",
    threadId: "t",
    task: "你好",
  };
  assert.deepEqual(
    parseGalateaDurableFrame(JSON.stringify(source), 5),
    { ok: false, code: "FRAME_TOO_LARGE" },
  );
  const parsed = parseGalateaDurableFrame(JSON.stringify(source), 6);
  assert.equal(parsed.ok, true);
  if (parsed.ok && parsed.frame.type === "inspect-dispatch") {
    assert.equal(parsed.frame.task, "你好");
  }
});

test("durable output encoder emits one canonical JSONL frame", () => {
  assert.equal(
    encodeGalateaDurableOutputFrame({
      v: 2,
      type: "dispatch-inspected",
      requestId: "r",
      dispatchId: "d",
      threadId: "t",
      outcome: "not-found",
    }),
    "{\"v\":2,\"type\":\"dispatch-inspected\",\"requestId\":\"r\",\"dispatchId\":\"d\",\"threadId\":\"t\",\"outcome\":\"not-found\"}\n",
  );
});
