import assert from "node:assert/strict";
import test from "node:test";
import { TaskStore } from "../src/codex/task-store.js";

test("turn completion before waiter registration is retained and compacted", async () => {
  const store = new TaskStore(1_000, 100);
  store.beginTurn("thread-1", "turn-1");
  store.handleNotification({
    method: "item/completed",
    params: {
      threadId: "thread-1",
      turnId: "turn-1",
      item: {
        type: "agentMessage",
        id: "item-1",
        phase: "final_answer",
        memoryCitation: null,
        text: JSON.stringify({
          summary: "Done.",
          findings: ["Evidence"],
          changed_files: ["a.ts"],
          validation: ["3 tests passed"],
          warnings: [],
        }),
      },
    },
  });
  store.handleNotification({
    method: "turn/completed",
    params: {
      threadId: "thread-1",
      turn: {
        id: "turn-1",
        items: [],
        itemsView: "full",
        status: "completed",
        error: null,
        startedAt: 1,
        completedAt: 2,
        durationMs: 1,
      },
    },
  });

  const snapshot = await store.waitForTurn("thread-1", "turn-1", 10);
  assert.equal(snapshot.status, "completed");
  assert.match(snapshot.result ?? "", /Done/);
  assert.equal(snapshot.finalTruncated, false);
  assert.deepEqual(snapshot.changedFiles, ["a.ts"]);
  assert.deepEqual(snapshot.validation, ["3 tests passed"]);

  // Simulate turn/start response processing after terminal notifications.
  store.beginTurn("thread-1", "turn-1");
  assert.equal(store.snapshot("thread-1").status, "completed");
});

test("final truncation is explicit and is reset by the next complete final", () => {
  const store = new TaskStore(5, 100);
  store.beginTurn("thread-1", "turn-1");
  store.handleNotification({
    method: "item/completed",
    params: {
      threadId: "thread-1",
      turnId: "turn-1",
      item: {
        type: "agentMessage",
        id: "item-1",
        phase: "final_answer",
        memoryCitation: null,
        text: "123456",
      },
    },
  });
  assert.equal(store.snapshot("thread-1").finalTruncated, true);

  store.beginTurn("thread-1", "turn-2");
  store.handleNotification({
    method: "item/completed",
    params: {
      threadId: "thread-1",
      turnId: "turn-2",
      item: {
        type: "agentMessage",
        id: "item-2",
        phase: null,
        memoryCitation: null,
        text: "1234",
      },
    },
  });
  assert.equal(store.snapshot("thread-1").finalTruncated, false);
});

test("bounded wait returns running and does not interrupt", async () => {
  const store = new TaskStore(1_000, 10);
  store.beginTurn("thread-1", "turn-1");
  store.handleNotification({
    method: "item/agentMessage/delta",
    params: { threadId: "thread-1", turnId: "turn-1", delta: "1234567890abcdef" },
  });
  const snapshot = await store.waitForTurn("thread-1", "turn-1", 5);
  assert.equal(snapshot.status, "running");
  assert.ok((snapshot.progress?.length ?? 0) <= 10);
});

test("generated image path is retained as a changed file", () => {
  const store = new TaskStore(1_000, 100);
  store.beginTurn("thread-1", "turn-1");
  store.handleNotification({
    method: "item/completed",
    params: {
      threadId: "thread-1",
      turnId: "turn-1",
      item: {
        type: "imageGeneration",
        id: "image-1",
        status: "completed",
        revisedPrompt: null,
        result: "generated",
        savedPath: "/workspace/generated.png",
      },
    },
  });

  assert.deepEqual(store.snapshot("thread-1").changedFiles, [
    "/workspace/generated.png",
  ]);
});

test("late item notifications from an older turn cannot overwrite the current final", () => {
  const store = new TaskStore(1_000, 100);
  store.beginTurn("thread-1", "turn-2");
  store.handleNotification({
    method: "item/completed",
    params: {
      threadId: "thread-1",
      turnId: "turn-1",
      item: {
        type: "agentMessage",
        id: "old-item",
        phase: "final_answer",
        memoryCitation: null,
        text: "stale",
      },
    },
  });
  assert.equal(store.snapshot("thread-1").final, undefined);
});
