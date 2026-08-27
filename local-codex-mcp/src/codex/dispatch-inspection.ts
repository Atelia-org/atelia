import type { Thread } from "../../schemas/v2/Thread.js";
import type { ThreadItem } from "../../schemas/v2/ThreadItem.js";
import type { Turn } from "../../schemas/v2/Turn.js";
import type {
  GalateaDispatchInspection,
  GalateaDispatchAmbiguityCode,
} from "../backend/galatea-staged-backend.js";

export interface GalateaDispatchInspectionLimits {
  maximumTurns: number;
  maximumItems: number;
  maximumFinalUtf8Bytes: number;
}

export const DefaultGalateaDispatchInspectionLimits = {
  maximumTurns: 4_096,
  maximumItems: 262_144,
} as const;

function ambiguous(
  threadId: string,
  code: GalateaDispatchAmbiguityCode,
): GalateaDispatchInspection {
  return { kind: "ambiguous", threadId, code };
}

function isStrictUnicode(value: string): boolean {
  for (let index = 0; index < value.length; index += 1) {
    const code = value.charCodeAt(index);
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = value.charCodeAt(index + 1);
      if (!(next >= 0xdc00 && next <= 0xdfff)) return false;
      index += 1;
    } else if (code >= 0xdc00 && code <= 0xdfff) {
      return false;
    }
  }
  return true;
}

function hasExactTaskBody(item: ThreadItem, task: string): boolean {
  if (item.type !== "userMessage" || item.content.length !== 1) {
    return false;
  }
  const content = item.content[0];
  return content?.type === "text"
    && content.text === task
    && Array.isArray(content.text_elements)
    && content.text_elements.length === 0;
}

function selectFinal(
  threadId: string,
  turn: Turn,
  maximumFinalUtf8Bytes: number,
): GalateaDispatchInspection {
  const messages = turn.items.filter(
    (item): item is Extract<ThreadItem, { type: "agentMessage" }> =>
      item.type === "agentMessage" && item.phase !== "commentary",
  );
  const explicit = messages.filter((item) => item.phase === "final_answer");
  const legacy = messages.filter((item) => item.phase === null);
  const candidates = explicit.length > 0 ? explicit : legacy;
  if (candidates.length > 1) {
    return ambiguous(threadId, "FINAL_AMBIGUOUS");
  }
  const final = candidates[0]?.text;
  if (final === undefined) {
    return {
      kind: "failed",
      threadId,
      turnId: turn.id,
      code: "FINAL_MISSING",
    };
  }
  if (final.trim().length === 0) {
    return {
      kind: "failed",
      threadId,
      turnId: turn.id,
      code: "FINAL_BLANK",
    };
  }
  if (!isStrictUnicode(final)) {
    return {
      kind: "failed",
      threadId,
      turnId: turn.id,
      code: "FINAL_INVALID_UNICODE",
    };
  }
  if (Buffer.byteLength(final, "utf8") > maximumFinalUtf8Bytes) {
    return {
      kind: "failed",
      threadId,
      turnId: turn.id,
      code: "FINAL_TOO_LARGE",
    };
  }
  return {
    kind: "completed",
    threadId,
    turnId: turn.id,
    final,
  };
}

export function classifyGalateaDispatch(
  thread: Thread,
  dispatchId: string,
  task: string,
  limits: GalateaDispatchInspectionLimits,
): GalateaDispatchInspection {
  const threadId = typeof thread?.id === "string" ? thread.id : "invalid-thread";
  if (!thread || !Array.isArray(thread.turns)) {
    return ambiguous(threadId, "THREAD_SHAPE_INVALID");
  }
  if (
    !Number.isInteger(limits.maximumTurns)
    || limits.maximumTurns < 1
    || !Number.isInteger(limits.maximumItems)
    || limits.maximumItems < 1
    || !Number.isInteger(limits.maximumFinalUtf8Bytes)
    || limits.maximumFinalUtf8Bytes < 1
  ) {
    throw new RangeError("Galatea dispatch inspection limits must be positive integers.");
  }
  if (thread.turns.length > limits.maximumTurns) {
    return ambiguous(threadId, "INSPECTION_LIMIT_EXCEEDED");
  }

  const turnIds = new Set<string>();
  const itemIds = new Set<string>();
  let itemCount = 0;
  const matches: Array<{ turn: Turn; item: ThreadItem }> = [];
  for (const turn of thread.turns) {
    if (!turn || typeof turn.id !== "string" || turn.id.length === 0) {
      return ambiguous(threadId, "TURN_ID_INVALID");
    }
    if (turnIds.has(turn.id)) {
      return ambiguous(threadId, "TURN_ID_NOT_UNIQUE");
    }
    turnIds.add(turn.id);
    if (turn.itemsView !== "full") {
      return ambiguous(threadId, "TURN_ITEMS_INCOMPLETE");
    }
    if (!Array.isArray(turn.items)) {
      return ambiguous(threadId, "TURN_ITEMS_INVALID");
    }
    itemCount += turn.items.length;
    if (itemCount > limits.maximumItems) {
      return ambiguous(threadId, "INSPECTION_LIMIT_EXCEEDED");
    }
    for (const item of turn.items) {
      if (!item || typeof item.id !== "string" || item.id.length === 0) {
        return ambiguous(threadId, "ITEM_ID_INVALID");
      }
      if (itemIds.has(item.id)) {
        return ambiguous(threadId, "ITEM_ID_NOT_UNIQUE");
      }
      itemIds.add(item.id);
      if (item?.type === "userMessage" && item.clientId === dispatchId) {
        matches.push({ turn, item });
      }
    }
  }

  if (matches.length === 0) {
    return { kind: "not-found", threadId };
  }
  if (matches.length !== 1) {
    return ambiguous(threadId, "DISPATCH_ID_NOT_UNIQUE");
  }
  const match = matches[0]!;
  if (!hasExactTaskBody(match.item, task)) {
    return ambiguous(threadId, "DISPATCH_BODY_MISMATCH");
  }

  switch (match.turn.status) {
    case "inProgress":
      return {
        kind: "running",
        threadId,
        turnId: match.turn.id,
      };
    case "failed":
      return {
        kind: "failed",
        threadId,
        turnId: match.turn.id,
        code: "TURN_FAILED",
      };
    case "interrupted":
      return {
        kind: "failed",
        threadId,
        turnId: match.turn.id,
        code: "TURN_INTERRUPTED",
      };
    case "completed":
      return selectFinal(
        threadId,
        match.turn,
        limits.maximumFinalUtf8Bytes,
      );
    default:
      return ambiguous(threadId, "TURN_STATUS_INVALID");
  }
}
