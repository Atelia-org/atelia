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

assert.match(source, /\/api\/v1\/recent-turns/);
assert.doesNotMatch(source, /["'`]\/api\/(?!v1\/|me["'`])/);
assert.doesNotMatch(source, /pendingPoppedTurn/);
