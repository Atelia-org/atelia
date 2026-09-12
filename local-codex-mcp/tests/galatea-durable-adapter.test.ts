import assert from "node:assert/strict";
import test from "node:test";
import type {
  EnsureGalateaBindingInput,
  GalateaDispatchInspection,
  GalateaStagedBackend,
  InspectGalateaDispatchInput,
  StartGalateaBoundTurnInput,
} from "../src/backend/galatea-staged-backend.js";
import { BridgeError } from "../src/errors.js";
import { GalateaDurableAdapter } from "../src/galatea/durable-adapter.js";
import type {
  GalateaDurableInputFrame,
  GalateaDurableOutputFrame,
} from "../src/galatea/durable-protocol.js";
import { NullLogger } from "../src/logger.js";

class StubBackend implements GalateaStagedBackend {
  startCalls = 0;
  inspection: GalateaDispatchInspection = {
    kind: "not-found",
    threadId: "thread-1",
    source: "persistent",
  };
  inspectError?: Error;
  inspectionInput?: InspectGalateaDispatchInput;
  bindingInput?: EnsureGalateaBindingInput;
  startInput?: StartGalateaBoundTurnInput;
  releaseStart?: Promise<void>;

  async ensureBinding(input: EnsureGalateaBindingInput) {
    this.bindingInput = input;
    return { threadId: "thread-1" };
  }

  async startBoundTurn(input: StartGalateaBoundTurnInput) {
    this.startInput = input;
    this.startCalls += 1;
    await this.releaseStart;
    return { threadId: "thread-1", turnId: "turn-1" };
  }

  async inspectDispatch(input: InspectGalateaDispatchInput) {
    this.inspectionInput = input;
    if (this.inspectError) throw this.inspectError;
    return this.inspection;
  }

  async stop() {}
}

function frame(
  type: "start-turn" | "inspect-dispatch",
  requestId: string,
): GalateaDurableInputFrame {
  return type === "inspect-dispatch" ? {
    v: 4,
    type,
    requestId,
    dispatchId: "dispatch-1",
    threadId: "thread-1",
    task: "exact task",
    expectedTurnId: null,
  } : {
    v: 4,
    type,
    requestId,
    dispatchId: "dispatch-1",
    threadId: "thread-1",
    task: "exact task",
    cwd: "/workspace",
  };
}

function harness(maximumOutputFrameBytes = 10_000) {
  const backend = new StubBackend();
  const frames: GalateaDurableOutputFrame[] = [];
  const adapter = new GalateaDurableAdapter({
    backend,
    logger: new NullLogger(),
    mode: "work",
    localCommandNetwork: false,
    tools: { webSearch: "live", imageGeneration: true, viewImage: true },
    maximumFinalUtf8Bytes: 1_000,
    maximumOutputFrameBytes,
    write: async (output) => { frames.push(output); },
  });
  return { adapter, backend, frames };
}

test("durable adapter emits one short correlated response for each staged operation", async () => {
  const value = harness();
  await value.adapter.handle({
    v: 4,
    type: "ensure-binding",
    cwd: "/workspace",
    requestId: "request-binding",
    bindingOperationId: "binding-1",
  });
  await value.adapter.handle(frame("start-turn", "request-start"));
  await value.adapter.handle(frame("inspect-dispatch", "request-inspect"));
  assert.equal(value.backend.inspectionInput?.expectedTurnId, null);
  assert.equal(value.backend.bindingInput?.cwd, "/workspace");
  assert.equal(value.backend.startInput?.cwd, "/workspace");
  assert.equal("cwd" in value.backend.inspectionInput!, false);
  assert.deepEqual(value.frames, [
    {
      v: 4,
      type: "binding-established",
      requestId: "request-binding",
      bindingOperationId: "binding-1",
      threadId: "thread-1",
    },
    {
      v: 4,
      type: "turn-accepted",
      requestId: "request-start",
      dispatchId: "dispatch-1",
      threadId: "thread-1",
      turnId: "turn-1",
    },
    {
      v: 4,
      type: "dispatch-inspected",
      requestId: "request-inspect",
      dispatchId: "dispatch-1",
      threadId: "thread-1",
      outcome: "not-found",
      source: "persistent",
    },
  ]);
});

test("shared adapter forwards each operation's cwd independently", async () => {
  const value = harness();
  await value.adapter.handle({
    v: 4, type: "ensure-binding", requestId: "binding-a", bindingOperationId: "binding-a", cwd: "/home-a",
  });
  assert.equal(value.backend.bindingInput?.cwd, "/home-a");
  await value.adapter.handle({
    v: 4, type: "start-turn", requestId: "start-b", dispatchId: "dispatch-b", threadId: "thread-1", task: "task b", cwd: "/home-b",
  });
  assert.equal(value.backend.startInput?.cwd, "/home-b");
});

test("durable adapter blocks only a concurrently active duplicate start", async () => {
  const value = harness();
  let release!: () => void;
  value.backend.releaseStart = new Promise<void>((resolve) => { release = resolve; });
  const first = value.adapter.handle(frame("start-turn", "request-first"));
  await Promise.resolve();
  await value.adapter.handle(frame("start-turn", "request-duplicate"));
  release();
  await first;
  assert.equal(value.backend.startCalls, 1);
  assert.equal(value.frames[0]?.type, "failed");
  assert.equal(
    value.frames[0]?.type === "failed" && value.frames[0].code,
    "DISPATCH_ALREADY_ACTIVE",
  );
  assert.equal(value.frames[1]?.type, "turn-accepted");

  await value.adapter.handle(frame("start-turn", "request-later"));
  assert.equal(value.backend.startCalls, 2);
});

test("durable adapter maps inspection outcomes and transport unavailability without inventing terminal state", async () => {
  const value = harness(220);
  value.backend.inspection = {
    kind: "completed",
    threadId: "thread-1",
    turnId: "turn-1",
    final: "x".repeat(500),
    source: "live",
  };
  await value.adapter.handle(frame("inspect-dispatch", "request-large"));
  assert.equal(value.frames[0]?.type, "dispatch-inspected");
  if (value.frames[0]?.type === "dispatch-inspected") {
    assert.equal(value.frames[0].outcome, "failed");
    assert.equal("code" in value.frames[0] && value.frames[0].code, "FINAL_TOO_LARGE");
  }

  value.backend.inspectError = new BridgeError(
    "CODEX_PROTOCOL_ERROR",
    "app-server generation changed during inspection",
  );
  await value.adapter.handle(frame("inspect-dispatch", "request-unavailable"));
  const unavailable = value.frames[1];
  assert.equal(unavailable?.type, "failed");
  assert.equal(
    unavailable?.type === "failed" && unavailable.code,
    "INSPECTION_UNAVAILABLE",
  );
  assert.equal(unavailable !== undefined && Object.hasOwn(unavailable, "source"), false);
});

test("durable adapter preserves Accepted selector and retryable visibility outcome", async () => {
  const value = harness();
  value.backend.inspection = {
    kind: "unavailable",
    threadId: "thread-1",
    turnId: "turn-expected",
    source: "persistent",
    code: "ACCEPTED_TURN_NOT_VISIBLE",
  };
  await value.adapter.handle({
    v: 4,
    type: "inspect-dispatch",
    requestId: "request-known",
    dispatchId: "dispatch-1",
    threadId: "thread-1",
    task: "exact task",
    expectedTurnId: "turn-expected",
  });
  assert.equal(value.backend.inspectionInput?.expectedTurnId, "turn-expected");
  assert.deepEqual(value.frames[0], {
    v: 4,
    type: "dispatch-inspected",
    requestId: "request-known",
    dispatchId: "dispatch-1",
    threadId: "thread-1",
    outcome: "unavailable",
    source: "persistent",
    turnId: "turn-expected",
    code: "ACCEPTED_TURN_NOT_VISIBLE",
  });
});

test("durable adapter rejects a wrong returned Accepted turn identity", async () => {
  const value = harness();
  value.backend.inspection = {
    kind: "running",
    threadId: "thread-1",
    turnId: "wrong-turn",
    source: "live",
  };
  await value.adapter.handle({
    v: 4,
    type: "inspect-dispatch",
    requestId: "request-wrong-turn",
    dispatchId: "dispatch-1",
    threadId: "thread-1",
    task: "exact task",
    expectedTurnId: "turn-expected",
  });
  const result = value.frames[0];
  assert.equal(result?.type, "dispatch-inspected");
  if (result?.type === "dispatch-inspected") {
    assert.equal(result.outcome, "ambiguous");
    if (result.outcome === "ambiguous") assert.equal(result.code, "DISPATCH_TURN_MISMATCH");
  }
});
