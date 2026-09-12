import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import vm from "node:vm";
import test from "node:test";

const source = await readFile(new URL(
  "../../prototypes/Galatea/wwwroot/assets/galatea.js", import.meta.url,
), "utf8");
const production = await import(
  `data:text/javascript;base64,${Buffer.from(source).toString("base64")}`,
);
const flush = () => new Promise((resolve) => setImmediate(resolve));
const turnId = "0123456789abcdef0123456789abcdef";
const idle = { status: "idle", turnId: null, connectionId: null,
  restartRequired: false, recoveryHead: null };
const running = { ...idle, status: "running", turnId, connectionId: "codex" };
const waiting = { state: "waiting", connectionId: "codex",
  nextActivationAtUnixTimeMilliseconds: Date.now() + 600000,
  lastActivationAtUnixTimeMilliseconds: null, code: null, admissionFailure: null };
const recent = (text) => ({ turns: [{ userText: "world",
  assistant: { text, reasoningText: null } }], rewindLatestToken: null,
  contextHeader: { observation: "", action: "" }, recapGridReadiness: null });
const response = (value) => new Response(JSON.stringify(value), {
  headers: { "content-type": "application/json" },
});
function deferred() {
  let resolve;
  const promise = new Promise((done) => { resolve = done; });
  return { promise, resolve };
}
function timers() {
  const pending = new Map();
  let nextId = 0;
  return {
    pending,
    set(callback, delay) {
      const id = ++nextId;
      pending.set(id, { callback, delay });
      return id;
    },
    clear(id) { pending.delete(id); },
    async tick(delay) {
      const found = [...pending].find(([, value]) => value.delay === delay);
      assert.ok(found, `expected a ${delay}ms timer`);
      pending.delete(found[0]);
      found[1].callback();
      await flush();
    },
  };
}

test("agent decoder enforces exact fields and state matrix", () => {
  assert.equal(production.requireAgentStatus(waiting), waiting);
  const paused = { ...waiting, state: "autonomy-paused",
    nextActivationAtUnixTimeMilliseconds: null, code: "AUTONOMOUS_TURN_FAILED" };
  assert.equal(production.requireAgentStatus(paused), paused);
  for (const state of ["disabled", "starting", "maintenance", "stopping",
    "blocked", "running"]) {
    const status = { ...waiting, state,
      connectionId: state === "disabled" ? null : "codex",
      nextActivationAtUnixTimeMilliseconds: null,
      code: state === "blocked" ? "RECOVERY_REQUIRED" : null };
    assert.equal(production.requireAgentStatus(status), status);
  }
  for (const invalid of [
    { ...waiting, extra: true }, { ...waiting, state: "future" },
    { ...waiting, nextActivationAtUnixTimeMilliseconds: null },
    { ...waiting, nextActivationAtUnixTimeMilliseconds: 1.5 },
    { ...waiting, lastActivationAtUnixTimeMilliseconds: -1 },
    { ...waiting, code: "ERR" }, { ...waiting, connectionId: "" },
    { ...paused, code: null }, { ...paused, code: "OTHER" },
    { ...paused, state: "running" },
    { ...waiting, state: "disabled" },
    { ...waiting, state: "blocked", nextActivationAtUnixTimeMilliseconds: null },
  ]) assert.throws(() => production.requireAgentStatus(invalid));
  for (const state of ["disabled", "starting", "maintenance", "stopping"]) {
    assert.throws(() => production.requireAgentStatus({
      ...waiting, state, nextActivationAtUnixTimeMilliseconds: null,
      lastActivationAtUnixTimeMilliseconds: 1,
    }));
  }
});

const admissionBlocked = { ...waiting, state: "blocked",
  nextActivationAtUnixTimeMilliseconds: null, code: "AUTOMATIC_ADMISSION_FAILED",
  admissionFailure: { code: "character-memory-extraction-unavailable",
    error: "Note 提取未成功完成。 提取错误：CompletionOutputInvalid。" } };

test("admission failure detail is restricted to its blocked state and rendered as text", () => {
  assert.equal(production.requireAgentStatus(admissionBlocked), admissionBlocked);
  assert.match(production.formatAgentStatus(admissionBlocked, 0).stateText,
    /Note 提取未成功完成.*CompletionOutputInvalid/);
  assert.throws(() => production.requireAgentStatus({ ...waiting,
    admissionFailure: admissionBlocked.admissionFailure }));
  assert.throws(() => production.requireAgentStatus({ ...admissionBlocked,
    code: "AUTOMATIC_REPLY_FAILED" }));
  assert.throws(() => production.requireAgentStatus({ ...admissionBlocked,
    admissionFailure: { code: "bad", error: "" } }));
});

test("GET follower finds running and between-poll completed turns without duplicate SSE", async () => {
  const clock = timers();
  const reads = [];
  const views = [];
  const attaches = [];
  let current = running;
  let busy = false;
  const follower = production.createAgentStatusFollower({
    fetchImpl: async (url, options) => {
      reads.push({ url, options });
      return response(url.endsWith("/status") ? waiting :
        url.endsWith("/current") ? current : recent("completed"));
    },
    publishStatus: () => {}, publishCurrent: () => {},
    publishRecent: (value) => views.push(value),
    attachTurn: (id) => { busy = true; attaches.push(id); return new Promise(() => {}); },
    getRevision: () => 0, isBusy: () => busy,
    setTimeoutFn: clock.set, clearTimeoutFn: clock.clear,
  });
  follower.start();
  await clock.tick(0);
  assert.deepEqual(attaches, [turnId]);
  await clock.tick(5000);
  await clock.tick(5000);
  assert.deepEqual(attaches, [turnId], "SSE owns the UI until terminal");
  busy = false;
  current = idle;
  await clock.tick(5000);
  assert.equal(views[0].turns[0].assistant.text, "completed");
  await clock.tick(5000);
  assert.equal(views.length, 2, "idle reads cover turns completed between polls");
  assert.ok(reads.every(({ options }) => options.method === "GET"));
  assert.ok(reads.every(({ url }) => !url.includes("ready-turn")));
  follower.stop();
  assert.equal(clock.pending.size, 0);
});

test("a failed current or recent read preserves the successful agent status", async () => {
  for (const failedPath of ["/current", "/recent-turns"]) {
    const clock = timers();
    const statuses = [];
    const follower = production.createAgentStatusFollower({
      fetchImpl: async (url) => {
        if (url.endsWith(failedPath)) return new Response("", { status: 409 });
        return response(url.endsWith("/status") ? waiting : idle);
      },
      publishStatus: (value) => statuses.push(value),
      publishCurrent: () => {}, publishRecent: () => assert.fail("read failed"),
      attachTurn: () => assert.fail("idle must not attach"),
      getRevision: () => 0, isBusy: () => false,
      setTimeoutFn: clock.set, clearTimeoutFn: clock.clear,
    });
    follower.start();
    await clock.tick(0);
    assert.deepEqual(statuses, [waiting]);
    assert.equal(clock.pending.size, 1, "failure keeps polling");
    follower.stop();
  }
});

test("GET follower fences manual mutations and stale page lifecycle responses", async () => {
  const clock = timers();
  const pending = [];
  const statuses = [];
  const views = [];
  let revision = 0;
  let calls = 0;
  const follower = production.createAgentStatusFollower({
    fetchImpl: async (url) => {
      calls += 1;
      if (url.endsWith("/status")) return response(waiting);
      if (url.endsWith("/current")) return response(idle);
      const result = deferred(); pending.push(result); return result.promise;
    },
    publishStatus: (value) => statuses.push(value),
    publishCurrent: () => {}, publishRecent: (value) => views.push(value),
    attachTurn: () => assert.fail("idle must not attach"),
    getRevision: () => revision, isBusy: () => false,
    setTimeoutFn: clock.set, clearTimeoutFn: clock.clear,
  });
  follower.start(); await clock.tick(0);
  assert.equal(calls, 3);
  follower.start();
  assert.equal(clock.pending.size, 0, "refresh never overlaps in-flight GETs");
  revision += 1; // manual send or rewind, followed by a newer SSE result
  pending.shift().resolve(response(recent("stale before manual")));
  await flush();
  assert.equal(views.length, 0);
  await clock.tick(0);
  follower.stop(); follower.start();
  pending.shift().resolve(response(recent("stale hidden page")));
  await flush();
  assert.equal(views.length, 0);
  await clock.tick(0);
  pending.shift().resolve(response(recent("current")));
  await flush();
  assert.equal(views[0].turns[0].assistant.text, "current");
  follower.stop();
});

test("status errors remain read-only and polling recovers", async () => {
  const clock = timers();
  const statuses = [];
  const failures = [response({ malformed: true }),
    new Response("unavailable", { status: 503 }),
    new Response("unauthorized", { status: 401 })];
  const follower = production.createAgentStatusFollower({
    fetchImpl: async (url, options) => {
      assert.equal(options.method, "GET");
      if (failures.length > 0) return failures.shift();
      return response(url.endsWith("/status") ? waiting :
        url.endsWith("/current") ? idle : recent("ok"));
    },
    publishStatus: (value) => statuses.push(value),
    publishCurrent: () => {}, publishRecent: () => {}, attachTurn: () => {},
    getRevision: () => 0, isBusy: () => false,
    setTimeoutFn: clock.set, clearTimeoutFn: clock.clear,
  });
  follower.start(); await clock.tick(0);
  assert.deepEqual(statuses, [null]);
  await clock.tick(5000); await clock.tick(5000);
  assert.deepEqual(statuses, [null, null, null]);
  await clock.tick(5000);
  assert.equal(statuses[3].state, "waiting");
  follower.stop();
});

test("follower waits for turn identity publication without reading stale recent", async () => {
  const clock = timers();
  const attached = [];
  let current = { ...idle, status: "running" };
  const follower = production.createAgentStatusFollower({
    fetchImpl: async (url) => {
      assert.ok(!url.endsWith("/recent-turns"));
      return response(url.endsWith("/status") ? waiting : current);
    },
    publishStatus: () => {}, publishCurrent: () => {},
    publishRecent: () => assert.fail("running must not publish recent"),
    attachTurn: (id) => attached.push(id),
    getRevision: () => 0, isBusy: () => false,
    setTimeoutFn: clock.set, clearTimeoutFn: clock.clear,
  });
  follower.start(); await clock.tick(0);
  assert.deepEqual(attached, []);
  current = running; await clock.tick(5000);
  assert.deepEqual(attached, [turnId]);
  follower.stop();
});

function domHarness({ current = idle, maintenanceMode = false, initialRecent = null } = {}) {
  const clock = timers();
  const nodes = new Map();
  const listeners = new Map();
  function node(id) {
    if (nodes.has(id)) return nodes.get(id);
    const handlers = new Map();
    const classes = new Set();
    const value = {
      textContent: "", innerHTML: "", value: "", disabled: false,
      classList: {
        add: (name) => classes.add(name), remove: (name) => classes.delete(name),
        toggle: (name, set) => set ? classes.add(name) : classes.delete(name),
        contains: (name) => classes.has(name),
      },
      addEventListener: (type, callback) => handlers.set(type, callback),
      querySelectorAll: () => [],
      dispatch: async (type) => handlers.get(type)?.({ preventDefault() {} }),
      focus() {}, setSelectionRange() {}, scrollIntoView() {},
    };
    nodes.set(id, value);
    return value;
  }
  const document = {
    visibilityState: "visible", getElementById: node,
    addEventListener: (type, callback) => listeners.set(type, callback),
  };
  const requests = [];
  const streams = [];
  let recentValue = recent("initial");
  let recentPending = initialRecent;
  let currentValue = current;
  let statusValue = waiting;
  let retryResponse = null;
  let confirms = 0;
  const fetchImpl = async (url, options = {}) => {
    requests.push({ url, options });
    if (url.endsWith("/mailbox/status")) return response({
      state: "no-mail", queuedCount: 0, readyNoticeCount: 0,
      attemptCount: 0, code: null, nextRetryAtUnixTimeMilliseconds: null,
    });
    if (url.endsWith("/agent/status")) return response(statusValue);
    if (url.endsWith("/retry-admission")) {
      return retryResponse === null ? response(statusValue) : await retryResponse;
    }
    if (url.endsWith("/current")) return response(currentValue);
    if (url.endsWith("/recent-turns")) {
      if (recentPending) { const result = recentPending; recentPending = null; return result.promise; }
      return response(recentValue);
    }
    if (url.endsWith("/recap-cadence-progress")) {
      return new Response(JSON.stringify({ code: "unavailable", error: "no fixture" }),
        { status: 503, headers: { "content-type": "application/json" } });
    }
    if (url.endsWith("/events")) {
      const stream = new ReadableStream({ start(controller) { streams.push(controller); } });
      return new Response(stream);
    }
    if (url === "/api/v1/chat/turns" || url.endsWith("/resume")) {
      currentValue = running;
      return response({ turnId });
    }
    throw new Error(`unexpected URL ${url}`);
  };
  const window = {
    galateaBootstrap: {
      userId: "gpt", maintenanceMode, defaultConnectionId: "codex",
      connections: [{ id: "codex", modelId: "gpt" }, { id: "manual", modelId: "other" }],
      streamLimits: { maximumConnectionBytes: 1000000, maximumFrameBytes: 100000 },
    },
    fetch: fetchImpl, setTimeout: clock.set, clearTimeout: clock.clear,
    performance: { now: () => 0 },
    localStorage: { getItem: () => "manual", setItem() {} },
    confirm: () => { confirms += 1; return true; },
    addEventListener: (type, callback) => listeners.set(type, callback),
  };
  const context = vm.createContext({
    window, document, fetch: fetchImpl, TextDecoder, TextEncoder,
    Uint8Array, Intl, Date, URL, console,
  });
  vm.runInContext(source.replaceAll(/^export /gm, ""), context);
  return {
    clock, node, requests, streams, listeners, document,
    setCurrent: (value) => { currentValue = value; },
    setStatus: (value) => { statusValue = value; },
    setRetryResponse: (value) => { retryResponse = value; },
    setRecent: (value) => { recentValue = value; },
    deferRecent() { recentPending = deferred(); return recentPending; },
    confirms: () => confirms,
    finish(text, nextCurrent = idle) {
      currentValue = nextCurrent; recentValue = recent(text);
      const stream = streams.shift();
      stream.enqueue(new TextEncoder().encode(
        `event: done\ndata: ${JSON.stringify({ recent: recentValue })}\n\n`,
      ));
      stream.close();
    },
  };
}
async function poll(harness) {
  // First 0ms timer can belong to the independent mailbox status reader.
  for (let i = 0; i < 3 && [...harness.clock.pending.values()].some((x) => x.delay === 0); i++) {
    await harness.clock.tick(0);
  }
}

test("retry button settles unfinished work without submitting a turn or losing a draft", async () => {
  const h = domHarness();
  h.setStatus(admissionBlocked);
  await flush(); await poll(h);
  const button = h.node("retry-admission");
  assert.equal(button.classList.contains("hidden"), false);
  assert.equal(button.disabled, false);
  assert.match(h.node("autonomy-state").textContent, /CompletionOutputInvalid/);
  h.node("message-input").value = "keep my draft";
  const pending = deferred();
  h.setRetryResponse(pending.promise);
  const clicked = button.dispatch("click");
  await flush();
  assert.equal(button.disabled, true);
  assert.equal(h.node("send-button").disabled, true);
  await button.dispatch("click");
  await h.node("chat-form").dispatch("submit");
  const posts = h.requests.filter((x) => x.options.method === "POST");
  assert.equal(posts.length, 1);
  assert.equal(posts[0].url, "/api/v1/agent/retry-admission");
  assert.equal(posts[0].options.body, "{}");
  pending.resolve(response(waiting));
  h.setStatus(waiting);
  await clicked;
  assert.equal(h.node("message-input").value, "keep my draft");
  assert.equal(button.classList.contains("hidden"), true);
  assert.equal(h.streams.length, 0);
  assert.match(h.node("status-text").textContent, /未完成处理已完成/);
});

test("failed retry preserves error and remains retryable, while maintenance and running disable it", async () => {
  const h = domHarness();
  h.setStatus(admissionBlocked);
  await flush(); await poll(h);
  h.setRetryResponse(Promise.resolve(new Response(JSON.stringify(admissionBlocked.admissionFailure), {
    status: 409, headers: { "content-type": "application/json" },
  })));
  await h.node("retry-admission").dispatch("click");
  assert.match(h.node("status-text").textContent, /Note 提取未成功完成/);
  assert.equal(h.node("retry-admission").classList.contains("hidden"), false);
  assert.equal(h.node("retry-admission").disabled, false);
  h.setCurrent(running);
  h.listeners.get("visibilitychange")(); await poll(h);
  assert.equal(h.node("retry-admission").disabled, true);
  const postCount = h.requests.filter((x) => x.options.method === "POST").length;
  await h.node("retry-admission").dispatch("click");
  assert.equal(h.requests.filter((x) => x.options.method === "POST").length, postCount);
  h.finish("finished"); await flush();

  const maintenance = domHarness({ maintenanceMode: true });
  maintenance.setStatus(admissionBlocked);
  await flush(); await poll(maintenance);
  await maintenance.node("retry-admission").dispatch("click");
  assert.equal(maintenance.requests.some((x) => x.options.method === "POST"), false);
});
test("actual DOM app follows automatic SSE, preserves draft and manual connection, and refreshes completed work", async () => {
  const h = domHarness();
  await flush(); await poll(h);
  h.node("message-input").value = "my unsent draft";
  h.setCurrent(running);
  h.listeners.get("visibilitychange")(); await poll(h);
  assert.equal(h.streams.length, 1);
  assert.equal(h.node("message-input").value, "my unsent draft");
  assert.match(h.node("autonomy-connection").textContent, /codex/);
  h.listeners.get("visibilitychange")(); await poll(h);
  assert.equal(h.requests.filter((x) => x.url.endsWith("/events")).length, 1);
  h.finish("automatic result"); await flush();
  assert.match(h.node("turn-list").innerHTML, /automatic result/);
  assert.equal(h.node("message-input").value, "my unsent draft");
  assert.ok(h.requests.every((x) => (x.options.method ?? "GET") === "GET"));
  h.setRecent(recent("completed between polls"));
  h.listeners.get("visibilitychange")(); await poll(h);
  assert.match(h.node("turn-list").innerHTML, /completed between polls/);
  const submitted = h.node("chat-form").dispatch("submit");
  await flush();
  const send = h.requests.find((x) => x.url === "/api/v1/chat/turns");
  assert.equal(JSON.parse(send.options.body).connectionId, "manual",
    "observing backend connection must not change the manual choice");
  h.finish("manual result"); await submitted;
  assert.equal(h.node("message-input").value, "");
  h.listeners.get("pagehide")();
});
test("actual DOM old recent GET cannot overwrite a manual result", async () => {
  const h = domHarness();
  await flush(); await poll(h);
  const pending = h.deferRecent();
  h.listeners.get("visibilitychange")(); await poll(h);
  h.node("message-input").value = "manual action";
  const submitted = h.node("chat-form").dispatch("submit");
  await flush();
  h.finish("new manual result"); await submitted;
  pending.resolve(response(recent("old before send"))); await flush();
  assert.match(h.node("turn-list").innerHTML, /new manual result/);
  assert.doesNotMatch(h.node("turn-list").innerHTML, /old before send/);
  h.listeners.get("pagehide")();
});
test("actual DOM initialization owns the composer until its delayed recent read settles", async () => {
  const initial = deferred();
  const h = domHarness({ initialRecent: initial });
  await flush();
  assert.equal(h.node("message-input").disabled, true);
  assert.equal(h.node("send-button").disabled, true);
  h.node("message-input").value = "early draft";
  await h.node("chat-form").dispatch("submit");
  assert.equal(h.requests.filter((x) => x.options.method === "POST").length, 0);
  initial.resolve(response(recent("loaded initial")));
  await flush();
  assert.equal(h.node("send-button").disabled, false);
  assert.equal(h.node("message-input").value, "early draft");
  const submitted = h.node("chat-form").dispatch("submit");
  await flush(); h.finish("manual after initial"); await submitted;
  assert.match(h.node("turn-list").innerHTML, /manual after initial/);
  h.listeners.get("pagehide")();
});
test("actual DOM clears obsolete recovery notices when current becomes idle", async () => {
  const recovery = { ...idle, status: "recovery-required", recoveryHead: "head" };
  const h = domHarness({ current: recovery });
  await flush(); await poll(h);
  assert.match(h.node("status-text").textContent, /待恢复/);
  h.setCurrent(idle);
  h.listeners.get("visibilitychange")(); await poll(h);
  assert.equal(h.node("status-text").textContent, "");
  assert.equal(h.node("resume-turn-button").disabled, true);
  h.listeners.get("pagehide")();
});
test("actual DOM terminal handoff does not wait for a later background turn to finish", async () => {
  const h = domHarness({ current: running });
  await flush(); await poll(h);
  const next = { ...running, turnId: "fedcba9876543210fedcba9876543210" };
  h.finish("first turn complete", next);
  await flush();
  assert.equal(h.node("send-button").disabled, false,
    "the old SSE releases ownership when current has moved to another turn");
  h.listeners.get("visibilitychange")(); await poll(h);
  assert.equal(h.streams.length, 1);
  assert.ok(h.requests.some((x) => x.url.includes(next.turnId)));
  h.finish("next turn complete"); await flush();
  assert.match(h.node("turn-list").innerHTML, /next turn complete/);
  h.listeners.get("pagehide")();
});
test("actual DOM recovery is explicit, maintenance is read-only, and hidden pages stop follower", async () => {
  const recovery = { ...idle, status: "recovery-required", recoveryHead: "head",
    restartRequired: true };
  const h = domHarness({ current: recovery });
  await flush(); await poll(h);
  assert.equal(h.confirms(), 0);
  assert.equal(h.requests.filter((x) => x.options.method === "POST").length, 0);
  assert.equal(h.node("resume-turn-button").disabled, false);
  const resumed = h.node("resume-turn-button").dispatch("click");
  await flush();
  assert.equal(h.confirms(), 1);
  const request = h.requests.find((x) => x.url.endsWith("/resume"));
  assert.equal(JSON.parse(request.options.body).expectedHead, "head");
  assert.equal(JSON.parse(request.options.body).restartUncertainCompletion, true);
  h.finish("recovered"); await resumed;
  h.document.visibilityState = "hidden";
  h.listeners.get("visibilitychange")();
  const count = h.requests.filter((x) => x.url.endsWith("/agent/status")).length;
  // Remaining mailbox timers are independent; no agent status read is scheduled.
  for (let i = 0; i < 2; i++) await h.clock.tick(5000);
  assert.equal(h.requests.filter((x) => x.url.endsWith("/agent/status")).length, count);
  h.document.visibilityState = "visible";
  h.listeners.get("visibilitychange")(); await poll(h);
  assert.ok(h.requests.filter((x) => x.url.endsWith("/agent/status")).length > count);
  h.listeners.get("pagehide")();
  const maintenance = domHarness({ current: recovery, maintenanceMode: true });
  await flush(); await poll(maintenance);
  assert.equal(maintenance.node("resume-turn-button").disabled, true);
  await maintenance.node("resume-turn-button").dispatch("click");
  assert.equal(maintenance.requests.filter((x) => x.options.method === "POST").length, 0);
  maintenance.listeners.get("pagehide")();
});
