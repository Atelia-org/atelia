import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const sourceUrl = new URL(
  "../../prototypes/Galatea/wwwroot/assets/galatea.js",
  import.meta.url,
);
const source = await readFile(sourceUrl, "utf8");
const production = await import(
  `data:text/javascript;base64,${Buffer.from(source).toString("base64")}`
);

const encoder = new TextEncoder();
const validRecent = {
  turns: [{
    userText: "user",
    assistant: { text: "assistant", reasoningText: null },
  }],
  rewindLatestToken: null,
  recapGridReadiness: null,
};

function frame(eventName, payload) {
  return encoder.encode(
    `event: ${eventName}\ndata: ${JSON.stringify(payload)}\n\n`,
  );
}

function concat(...parts) {
  const result = new Uint8Array(
    parts.reduce((length, part) => length + part.byteLength, 0),
  );
  let offset = 0;
  for (const part of parts) {
    result.set(part, offset);
    offset += part.byteLength;
  }
  return result;
}

function limitsFor(bytes, overrides = {}) {
  return {
    maximumConnectionBytes: bytes.byteLength,
    maximumFrameBytes: bytes.byteLength,
    ...overrides,
  };
}

function parse(bytes, limits = limitsFor(bytes), everyByte = false) {
  const parser = new production.GalateaSseV1Parser(limits);
  const events = [];
  if (everyByte) {
    for (let index = 0; index < bytes.byteLength; index += 1) {
      events.push(...parser.push(bytes.subarray(index, index + 1)));
    }
  } else {
    events.push(...parser.push(bytes));
  }
  return { events, terminal: parser.finish() };
}

const allEvents = concat(
  frame("status", { code: "generating" }),
  frame("status", { code: "normalizing-input" }),
  frame("status", {
    code: "input-normalization-finished",
    changed: true,
  }),
  frame("status", { code: "using-tools" }),
  frame("reasoning-delta", { delta: "思考" }),
  frame("text-delta", { delta: "你好" }),
  frame("done", { recent: validRecent }),
);
const split = parse(allEvents, limitsFor(allEvents), true);
assert.deepEqual(
  split.events.map((event) => event.type),
  [
    "status", "status", "status", "status",
    "reasoning-delta", "text-delta", "done",
  ],
);
assert.equal(split.events[4].delta, "思考");
assert.equal(split.events[5].delta, "你好");
assert.equal(split.terminal.type, "done");
assert.deepEqual(split.terminal.recent, validRecent);

const nullDone = frame("done", { recent: null });
assert.deepEqual(parse(nullDone).terminal, { type: "done", recent: null });

const terminalError = frame("error", {
  code: "completion-failed",
  message: "sanitized",
});
assert.deepEqual(parse(terminalError).terminal, {
  type: "error",
  code: "completion-failed",
  message: "sanitized",
});

assert.equal(production.requireRecentTurnsResponse(validRecent), validRecent);
const invalidRecent = { ...validRecent, extra: true };
assert.throws(
  () => production.requireRecentTurnsResponse(invalidRecent),
  /unexpected fields/,
);
assert.throws(
  () => parse(frame("done", { recent: invalidRecent })),
  /unexpected fields/,
);

for (const invalid of [
  encoder.encode("event: done\r\ndata: {\"recent\":null}\r\n\r\n"),
  encoder.encode("event: done\ndata: {\"recent\":null}\ndata: {}\n\n"),
  encoder.encode("id: 1\nevent: done\ndata: {\"recent\":null}\n\n"),
  encoder.encode("event: unknown\ndata: {}\n\n"),
  encoder.encode("event: done\ndata:  {\"recent\":null}\n\n"),
  encoder.encode("event: done\ndata: {\"recent\":null} \n\n"),
  frame("status", { code: "generating", changed: false }),
  frame("status", { code: "input-normalization-finished" }),
  frame("status", {
    code: "input-normalization-finished",
    changed: "yes",
  }),
  frame("text-delta", { delta: "" }),
  frame("reasoning-delta", { delta: null }),
  frame("error", { code: "provider-secret", message: "leak" }),
  frame("error", { code: "internal-failure", message: "" }),
  frame("done", { Recent: null }),
]) {
  assert.throws(
    () => parse(invalid),
    production.GalateaSseProtocolError,
  );
}

const previewOnly = frame("text-delta", { delta: "partial" });
assert.throws(
  () => parse(previewOnly),
  production.GalateaSseEofBeforeTerminalError,
);
const unterminated = encoder.encode(
  "event: done\ndata: {\"recent\":null}",
);
assert.throws(
  () => parse(unterminated),
  production.GalateaSseEofBeforeTerminalError,
);

assert.throws(
  () => parse(concat(nullDone, nullDone)),
  /followed a terminal event/,
);
assert.throws(
  () => parse(concat(nullDone, encoder.encode("x"))),
  /followed a terminal event/,
);

const incompleteUtf8 = new production.GalateaSseV1Parser({
  maximumConnectionBytes: 1,
  maximumFrameBytes: 1,
});
assert.deepEqual(incompleteUtf8.push(Uint8Array.of(0xc3)), []);
assert.throws(
  () => incompleteUtf8.finish(),
  /inside a UTF-8 sequence/,
);
const invalidUtf8 = new production.GalateaSseV1Parser({
  maximumConnectionBytes: 1,
  maximumFrameBytes: 1,
});
assert.throws(
  () => invalidUtf8.push(Uint8Array.of(0xff)),
  /invalid UTF-8/,
);
const predecodeFrameBound = new production.GalateaSseV1Parser({
  maximumConnectionBytes: 2,
  maximumFrameBytes: 1,
});
assert.throws(
  () => predecodeFrameBound.push(Uint8Array.of(0xff, 0xff)),
  /frame byte limit exceeded/,
);

assert.equal(
  parse(nullDone, limitsFor(nullDone)).terminal.type,
  "done",
);
assert.throws(
  () => parse(nullDone, limitsFor(nullDone, {
    maximumConnectionBytes: nullDone.byteLength - 1,
    maximumFrameBytes: nullDone.byteLength - 1,
  })),
  /connection byte limit exceeded/,
);
assert.throws(
  () => parse(nullDone, limitsFor(nullDone, {
    maximumFrameBytes: nullDone.byteLength - 1,
  })),
  /frame byte limit exceeded/,
);

assert.throws(
  () => production.requireStreamLimits({
    maximumConnectionBytes: 8,
    maximumFrameBytes: 9,
  }),
  /frame bound exceeds connection bound/,
);
assert.doesNotMatch(source, /maximumConnectionBytes\s*=\s*\d/);
assert.doesNotMatch(source, /maximumFrameBytes\s*=\s*\d/);
const attachSource = source.slice(
  source.indexOf("async function attachToTurn"),
  source.indexOf('form.addEventListener("submit"'),
);
const protocolBranch = attachSource.indexOf(
  "error instanceof GalateaSseProtocolError",
);
const reconciliationRead = attachSource.indexOf(
  "currentTurn = await loadCurrentTurn()",
);
assert.ok(protocolBranch >= 0 && protocolBranch < reconciliationRead);
assert.match(
  attachSource.slice(protocolBranch, reconciliationRead),
  /已停止自动重连[\s\S]*return;/,
);
assert.match(attachSource, /currentTurn\?\.status === "running"/);
assert.match(attachSource, /reconciliationFailures >= 3/);
assert.match(attachSource, /SSE turn was not found/);
assert.match(
  source,
  /typeof window !== "undefined" && typeof document !== "undefined"/,
);
