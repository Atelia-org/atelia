import assert from "node:assert/strict";
import { PassThrough } from "node:stream";
import test from "node:test";
import {
  JsonlFrameWriter,
  readBoundedJsonLines,
  type BoundedJsonLine,
} from "../src/galatea/jsonl.js";

async function readAll(input: PassThrough, maximumBytes: number): Promise<BoundedJsonLine[]> {
  const lines: BoundedJsonLine[] = [];
  for await (const line of readBoundedJsonLines(input, maximumBytes)) lines.push(line);
  return lines;
}

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
  type Frame = { sequence: number; text: string };
  const output = new PassThrough();
  let text = "";
  output.setEncoding("utf8");
  output.on("data", (chunk: string) => { text += chunk; });
  const writer = new JsonlFrameWriter<Frame>(
    output,
    1_000,
    1_000,
    (frame) => `${JSON.stringify(frame)}\n`,
  );
  await Promise.all([
    writer.write({ sequence: 1, text: "first" }),
    writer.write({ sequence: 2, text: "second" }),
  ]);
  await writer.flush();
  assert.deepEqual(text.trimEnd().split("\n").map((line) => JSON.parse(line)), [
    { sequence: 1, text: "first" },
    { sequence: 2, text: "second" },
  ]);
});
