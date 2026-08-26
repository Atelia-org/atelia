import { TextDecoder } from "node:util";
import type { Readable, Writable } from "node:stream";

export const GALATEA_SIDECAR_PROTOCOL_VERSION = 1 as const;
export const DEFAULT_MAX_INPUT_FRAME_BYTES = 128 * 1024;
export const DEFAULT_MAX_OUTPUT_FRAME_BYTES = 1024 * 1024;
export const DEFAULT_MAX_TASK_BYTES = 100 * 1024;

const identifierPattern = /^[A-Za-z0-9][A-Za-z0-9._:-]*$/;
const maxIdentifierBytes = 200;

export interface GalateaDispatchFrame {
  v: typeof GALATEA_SIDECAR_PROTOCOL_VERSION;
  type: "dispatch";
  requestId: string;
  dispatchId: string;
  threadId?: string;
  task: string;
}

export type GalateaOutputFrame =
  | { v: 1; type: "ready" }
  | {
      v: 1;
      type: "accepted";
      requestId: string;
      dispatchId: string;
      threadId: string;
      turnId: string;
    }
  | {
      v: 1;
      type: "completed";
      dispatchId: string;
      threadId: string;
      turnId: string;
      final: string;
    }
  | {
      v: 1;
      type: "failed";
      requestId?: string;
      dispatchId?: string;
      threadId?: string;
      turnId?: string;
      stage: "protocol" | "start" | "turn" | "shutdown";
      code: string;
    };

export type BoundedJsonLine =
  | { ok: true; text: string }
  | { ok: false; code: "FRAME_TOO_LARGE" | "INVALID_UTF8" };

export type DispatchParseResult =
  | { ok: true; frame: GalateaDispatchFrame }
  | { ok: false; code: "INVALID_FRAME" | "FRAME_TOO_LARGE" };

function byteLength(value: string): number {
  return Buffer.byteLength(value, "utf8");
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isIdentifier(value: unknown): value is string {
  return (
    typeof value === "string" &&
    byteLength(value) <= maxIdentifierBytes &&
    identifierPattern.test(value)
  );
}

export function parseDispatchFrame(text: string, maxTaskBytes = DEFAULT_MAX_TASK_BYTES): DispatchParseResult {
  let value: unknown;
  try {
    value = JSON.parse(text);
  } catch {
    return { ok: false, code: "INVALID_FRAME" };
  }
  if (!isRecord(value)) return { ok: false, code: "INVALID_FRAME" };

  const keys = Object.keys(value).sort();
  const allowed = value.threadId === undefined
    ? ["dispatchId", "requestId", "task", "type", "v"]
    : ["dispatchId", "requestId", "task", "threadId", "type", "v"];
  if (keys.length !== allowed.length || keys.some((key, index) => key !== allowed[index])) {
    return { ok: false, code: "INVALID_FRAME" };
  }
  if (
    value.v !== GALATEA_SIDECAR_PROTOCOL_VERSION ||
    value.type !== "dispatch" ||
    !isIdentifier(value.requestId) ||
    !isIdentifier(value.dispatchId) ||
    typeof value.task !== "string" ||
    value.task.trim().length === 0
  ) {
    return { ok: false, code: "INVALID_FRAME" };
  }
  if (byteLength(value.task) > maxTaskBytes) return { ok: false, code: "FRAME_TOO_LARGE" };

  let threadId: string | undefined;
  if (value.threadId !== undefined && value.threadId !== null) {
    if (!isIdentifier(value.threadId)) return { ok: false, code: "INVALID_FRAME" };
    threadId = value.threadId;
  } else if (value.threadId === null) {
    threadId = undefined;
  }

  return {
    ok: true,
    frame: {
      v: GALATEA_SIDECAR_PROTOCOL_VERSION,
      type: "dispatch",
      requestId: value.requestId,
      dispatchId: value.dispatchId,
      ...(threadId === undefined ? {} : { threadId }),
      task: value.task,
    },
  };
}

export async function* readBoundedJsonLines(
  input: Readable,
  maximumBytes = DEFAULT_MAX_INPUT_FRAME_BYTES,
): AsyncGenerator<BoundedJsonLine> {
  const decoder = new TextDecoder("utf-8", { fatal: true });
  let segments: Buffer[] = [];
  let length = 0;
  let discarding = false;

  const completeLine = (): BoundedJsonLine | undefined => {
    if (discarding) {
      discarding = false;
      segments = [];
      length = 0;
      return undefined;
    }
    let line = Buffer.concat(segments, length);
    segments = [];
    length = 0;
    if (line.at(-1) === 0x0d) line = line.subarray(0, -1);
    try {
      return { ok: true, text: decoder.decode(line) };
    } catch {
      return { ok: false, code: "INVALID_UTF8" };
    }
  };

  for await (const rawChunk of input) {
    const chunk = Buffer.isBuffer(rawChunk) ? rawChunk : Buffer.from(String(rawChunk));
    let start = 0;
    for (let index = 0; index < chunk.length; index += 1) {
      if (chunk[index] !== 0x0a) continue;
      if (!discarding && index > start) {
        const segment = chunk.subarray(start, index);
        segments.push(segment);
        length += segment.length;
      }
      if (!discarding && length > maximumBytes) {
        yield { ok: false, code: "FRAME_TOO_LARGE" };
        discarding = true;
      }
      const line = completeLine();
      if (line) yield line;
      start = index + 1;
    }

    if (start < chunk.length && !discarding) {
      const segment = chunk.subarray(start);
      segments.push(segment);
      length += segment.length;
      if (length > maximumBytes) {
        yield { ok: false, code: "FRAME_TOO_LARGE" };
        discarding = true;
        segments = [];
        length = 0;
      }
    }
  }

  if (discarding) return;
  if (length > 0) {
    const line = completeLine();
    if (line) yield line;
  }
}

export function encodeOutputFrame(frame: GalateaOutputFrame): string {
  return `${JSON.stringify(frame)}\n`;
}

export function encodedOutputFrameBytes(frame: GalateaOutputFrame): number {
  return Buffer.byteLength(encodeOutputFrame(frame), "utf8");
}

export class JsonlFrameWriter {
  private tail = Promise.resolve();

  constructor(
    private readonly output: Writable,
    private readonly maximumBytes = DEFAULT_MAX_OUTPUT_FRAME_BYTES,
  ) {}

  write(frame: GalateaOutputFrame): Promise<void> {
    const encoded = encodeOutputFrame(frame);
    if (Buffer.byteLength(encoded, "utf8") > this.maximumBytes) {
      return Promise.reject(new Error("Sidecar output frame exceeds its configured byte limit."));
    }
    const write = this.tail.then(() => this.writeEncoded(encoded));
    this.tail = write.catch(() => undefined);
    return write;
  }

  flush(): Promise<void> {
    return this.tail;
  }

  private writeEncoded(encoded: string): Promise<void> {
    return new Promise<void>((resolve, reject) => {
      let settled = false;
      const finish = (error?: Error | null) => {
        if (settled) return;
        settled = true;
        this.output.off("error", finish);
        error ? reject(error) : resolve();
      };
      this.output.once("error", finish);
      this.output.write(encoded, (error?: Error | null) => finish(error));
    });
  }
}
