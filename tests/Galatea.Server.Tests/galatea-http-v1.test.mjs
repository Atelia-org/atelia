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

const valid = {
  turns: [{
    userText: "user",
    assistant: { text: "assistant", reasoningText: null },
  }],
  rewindLatestToken: null,
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
  restartRequired: false,
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
  source.indexOf("initializeApp().catch"),
);
assert.ok(
  initializeFunction.indexOf("await loadCurrentTurn()")
    < initializeFunction.indexOf("await loadRecentTurns()"),
);
assert.match(initializeFunction, /canContinueWithoutInitialRecent/);

assert.match(source, /\/api\/v1\/recent-turns/);
assert.doesNotMatch(source, /["'`]\/api\/(?!v1\/)/);
assert.doesNotMatch(source, /pendingPoppedTurn/);
