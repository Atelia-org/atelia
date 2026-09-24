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

const exactMailboxStatus = {
  state: "accepted-history-unavailable",
  queuedCount: 2,
  readyNoticeCount: 1,
  attemptCount: 4,
  code: "ACCEPTED_TURN_NOT_VISIBLE",
  nextRetryAtUnixTimeMilliseconds: 1_788_000_000_000,
};
assert.equal(
  production.requireMailboxStatus(exactMailboxStatus),
  exactMailboxStatus,
);
assert.throws(
  () => production.requireMailboxStatus({ ...exactMailboxStatus, extra: 1 }),
  /unexpected fields/,
);
assert.throws(
  () => production.requireMailboxStatus({
    ...exactMailboxStatus,
    state: "future-state",
  }),
  /state is unknown/,
);
assert.throws(
  () => production.requireMailboxStatus({
    ...exactMailboxStatus,
    queuedCount: -1,
  }),
  /nonnegative safe integer/,
);
assert.throws(
  () => production.requireMailboxStatus({
    ...exactMailboxStatus,
    state: "backoff",
    code: "TEMPORARY",
    nextRetryAtUnixTimeMilliseconds: null,
  }),
  /must include next retry time/,
);
assert.throws(
  () => production.requireMailboxStatus({
    ...exactMailboxStatus,
    state: "quarantined",
    code: "ROUTE_BAD",
  }),
  /must not imply retry/,
);

const mailboxFetchCalls = [];
for (const [response, expectedCode] of [
  [new Response("", { status: 401 }), "HTTP_401"],
  [new Response("", { status: 503 }), "HTTP_503"],
  [new Response("<html></html>", { headers: { "content-type": "text/html" } }), "INVALID_CONTENT_TYPE"],
  [new Response("{", { headers: { "content-type": "application/json" } }), "INVALID_JSON"],
  [new Response("{}", { headers: { "content-type": "application/json" } }), "INVALID_RESPONSE"],
]) {
  await assert.rejects(production.fetchMailboxStatus(async () => response, "/api/v1/characters/gpt"),
    (error) => production.statusReadFailureCode(error) === expectedCode);
}
assert.equal(production.statusReadFailureCode(new TypeError("Failed to fetch")), "STATUS_READ_FAILED");

const fetchedMailboxStatus = await production.fetchMailboxStatus(
  async (...args) => {
    mailboxFetchCalls.push(args);
    return {
      ok: true,
      status: 200,
      headers: new Map([["content-type", "application/json; charset=utf-8"]]),
      text: async () => JSON.stringify(exactMailboxStatus),
    };
  },
  "/api/v1/characters/gpt",
);
assert.deepEqual(fetchedMailboxStatus, exactMailboxStatus);
assert.deepEqual(mailboxFetchCalls, [[
  "/api/v1/characters/gpt/mailbox/status",
  { method: "GET", credentials: "same-origin", cache: "no-store" },
]]);

assert.match(
  production.formatMailboxStatus({
    ...exactMailboxStatus,
    state: "unavailable",
    attemptCount: 0,
    code: "MAINTENANCE_READ_ONLY",
    nextRetryAtUnixTimeMilliseconds: null,
  }),
  /维护模式，后台处理已暂停.*排队：2.*待续接回信：1/,
);
assert.doesNotMatch(
  production.formatMailboxStatus({
    ...exactMailboxStatus,
    state: "quarantined",
    code: "ROUTE_BAD",
    nextRetryAtUnixTimeMilliseconds: null,
  }),
  /重试/,
);
assert.match(
  production.formatMailboxStatus({
    ...exactMailboxStatus,
    state: "quarantined",
    code: "ROUTE_BAD",
    nextRetryAtUnixTimeMilliseconds: null,
  }),
  /此前尝试 4 次/,
);

const scheduledMailboxTimers = [];
const clearedMailboxTimers = [];
const publishedMailboxStatuses = [];
const pendingMailboxReads = [];
let nextMailboxTimerId = 1;
const mailboxPoller = production.createMailboxStatusPoller({
  readStatus: () => new Promise((resolve, reject) => {
    pendingMailboxReads.push({ resolve, reject });
  }),
  publishStatus: (status) => publishedMailboxStatuses.push(status),
  setTimeoutFn: (callback, delay) => {
    const timer = { id: nextMailboxTimerId++, callback, delay };
    scheduledMailboxTimers.push(timer);
    return timer.id;
  },
  clearTimeoutFn: (id) => clearedMailboxTimers.push(id),
});
mailboxPoller.start();
assert.equal(scheduledMailboxTimers.length, 1);
assert.equal(scheduledMailboxTimers[0].delay, 0);
scheduledMailboxTimers.shift().callback();
assert.equal(pendingMailboxReads.length, 1);
mailboxPoller.start();
assert.equal(scheduledMailboxTimers.length, 0, "in-flight read is single");
pendingMailboxReads.shift().resolve(exactMailboxStatus);
await new Promise((resolve) => setImmediate(resolve));
assert.deepEqual(publishedMailboxStatuses, [exactMailboxStatus]);
assert.equal(scheduledMailboxTimers.length, 1);
assert.equal(scheduledMailboxTimers[0].delay, 5000);
scheduledMailboxTimers.shift().callback();
assert.equal(pendingMailboxReads.length, 1);
pendingMailboxReads.shift().reject(new Error("temporary"));
await new Promise((resolve) => setImmediate(resolve));
assert.deepEqual(publishedMailboxStatuses, [exactMailboxStatus, null]);
assert.equal(scheduledMailboxTimers.length, 1, "failure keeps polling");
assert.equal(scheduledMailboxTimers[0].delay, 5000);
mailboxPoller.stop();
assert.deepEqual(clearedMailboxTimers, [scheduledMailboxTimers[0].id]);

const waitingPulseStatus = {
  state: "waiting",
  defaultConnectionId: "codex",
  runtimeConnectionOverrideId: null,
  effectiveConnectionId: "codex",
  nextActivationAtUnixTimeMilliseconds: 11_000,
  lastActivationAtUnixTimeMilliseconds: null,
  code: null,
  admissionFailure: null,
};
const pausedPulseStatus = {
  state: "autonomy-paused",
  defaultConnectionId: "codex",
  runtimeConnectionOverrideId: null,
  effectiveConnectionId: "codex",
  nextActivationAtUnixTimeMilliseconds: null,
  lastActivationAtUnixTimeMilliseconds: 7_000,
  code: "AUTONOMOUS_TURN_FAILED",
  admissionFailure: null,
};
const countdownProjection = production.createAutonomyCountdownProjection(
  waitingPulseStatus,
  1_000,
  50,
);
assert.deepEqual(countdownProjection, {
  remainingAtReceiptMilliseconds: 10_000,
  receiptMonotonicMilliseconds: 50,
});
assert.equal(
  production.projectAutonomyCountdown(countdownProjection, 5_050),
  5_000,
);
assert.equal(
  production.projectAutonomyCountdown(countdownProjection, 12_000),
  0,
  "local projection clamps after the server deadline",
);
assert.equal(
  production.projectAutonomyCountdown(countdownProjection, 25),
  10_000,
  "a monotonic clock regression cannot make the countdown early",
);
assert.equal(
  production.createAutonomyCountdownProjection(pausedPulseStatus, 1_000, 50),
  null,
);
assert.equal(production.formatAutonomyCountdown(65_000), "约 1 分 5 秒");
assert.equal(production.formatAutonomyCountdown(0), "约 0 秒");
assert.deepEqual(
  production.formatAgentStatus(waitingPulseStatus, 5_000),
  {
    stateText: "自主活动：等待空闲倒计时",
    countdownText: "下次自主激活：约 5 秒",
    lastActivationText: "上次自主激活：尚无",
    paused: false,
  },
);
assert.deepEqual(
  production.formatAgentStatus(
    pausedPulseStatus,
    null,
    (milliseconds) => `time-${milliseconds}`,
  ),
  {
    stateText: "自主活动：已暂停（AUTONOMOUS_TURN_FAILED）；仍会继续收取 Codex 回信",
    countdownText: "",
    lastActivationText: "上次自主激活：time-7000",
    paused: true,
  },
);
const valid = {
  turns: [{
    userText: "user",
    endReason: null,
    assistant: { text: "assistant", reasoningText: null },
  }],
  rewindLatestToken: null,
  contextHeader: { observation: "recap <world>", action: "recap self" },
  recapGridReadiness: null,
};

assert.equal(production.requireRecentTurnsResponse(valid), valid);
assert.throws(
  () => production.requireRecentTurnsResponse({ ...valid, extra: true }),
  /unexpected fields/,
);
assert.throws(
  () => production.requireRecentTurnsResponse({
    ...valid,
    turns: [{ userText: "user", assistant: { text: "assistant" } }],
  }),
  /unexpected fields/,
);
assert.throws(
  () => production.requireRecentTurnsResponse({
    ...valid,
    contextHeader: { observation: "recap" },
  }),
  /unexpected fields/,
);
assert.throws(
  () => production.requireRecentTurnsResponse({
    ...valid,
    recapGridReadiness: {
      freshness: "exact",
      state: "future-state",
      observedRawHead: null,
      authority: null,
      metrics: null,
      orderedMissing: null,
      code: null,
      detail: null,
      reserveBootstrap: null,
    },
  }),
  /state is unknown/,
);
assert.throws(
  () => production.requireRecentTurnsResponse({
    ...valid,
    recapCadenceProgress: null,
  }),
  /unexpected fields/,
);

const exactCadenceProgress = {
  freshness: "exact",
  state: "awaiting-recent-reserve",
  observedRawHead: "0000000000000001:0000000000000002",
  cadenceBaseline: "0000000000000001:0000000000000001",
  recentHistoryPlanningUnitCount: 3,
  recentHistoryLoad: "9007199254740993",
  recapIntervalHistoryLoad: "60000",
  minimumRecentHistoryLoad: "24000",
  buildThresholdHistoryLoad: "9007199254741993",
  remainingHistoryLoad: "1000",
  historyLoadEstimatorId: "atelia.history-load.o200k-base.history-unit-v1",
  code: null,
  detail: null,
};
assert.equal(
  production.requireRecapCadenceProgressSnapshot(exactCadenceProgress),
  exactCadenceProgress,
);
for (const invalidDecimal of ["01", "-1", "+1", "1.0", " 1", 1]) {
  assert.throws(
    () => production.requireRecapCadenceProgressSnapshot({
      ...exactCadenceProgress,
      recentHistoryLoad: invalidDecimal,
    }),
    /canonical nonnegative decimal string|must be a string/,
  );
}
assert.throws(
  () => production.requireRecapCadenceProgressSnapshot({
    ...exactCadenceProgress,
    recentHistoryPlanningUnitCount: Number.MAX_SAFE_INTEGER + 1,
  }),
  /nonnegative safe integer/,
);
assert.throws(
  () => production.requireRecapCadenceProgressSnapshot({
    ...exactCadenceProgress,
    state: "future-state",
  }),
  /state is unknown/,
);
assert.throws(
  () => production.requireRecapCadenceProgressSnapshot({
    ...exactCadenceProgress,
    extra: null,
  }),
  /unexpected fields/,
);
assert.equal(
  production.formatHistoryLoadDecimal("9007199254740993"),
  "9,007,199,254,740,993",
);
assert.equal(
  production.recapCadenceProgressRatio(
    "9007199254740993",
    "18014398509481986",
  ),
  0.5,
);
assert.equal(
  production.recapCadenceProgressRatio("2", "1"),
  1,
);
const matchingReadiness = {
  freshness: "exact",
  state: "raw-only",
  observedRawHead: exactCadenceProgress.observedRawHead,
  authority: null,
  metrics: null,
  orderedMissing: null,
  code: null,
  detail: null,
  reserveBootstrap: null,
};
assert.equal(
  production.alignRecapCadenceProgressWithReadiness(
    exactCadenceProgress,
    matchingReadiness,
  ),
  exactCadenceProgress,
);
assert.deepEqual(
  production.alignRecapCadenceProgressWithReadiness(
    exactCadenceProgress,
    { ...matchingReadiness, observedRawHead: "different-head" },
  ),
  {
    ...exactCadenceProgress,
    freshness: "stale",
    state: "stale",
    code: "browser-head-mismatch",
    detail: "Cadence progress and RecapGrid readiness observed different raw heads.",
  },
);
const staleCadenceProgress =
  production.markRecapCadenceProgressSnapshotStale(
    exactCadenceProgress,
    "turn-accepted",
  );
assert.notEqual(staleCadenceProgress, exactCadenceProgress);
assert.equal(exactCadenceProgress.freshness, "exact");
assert.equal(staleCadenceProgress.freshness, "stale");
assert.equal(staleCadenceProgress.state, exactCadenceProgress.state);
assert.equal(
  staleCadenceProgress.recentHistoryLoad,
  exactCadenceProgress.recentHistoryLoad,
);
assert.equal(staleCadenceProgress.code, "turn-accepted");
assert.match(staleCadenceProgress.detail, /上一稳定边界/);
assert.throws(
  () => production.markRecapCadenceProgressSnapshotStale(
    exactCadenceProgress,
    " ",
  ),
  /must not be blank/,
);

const renderedContext = production.renderContextHeader(
  valid.contextHeader,
  "stale",
);
assert.ok(
  renderedContext.indexOf("Context · Action (Assistant)")
    < renderedContext.indexOf("Context · Observation (User)"),
);
assert.match(renderedContext, /recap &lt;world&gt;/);
assert.match(renderedContext, /上一稳定边界/);

const popSource = {
  ...valid,
  rewindLatestToken: "0000000000000001:0000000000000002",
};
const provisional = production.capturePopProvisional(popSource);
assert.deepEqual(provisional, {
  submittedToken: popSource.rewindLatestToken,
  poppedUserText: "user",
});
assert.equal(
  production.reconcilePopProvisional(provisional, popSource),
  null,
);
assert.equal(
  production.reconcilePopProvisional(
    provisional,
    { ...popSource, rewindLatestToken: null },
  ),
  "user",
);
let simulatedFetchCalls = 0;
async function simulateRejectedReceiptBody() {
  const staged = production.stagePopProvisional(popSource);
  simulatedFetchCalls += 1;
  try {
    await Promise.reject(new Error("mid-body reset"));
  } catch {
    return staged;
  }
}
const retained = await simulateRejectedReceiptBody();
assert.equal(simulatedFetchCalls, 1);
assert.equal(retained.rewindLatestToken, null);
assert.equal(retained.pendingPoppedDraftText, "user");
assert.equal(retained.inputValue, "user");

const runningCurrent = {
  status: "running",
  turnId: "0123456789abcdef0123456789abcdef",
  connectionId: "test",
  recoveryHead: null,
};
assert.equal(
  production.canContinueWithoutInitialRecent(
    runningCurrent,
    "recent-view-busy",
  ),
  true,
);
assert.equal(
  production.canContinueWithoutInitialRecent(
    { ...runningCurrent, status: "idle", turnId: null, connectionId: null },
    "recent-view-busy",
  ),
  false,
);

const idleCurrent = {
  ...runningCurrent,
  status: "idle",
  turnId: null,
  connectionId: null,
};
const currentSequence = [idleCurrent, runningCurrent];
let currentReads = 0;
let recentReads = 0;
const initialState = await production.loadInitialSessionState(
  async () => currentSequence[currentReads++],
  async () => {
    recentReads += 1;
    const error = new Error("recent busy");
    error.code = "recent-view-busy";
    throw error;
  },
);
assert.equal(initialState, runningCurrent);
assert.equal(currentReads, 2);
assert.equal(recentReads, 1);

assert.equal(production.shouldClearDraftForTurnOrigin("manual"), true);
for (const origin of [
  "delegate-reply", "heartbeat-activation", "observed", "recovery",
]) {
  assert.equal(production.shouldClearDraftForTurnOrigin(origin), false);
}
assert.throws(
  () => production.shouldClearDraftForTurnOrigin("unknown"),
  /turn origin is unknown/,
);
const popFunction = source.slice(
  source.indexOf("async function popLatestTurn"),
  source.indexOf("async function attachToTurn"),
);
assert.equal((popFunction.match(/method: "POST"/g) ?? []).length, 1);
assert.ok(
  popFunction.indexOf("state.rewindLatestToken = stagedPop.rewindLatestToken;")
    < popFunction.indexOf("await readJsonResponse(response, requirePopReceipt)"),
);
assert.match(
  popFunction,
  /await reconcileAmbiguousPop\(\s+provisional,\s+composerBeforePop,/,
);

const initializeFunction = source.slice(
  source.indexOf("async function initializeApp"),
  source.indexOf('resumeTurnButton?.addEventListener("click"'),
);
assert.match(initializeFunction, /await loadInitialSessionState\(/);
assert.ok(
  initializeFunction.indexOf("await loadInitialSessionState(")
    < initializeFunction.indexOf("void attachToTurn("),
);
assert.match(
  initializeFunction,
  /currentTurn\?\.status === "running"[\s\S]*void attachToTurn\([\s\S]*"observed"/,
);

assert.doesNotMatch(source, /createLoopPulseScheduler|mailLoopInFlight|mail-loop-enabled|mailbox\/ready-turn/);
assert.doesNotMatch(initializeFunction, /method: "POST"|selectConnection\(currentTurn/);
assert.doesNotMatch(source, /setInterval\(/);

const mailboxStatusValidator = source.slice(
  source.indexOf("export function requireMailboxStatus"),
  source.indexOf("export function requireRecentTurnsResponse"),
);
assert.match(mailboxStatusValidator, /requireExactKeys/);
for (const field of [
  "state", "queuedCount", "readyNoticeCount", "attemptCount", "code",
  "nextRetryAtUnixTimeMilliseconds",
]) {
  assert.match(mailboxStatusValidator, new RegExp(`"${field}"`));
}
for (const state of [
  "no-mail", "queued", "active-running", "backoff",
  "accepted-history-unavailable", "ready-reply", "quarantined",
  "unavailable",
]) {
  assert.match(mailboxStatusValidator, new RegExp(`"${state}"`));
}

const mailboxStatusPoller = source.slice(
  source.indexOf("export function createMailboxStatusPoller"),
  source.indexOf("export function formatMailboxStatus"),
);
assert.doesNotMatch(mailboxStatusPoller, /mailLoopEnabled|ready-turn|textarea/);
assert.doesNotMatch(mailboxStatusPoller, /autonomy/i);
assert.match(source, /mailboxStatusPoller\.start\(\);/);
for (const id of [
  "autonomy-status", "autonomy-state", "autonomy-countdown",
  "autonomy-last-activation",
]) {
  assert.match(source, new RegExp(`"${id}"`));
}

const streamEventHandler = source.slice(
  source.indexOf("function handleEvent"),
  source.indexOf("async function popLatestTurn"),
);
assert.match(
  streamEventHandler,
  /shouldClearDraftForTurnOrigin\(state\.activeTurnOrigin\)/,
);
assert.match(streamEventHandler, /input\.value = ""/);
const freshSubmitFunction = source.slice(
  source.indexOf('form.addEventListener("submit"'),
  source.indexOf('undoLastButton?.addEventListener("click"'),
);
const freshOkConfirmed = freshSubmitFunction.indexOf("if (!response.ok)");
const freshMarkedStale = freshSubmitFunction.indexOf(
  'markRecapCadenceProgressStale("turn-accepted")',
);
const freshAcceptedBody = freshSubmitFunction.indexOf(
  "await readJsonResponse(response, requireAcceptedTurn)",
);
const freshRejectedReturn = freshSubmitFunction.lastIndexOf(
  "return;",
  freshMarkedStale,
);
assert.ok(freshOkConfirmed >= 0);
assert.ok(freshRejectedReturn > freshOkConfirmed);
assert.ok(freshMarkedStale > freshOkConfirmed);
assert.ok(freshMarkedStale > freshRejectedReturn);
assert.ok(freshAcceptedBody > freshMarkedStale);
assert.match(
  freshSubmitFunction,
  /payload\.turnId, "正在(?:重新)?生成…", "manual"/,
);

const resumeFunction = source.slice(source.indexOf('resumeTurnButton?.addEventListener("click"'), source.indexOf("  let initialized = false"));
const resumeOkConfirmed = resumeFunction.indexOf("if (response.ok)");
const resumeMarkedStale = resumeFunction.indexOf(
  'markRecapCadenceProgressStale("turn-accepted")',
  resumeOkConfirmed,
);
const resumeAcceptedBody = resumeFunction.indexOf(
  "await readJsonResponse(response, requireAcceptedTurn)",
  resumeOkConfirmed,
);
assert.ok(resumeOkConfirmed >= 0);
assert.ok(resumeMarkedStale > resumeOkConfirmed);
assert.ok(resumeAcceptedBody > resumeMarkedStale);

for (const entryFunction of [freshSubmitFunction, resumeFunction]) {
  const busyBranch = entryFunction.indexOf(
    'if (error.code === "turn-busy")',
  );
  const busyMarkedStale = entryFunction.indexOf(
    'markRecapCadenceProgressStale("active-turn")',
    busyBranch,
  );
  const busyTurnId = entryFunction.indexOf(
    "if (error.turnId)",
    busyBranch,
  );
  assert.ok(busyBranch >= 0);
  assert.ok(busyMarkedStale > busyBranch);
  assert.ok(busyTurnId > busyMarkedStale);
}
assert.doesNotMatch(
  source,
  /error\.code === "turn-busy" && error\.turnId/,
);

assert.match(source, /\$\{apiBase\}\/recent-turns/);
assert.match(source, /\$\{apiBase\}\/recap-cadence-progress/);
assert.match(source, /BigInt\(/);
assert.match(source, /markRecapCadenceProgressStale\("active-turn"\)/);
assert.match(
  source,
  /waitForCurrentTurnTerminal\(normalizedTurnId\)[\s\S]*loadRecapCadenceProgressBestEffort\(\)/,
);
assert.match(
  source,
  /async function loadRecentTurns\(\)[\s\S]*loadRecapCadenceProgressBestEffort\(\)/,
);
assert.doesNotMatch(source, /["'`]\/api\/(?!v1\/)/);
assert.doesNotMatch(source, /pendingPoppedTurn/);
