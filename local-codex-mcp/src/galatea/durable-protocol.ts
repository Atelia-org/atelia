import type {
  GalateaDispatchAmbiguityCode,
  GalateaDispatchFailureCode,
} from "../backend/galatea-staged-backend.js";
import { DEFAULT_MAX_TASK_BYTES } from "./limits.js";

export const GALATEA_DURABLE_SIDECAR_PROTOCOL_VERSION = 3 as const;
export { DEFAULT_MAX_TASK_BYTES as DEFAULT_DURABLE_MAX_TASK_BYTES } from "./limits.js";

const identifierPattern = /^[A-Za-z0-9][A-Za-z0-9._:-]*$/;
const maximumIdentifierBytes = 200;

export interface GalateaEnsureBindingFrame {
  v: typeof GALATEA_DURABLE_SIDECAR_PROTOCOL_VERSION;
  type: "ensure-binding";
  requestId: string;
  bindingOperationId: string;
}

export interface GalateaStartTurnFrame {
  v: typeof GALATEA_DURABLE_SIDECAR_PROTOCOL_VERSION;
  type: "start-turn";
  requestId: string;
  dispatchId: string;
  threadId: string;
  task: string;
}

export interface GalateaInspectDispatchFrame {
  v: typeof GALATEA_DURABLE_SIDECAR_PROTOCOL_VERSION;
  type: "inspect-dispatch";
  requestId: string;
  dispatchId: string;
  threadId: string;
  task: string;
  expectedTurnId: string | null;
}

export type GalateaDurableInputFrame =
  | GalateaEnsureBindingFrame
  | GalateaStartTurnFrame
  | GalateaInspectDispatchFrame;

export type GalateaDurableFailureFrame =
  | {
      v: 3;
      type: "failed";
      stage: "protocol";
      requestId?: string;
      code: string;
    }
  | {
      v: 3;
      type: "failed";
      stage: "ensure-binding";
      requestId: string;
      bindingOperationId: string;
      code: string;
    }
  | {
      v: 3;
      type: "failed";
      stage: "start-turn" | "inspect-dispatch" | "shutdown";
      requestId: string;
      dispatchId: string;
      threadId: string;
      code: string;
    };

export type GalateaDurableOutputFrame =
  | { v: 3; type: "ready" }
  | {
      v: 3;
      type: "binding-established";
      requestId: string;
      bindingOperationId: string;
      threadId: string;
    }
  | {
      v: 3;
      type: "turn-accepted";
      requestId: string;
      dispatchId: string;
      threadId: string;
      turnId: string;
    }
  | {
      v: 3;
      type: "dispatch-inspected";
      requestId: string;
      dispatchId: string;
      threadId: string;
      outcome: "not-found";
      source: "persistent";
    }
  | {
      v: 3;
      type: "dispatch-inspected";
      requestId: string;
      dispatchId: string;
      threadId: string;
      outcome: "unavailable";
      source: "persistent";
      turnId: string;
      code: "ACCEPTED_TURN_NOT_VISIBLE";
    }
  | {
      v: 3;
      type: "dispatch-inspected";
      requestId: string;
      dispatchId: string;
      threadId: string;
      outcome: "running";
      turnId: string;
      source: "live" | "persistent";
    }
  | {
      v: 3;
      type: "dispatch-inspected";
      requestId: string;
      dispatchId: string;
      threadId: string;
      outcome: "completed";
      turnId: string;
      final: string;
      source: "live" | "persistent";
    }
  | {
      v: 3;
      type: "dispatch-inspected";
      requestId: string;
      dispatchId: string;
      threadId: string;
      outcome: "failed";
      turnId: string;
      code: GalateaDispatchFailureCode;
      source: "live" | "persistent";
    }
  | {
      v: 3;
      type: "dispatch-inspected";
      requestId: string;
      dispatchId: string;
      threadId: string;
      outcome: "ambiguous";
      code: GalateaDispatchAmbiguityCode;
      source: "live" | "persistent";
    }
  | GalateaDurableFailureFrame;

export type GalateaDurableParseResult =
  | { ok: true; frame: GalateaDurableInputFrame }
  | { ok: false; code: "INVALID_FRAME" | "FRAME_TOO_LARGE" };

function byteLength(value: string): number {
  return Buffer.byteLength(value, "utf8");
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isIdentifier(value: unknown): value is string {
  return typeof value === "string"
    && byteLength(value) <= maximumIdentifierBytes
    && identifierPattern.test(value);
}

function hasExactKeys(value: Record<string, unknown>, keys: readonly string[]): boolean {
  const actual = Object.keys(value).sort();
  const expected = [...keys].sort();
  return actual.length === expected.length
    && actual.every((key, index) => key === expected[index]);
}

function hasDuplicateTopLevelProperty(text: string): boolean {
  let depth = 0;
  let index = 0;
  let expectingKey = false;
  const keys = new Set<string>();
  while (index < text.length) {
    const char = text[index]!;
    if (/\s/.test(char)) { index += 1; continue; }
    if (char === "{") { depth += 1; expectingKey = depth === 1; index += 1; continue; }
    if (char === "}") { depth -= 1; index += 1; continue; }
    if (depth === 1 && char === ",") { expectingKey = true; index += 1; continue; }
    if (char === '"') {
      const start = index;
      index += 1;
      while (index < text.length) {
        if (text[index] === "\\") { index += 2; continue; }
        if (text[index] === '"') { index += 1; break; }
        index += 1;
      }
      if (depth === 1 && expectingKey) {
        let key: string;
        try { key = JSON.parse(text.slice(start, index)) as string; } catch { return false; }
        if (keys.has(key)) return true;
        keys.add(key);
        expectingKey = false;
      }
      continue;
    }
    index += 1;
  }
  return false;
}

function parseTaskFrame(
  value: Record<string, unknown>,
  type: "start-turn" | "inspect-dispatch",
  maximumTaskBytes: number,
): GalateaDurableParseResult {
  const keys = type === "inspect-dispatch"
    ? ["v", "type", "requestId", "dispatchId", "threadId", "task", "expectedTurnId"]
    : ["v", "type", "requestId", "dispatchId", "threadId", "task"];
  if (!hasExactKeys(value, keys)
    || value.v !== GALATEA_DURABLE_SIDECAR_PROTOCOL_VERSION
    || value.type !== type
    || !isIdentifier(value.requestId)
    || !isIdentifier(value.dispatchId)
    || !isIdentifier(value.threadId)
    || typeof value.task !== "string"
    || (type === "inspect-dispatch"
      && value.expectedTurnId !== null
      && !isIdentifier(value.expectedTurnId))
    || value.task.trim().length === 0) {
    return { ok: false, code: "INVALID_FRAME" };
  }
  if (byteLength(value.task) > maximumTaskBytes) {
    return { ok: false, code: "FRAME_TOO_LARGE" };
  }
  return {
    ok: true,
    frame: type === "inspect-dispatch" ? {
      v: GALATEA_DURABLE_SIDECAR_PROTOCOL_VERSION,
      type,
      requestId: value.requestId,
      dispatchId: value.dispatchId,
      threadId: value.threadId,
      task: value.task,
      expectedTurnId: value.expectedTurnId as string | null,
    } : {
      v: GALATEA_DURABLE_SIDECAR_PROTOCOL_VERSION,
      type,
      requestId: value.requestId,
      dispatchId: value.dispatchId,
      threadId: value.threadId,
      task: value.task,
    },
  };
}

export function parseGalateaDurableFrame(
  text: string,
  maximumTaskBytes = DEFAULT_MAX_TASK_BYTES,
): GalateaDurableParseResult {
  if (hasDuplicateTopLevelProperty(text)) {
    return { ok: false, code: "INVALID_FRAME" };
  }
  let value: unknown;
  try {
    value = JSON.parse(text);
  } catch {
    return { ok: false, code: "INVALID_FRAME" };
  }
  if (!isRecord(value)) return { ok: false, code: "INVALID_FRAME" };
  switch (value.type) {
    case "ensure-binding":
      if (!hasExactKeys(
        value,
        ["v", "type", "requestId", "bindingOperationId"],
      ) || value.v !== GALATEA_DURABLE_SIDECAR_PROTOCOL_VERSION
        || !isIdentifier(value.requestId)
        || !isIdentifier(value.bindingOperationId)) {
        return { ok: false, code: "INVALID_FRAME" };
      }
      return {
        ok: true,
        frame: {
          v: GALATEA_DURABLE_SIDECAR_PROTOCOL_VERSION,
          type: "ensure-binding",
          requestId: value.requestId,
          bindingOperationId: value.bindingOperationId,
        },
      };
    case "start-turn":
      return parseTaskFrame(value, "start-turn", maximumTaskBytes);
    case "inspect-dispatch":
      return parseTaskFrame(value, "inspect-dispatch", maximumTaskBytes);
    default:
      return { ok: false, code: "INVALID_FRAME" };
  }
}

export function encodeGalateaDurableOutputFrame(
  frame: GalateaDurableOutputFrame,
): string {
  return `${JSON.stringify(frame)}\n`;
}

export function encodedGalateaDurableOutputFrameBytes(
  frame: GalateaDurableOutputFrame,
): number {
  return Buffer.byteLength(encodeGalateaDurableOutputFrame(frame), "utf8");
}
