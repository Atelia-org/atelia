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
    endReason: null,
    assistant: { text: "assistant", reasoningText: null },
  }],
  rewindLatestToken: null,
  contextHeader: { observation: "recap observation", action: "recap action" },
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

const recallError = { code: "memo-recall-failed", message: "记忆召回失败，主模型尚未开始生成。" };
assert.deepEqual(parse(frame("error", recallError)).events,
  [{ type: "error", ...recallError }]);

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
const retryEvents = concat(
  frame("attempt-start", { attempt: 1, segment: 1 }),
  frame("text-delta", { delta: "failed" }),
  frame("attempt-reset", { segment: 1 }),
  frame("retry-wait", { attempt: 1, code: "transport", nextRetryAtUnixTimeMilliseconds: 1000 }),
  frame("attempt-start", { attempt: 2, segment: 1 }),
  frame("text-delta", { delta: "good" }),
  frame("attempt-start", { attempt: 1, segment: 2 }),
  frame("reasoning-delta", { delta: "failed reasoning" }),
  frame("attempt-reset", { segment: 2 }),
  frame("attempt-start", { attempt: 2, segment: 2 }),
  frame("text-delta", { delta: " next" }),
  frame("terminated", { reason: "stopped", recent: null }),
);
let preview = { segment: null, text: "", reasoning: "", textPrefix: "", reasoningPrefix: "" };
const retryParsed = parse(retryEvents, limitsFor(retryEvents), true);
for (const event of retryParsed.events) preview = production.projectAttemptPreview(preview, event);
assert.equal(preview.text, "good next");
assert.equal(preview.reasoning, "");
assert.equal(production.projectAttemptPreview(preview, { type: "attempt-reset", segment: 3 }), preview);
const emptyPreview = { segment: null, text: "", reasoning: "", textPrefix: "", reasoningPrefix: "" };
assert.equal(production.projectAttemptPreview(emptyPreview, { type: "attempt-reset", segment: 1 }), emptyPreview);
assert.equal(retryParsed.terminal.type, "terminated");
assert.throws(() => production.projectAttemptPreview(preview, { type: "attempt-reset", segment: 1 }), /matching segment/);
assert.throws(() => parse(concat(frame("terminated", { reason: "stopped", recent: null }), nullDone)), /followed a terminal/);
for (const [name, payload] of [
  ["attempt-start", { attempt: 0, segment: 1 }],
  ["attempt-start", { attempt: 1, segment: 1, extra: true }],
  ["attempt-reset", { segment: -1 }],
  ["retry-wait", { attempt: 1, code: "", nextRetryAtUnixTimeMilliseconds: 10 }],
  ["retry-wait", { attempt: 1, code: "transport", nextRetryAtUnixTimeMilliseconds: 1.5 }],
  ["terminated", { reason: "provider-secret", recent: null }],
]) assert.throws(() => parse(concat(frame(name, payload), nullDone)), production.GalateaSseProtocolError);
const endedRecent = { ...validRecent, turns: [{ userText: "user", assistant: null, endReason: "stopped" }] };
assert.equal(production.requireRecentTurnsResponse(endedRecent), endedRecent);
assert.throws(() => production.requireRecentTurnsResponse({ ...endedRecent,
  turns: [{ ...endedRecent.turns[0], assistant: { text: "fake", reasoningText: null } }] }), /must not carry/);
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
  /buffered chunk byte limit exceeded/,
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

// Traffic across unlimited retries can exceed the bounded decode/replay size.
const unboundedTraffic = new production.GalateaSseV1Parser({ maximumConnectionBytes: 256, maximumFrameBytes: 256 });
for (let attempt = 1; attempt <= 100; attempt++) {
  unboundedTraffic.push(frame("attempt-start", { attempt, segment: 1 }));
  unboundedTraffic.push(frame("text-delta", { delta: "discard this preview" }));
  unboundedTraffic.push(frame("attempt-reset", { segment: 1 }));
}
unboundedTraffic.push(nullDone);
assert.equal(unboundedTraffic.finish().type, "done");
const bigReadEvents = [];
await production.consumeGalateaSseStream({
  chunks: [concat(retryEvents)],
  async read() { return this.chunks.length ? { value: this.chunks.shift(), done: false } : { done: true }; },
}, { maximumConnectionBytes: 256, maximumFrameBytes: 256 }, (event) => bigReadEvents.push(event));
assert.equal(bigReadEvents.at(-1).type, "terminated");

function readerFrom(...chunks) {
  let index = 0;
  return {
    async read() {
      if (index === chunks.length) {
        return { value: undefined, done: true };
      }
      return { value: chunks[index++], done: false };
    },
  };
}

const successfulEffects = [];
await production.consumeGalateaSseStream(
  readerFrom(nullDone),
  limitsFor(nullDone),
  (event) => successfulEffects.push(event.type),
);
assert.deepEqual(successfulEffects, ["done"]);

for (const invalidTail of [Uint8Array.of(0x78), Uint8Array.of(0xff)]) {
  const effects = [];
  await assert.rejects(
    production.consumeGalateaSseStream(
      readerFrom(nullDone, invalidTail),
      {
        maximumConnectionBytes: nullDone.byteLength + 1,
        maximumFrameBytes: nullDone.byteLength,
      },
      (event) => effects.push(event.type),
    ),
    production.GalateaSseProtocolError,
  );
  assert.deepEqual(effects, []);
}

const idleCurrent = {
  status: "idle",
  turnId: null,
  connectionId: null,
  recoveryHead: null,
};
const runningCurrent = {
  status: "running",
  turnId: "a".repeat(32),
  connectionId: "test",
  recoveryHead: null,
};
const recoveryCurrent = {
  status: "recovery-required",
  turnId: null,
  connectionId: null,
  recoveryHead: "head",
};
assert.equal(production.decideGalateaStreamContinuation({
  outcome: "protocol-invalid",
  expectedTurnId: runningCurrent.turnId,
  currentTurn: null,
  reconciliationFailures: 0,
}), "stop-protocol");
assert.equal(production.decideGalateaStreamContinuation({
  outcome: "transport-ended",
  expectedTurnId: runningCurrent.turnId,
  currentTurn: runningCurrent,
  reconciliationFailures: 0,
}), "reconnect");
assert.equal(production.decideGalateaStreamContinuation({
  outcome: "transport-ended",
  expectedTurnId: runningCurrent.turnId,
  currentTurn: null,
  reconciliationFailures: 2,
}), "retry-confirm");
assert.equal(production.decideGalateaStreamContinuation({
  outcome: "transport-ended",
  expectedTurnId: runningCurrent.turnId,
  currentTurn: null,
  reconciliationFailures: 3,
}), "stop-unconfirmed");
assert.equal(production.decideGalateaStreamContinuation({
  outcome: "transport-ended",
  expectedTurnId: runningCurrent.turnId,
  currentTurn: idleCurrent,
  reconciliationFailures: 0,
}), "refresh-stop");
assert.equal(production.decideGalateaStreamContinuation({
  outcome: "transport-ended",
  expectedTurnId: runningCurrent.turnId,
  currentTurn: recoveryCurrent,
  reconciliationFailures: 0,
}), "refresh-stop");
assert.equal(production.decideGalateaStreamContinuation({
  outcome: "terminal",
  expectedTurnId: runningCurrent.turnId,
  currentTurn: null,
  reconciliationFailures: 0,
}), "refresh-stop");
assert.match(
  source,
  /typeof window !== "undefined" && typeof document !== "undefined"/,
);
