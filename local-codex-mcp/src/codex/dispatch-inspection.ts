import type { ThreadItem } from "../../schemas/v2/ThreadItem.js";
import type { Turn } from "../../schemas/v2/Turn.js";
import type {
  GalateaDispatchInspection,
  GalateaDispatchInspectionSource,
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

export function isStrictUnicode(value: string): boolean {
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

export function hasExactTaskBody(item: ThreadItem, task: string): boolean {
  if (item.type !== "userMessage" || !Array.isArray(item.content) || item.content.length !== 1) return false;
  const content = item.content[0];
  return content?.type === "text"
    && content.text === task
    && Array.isArray(content.text_elements)
    && content.text_elements.length === 0;
}

export function classifyTurnEvidence(
  threadId: string,
  turn: Turn,
  items: readonly ThreadItem[],
  dispatchId: string,
  task: string,
  maximumFinalUtf8Bytes: number,
  source: GalateaDispatchInspectionSource,
): GalateaDispatchInspection {
  const userMatches = items.filter(
    (item) => item.type === "userMessage" && item.clientId === dispatchId,
  );
  if (userMatches.length !== 1) {
    return {
      kind: "ambiguous",
      threadId,
      source,
      code: userMatches.length === 0 ? "DISPATCH_TURN_MISMATCH" : "DISPATCH_ID_NOT_UNIQUE",
    };
  }
  if (!hasExactTaskBody(userMatches[0]!, task)) {
    return { kind: "ambiguous", threadId, source, code: "DISPATCH_BODY_MISMATCH" };
  }
  switch (turn.status) {
    case "inProgress":
      return { kind: "running", threadId, turnId: turn.id, source };
    case "failed":
      return { kind: "failed", threadId, turnId: turn.id, source, code: "TURN_FAILED" };
    case "interrupted":
      return { kind: "failed", threadId, turnId: turn.id, source, code: "TURN_INTERRUPTED" };
    case "completed":
      return selectFinal(threadId, turn.id, items, maximumFinalUtf8Bytes, source);
    default:
      return { kind: "ambiguous", threadId, source, code: "TURN_STATUS_INVALID" };
  }
}

export function selectFinal(
  threadId: string,
  turnId: string,
  items: readonly ThreadItem[],
  maximumFinalUtf8Bytes: number,
  source: GalateaDispatchInspectionSource,
): GalateaDispatchInspection {
  const messages = items.filter(
    (item): item is Extract<ThreadItem, { type: "agentMessage" }> =>
      item.type === "agentMessage"
      && typeof item.text === "string"
      && item.phase !== "commentary",
  );
  const explicit = messages.filter((item) => item.phase === "final_answer");
  const legacy = messages.filter((item) => item.phase === null);
  const candidates = explicit.length > 0 ? explicit : legacy;
  if (candidates.length > 1) {
    return { kind: "ambiguous", threadId, source, code: "FINAL_AMBIGUOUS" };
  }
  const final = candidates[0]?.text;
  if (final === undefined) {
    return { kind: "failed", threadId, turnId, source, code: "FINAL_MISSING" };
  }
  if (final.trim().length === 0) {
    return { kind: "failed", threadId, turnId, source, code: "FINAL_BLANK" };
  }
  if (!isStrictUnicode(final)) {
    return { kind: "failed", threadId, turnId, source, code: "FINAL_INVALID_UNICODE" };
  }
  if (Buffer.byteLength(final, "utf8") > maximumFinalUtf8Bytes) {
    return { kind: "failed", threadId, turnId, source, code: "FINAL_TOO_LARGE" };
  }
  return { kind: "completed", threadId, turnId, source, final };
}
