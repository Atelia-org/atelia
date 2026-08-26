import assert from "node:assert/strict";
import { PassThrough } from "node:stream";
import test from "node:test";
import {
  JsonlFrameWriter,
  parseDispatchFrame,
  readBoundedJsonLines,
  type BoundedJsonLine,
} from "../src/galatea/protocol.js";

async function readAll(input: PassThrough, maximumBytes: number): Promise<BoundedJsonLine[]> {
  const lines: BoundedJsonLine[] = [];
  for await (const line of readBoundedJsonLines(input, maximumBytes)) lines.push(line);
  return lines;
}

test("dispatch parser accepts only the exact V1 shape and preserves task text", () => {
  const task = "请处理：\n```md\n正文\n```";
  const parsed = parseDispatchFrame(JSON.stringify({
    v: 1,
    type: "dispatch",
    requestId: "request-1",
    dispatchId: "turn:1.0",
    threadId: null,
    task,
  }));
  assert.equal(parsed.ok, true);
  if (parsed.ok) {
    assert.equal(parsed.frame.task, task);
    assert.equal(parsed.frame.threadId, undefined);
  }

  for (const invalid of [
    { v: 1, type: "Dispatch", requestId: "r", dispatchId: "d", task: "x" },
    { v: 1, type: "dispatch", RequestId: "r", dispatchId: "d", task: "x" },
    { v: 1, type: "dispatch", requestId: "r", dispatchId: "d", task: "x", cwd: "/tmp" },
    { v: 1, type: "dispatch", requestId: "r", dispatchId: "d", task: "   " },
    { v: 2, type: "dispatch", requestId: "r", dispatchId: "d", task: "x" },
  ]) {
    assert.deepEqual(parseDispatchFrame(JSON.stringify(invalid)), { ok: false, code: "INVALID_FRAME" });
  }
  assert.deepEqual(parseDispatchFrame("{"), { ok: false, code: "INVALID_FRAME" });
  assert.deepEqual(
    parseDispatchFrame(JSON.stringify({ v: 1, type: "dispatch", requestId: "r", dispatchId: "d", task: "你好" }), 5),
    { ok: false, code: "FRAME_TOO_LARGE" },
  );
});

test("bounded JSONL reader rejects oversize and invalid UTF-8 lines then resynchronizes", async () => {
  const input = new PassThrough();
  const reading = readAll(input, 8);
  input.write(Buffer.from("123456789\n"));
  input.write(Buffer.from([0xff, 0x0a]));
  input.end(Buffer.from("ok\r\n"));
  assert.deepEqual(await reading, [
    { ok: false, code: "FRAME_TOO_LARGE" },
    { ok: false, code: "INVALID_UTF8" },
    { ok: true, text: "ok" },
  ]);
});

test("JSONL writer serializes concurrent writes through one bounded writer", async () => {
  const output = new PassThrough();
  let text = "";
  output.setEncoding("utf8");
  output.on("data", (chunk: string) => { text += chunk; });
  const writer = new JsonlFrameWriter(output, 1_000);
  await Promise.all([
    writer.write({ v: 1, type: "ready" }),
    writer.write({ v: 1, type: "failed", stage: "protocol", code: "INVALID_FRAME" }),
  ]);
  await writer.flush();
  assert.deepEqual(text.trimEnd().split("\n").map((line) => JSON.parse(line)), [
    { v: 1, type: "ready" },
    { v: 1, type: "failed", stage: "protocol", code: "INVALID_FRAME" },
  ]);
});
