import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const source = await readFile(new URL(
  "../../prototypes/Galatea/wwwroot/assets/galatea.js", import.meta.url,
), "utf8");
const production = await import(
  `data:text/javascript;base64,${Buffer.from(source).toString("base64")}`,
);

test("connection preference separates Player and Character, including ambiguous delimiters", () => {
  const pairs = [["same", "same"], ["same", "other"], ["other", "same"],
    ["a:b", "c"], ["a", "b:c"], ["a\"", "b"], ["a", "\"b"]];
  const storage = new Map();
  for (const [index, pair] of pairs.entries()) {
    storage.set(production.connectionPreferenceKey(...pair), `model-${index}`);
  }
  assert.equal(storage.size, pairs.length);
  for (const [index, pair] of pairs.entries()) {
    assert.equal(storage.get(production.connectionPreferenceKey(...pair)), `model-${index}`);
    assert.equal(production.connectionPreferenceKey(...pair),
      `galatea:connection:${JSON.stringify(pair)}`);
  }
  assert.throws(() => production.connectionPreferenceKey(undefined, "target"));
  assert.throws(() => production.connectionPreferenceKey("visitor", ""));
});

test("mailbox fetch requires an explicit target and keeps the same Player out of routing", async () => {
  const requests = [];
  const fetch = async (url) => {
    requests.push(url);
    return new Response(JSON.stringify({ state: "no-mail", queuedCount: 0,
      readyNoticeCount: 0, attemptCount: 0, code: null,
      nextRetryAtUnixTimeMilliseconds: null }), {
      headers: { "content-type": "application/json" },
    });
  };
  await assert.rejects(production.fetchMailboxStatus(fetch));
  await assert.rejects(production.fetchMailboxStatus(fetch, "/api/v1"));
  assert.deepEqual(requests, []);
  await production.fetchMailboxStatus(fetch, production.characterApiBase("alpha"));
  await production.fetchMailboxStatus(fetch, production.characterApiBase("beta"));
  assert.deepEqual(requests, ["/api/v1/characters/alpha/mailbox/status",
    "/api/v1/characters/beta/mailbox/status"]);
});

test("background follower refuses absent character binding before scheduling or fetching", () => {
  assert.throws(() => production.createAgentStatusFollower({}), /explicit character/);
  assert.equal(production.characterApiBase("角色 ?"),
    "/api/v1/characters/%E8%A7%92%E8%89%B2%20%3F");
});
