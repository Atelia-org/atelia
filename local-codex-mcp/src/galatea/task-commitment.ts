import { createHash } from "node:crypto";

export interface TaskCommitment {
  taskSha256: string;
  taskUtf8Bytes: number;
}

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

export function isTaskCommitment(value: { taskSha256?: unknown; taskUtf8Bytes?: unknown }): value is TaskCommitment {
  return typeof value.taskSha256 === "string" && /^[0-9a-f]{64}$/.test(value.taskSha256)
    && typeof value.taskUtf8Bytes === "number" && Number.isInteger(value.taskUtf8Bytes)
    && value.taskUtf8Bytes > 0 && value.taskUtf8Bytes <= 2_147_483_647;
}

/** Commits to the complete original task, without normalization or a hash domain. */
export function taskCommitment(task: string): TaskCommitment {
  if (!isStrictUnicode(task)) throw new RangeError("Task contains an unpaired Unicode surrogate.");
  const bytes = Buffer.from(task, "utf8");
  if (bytes.length === 0 || bytes.length > 2_147_483_647) throw new RangeError("Task UTF-8 length is out of range.");
  return { taskSha256: createHash("sha256").update(bytes).digest("hex"), taskUtf8Bytes: bytes.length };
}

export function sameTaskCommitment(left: TaskCommitment, right: TaskCommitment): boolean {
  return left.taskSha256 === right.taskSha256 && left.taskUtf8Bytes === right.taskUtf8Bytes;
}

export function matchesTaskCommitment(task: string, expected: TaskCommitment): boolean {
  return isStrictUnicode(task) && task.length > 0 && sameTaskCommitment(taskCommitment(task), expected);
}
