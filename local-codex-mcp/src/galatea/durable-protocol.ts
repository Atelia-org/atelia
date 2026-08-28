import type {
  GalateaDispatchAmbiguityCode,
  GalateaDispatchFailureCode,
} from "../backend/galatea-staged-backend.js";
import { DEFAULT_MAX_TASK_BYTES } from "./limits.js";

export const GALATEA_DURABLE_SIDECAR_PROTOCOL_VERSION = 2 as const;
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
}

export type GalateaDurableInputFrame =
  | GalateaEnsureBindingFrame
  | GalateaStartTurnFrame
  | GalateaInspectDispatchFrame;

export type GalateaDurableFailureFrame =
  | {
      v: 2;
      type: "failed";
      stage: "protocol";
      requestId?: string;
      code: string;
    }
  | {
      v: 2;
      type: "failed";
      stage: "ensure-binding";
      requestId: string;
      bindingOperationId: string;
      code: string;
    }
  | {
      v: 2;
      type: "failed";
      stage: "start-turn" | "inspect-dispatch" | "shutdown";
      requestId: string;
      dispatchId: string;
      threadId: string;
      code: string;
    };

export type GalateaDurableOutputFrame =
  | { v: 2; type: "ready" }
  | {
      v: 2;
      type: "binding-established";
      requestId: string;
      bindingOperationId: string;
      threadId: string;
    }
  | {
      v: 2;
      type: "turn-accepted";
      requestId: string;
      dispatchId: string;
      threadId: string;
      turnId: string;
    }
  | {
      v: 2;
      type: "dispatch-inspected";
      requestId: string;
      dispatchId: string;
      threadId: string;
      outcome: "not-found";
    }
  | {
      v: 2;
      type: "dispatch-inspected";
      requestId: string;
      dispatchId: string;
      threadId: string;
      outcome: "running";
      turnId: string;
    }
  | {
      v: 2;
      type: "dispatch-inspected";
      requestId: string;
      dispatchId: string;
      threadId: string;
      outcome: "completed";
      turnId: string;
      final: string;
    }
  | {
      v: 2;
      type: "dispatch-inspected";
      requestId: string;
      dispatchId: string;
      threadId: string;
      outcome: "failed";
      turnId: string;
      code: GalateaDispatchFailureCode;
    }
  | {
      v: 2;
      type: "dispatch-inspected";
      requestId: string;
      dispatchId: string;
      threadId: string;
      outcome: "ambiguous";
      code: GalateaDispatchAmbiguityCode;
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

function parseTaskFrame(
  value: Record<string, unknown>,
  type: "start-turn" | "inspect-dispatch",
  maximumTaskBytes: number,
): GalateaDurableParseResult {
  if (!hasExactKeys(
    value,
    ["v", "type", "requestId", "dispatchId", "threadId", "task"],
  ) || value.v !== GALATEA_DURABLE_SIDECAR_PROTOCOL_VERSION
    || value.type !== type
    || !isIdentifier(value.requestId)
    || !isIdentifier(value.dispatchId)
    || !isIdentifier(value.threadId)
    || typeof value.task !== "string"
    || value.task.trim().length === 0) {
    return { ok: false, code: "INVALID_FRAME" };
  }
  if (byteLength(value.task) > maximumTaskBytes) {
    return { ok: false, code: "FRAME_TOO_LARGE" };
  }
  return {
    ok: true,
    frame: {
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
