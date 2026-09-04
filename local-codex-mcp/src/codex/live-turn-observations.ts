import { createHash } from "node:crypto";
import type { ThreadItem } from "../../schemas/v2/ThreadItem.js";
import type { Turn } from "../../schemas/v2/Turn.js";
import type { GalateaDispatchInspection } from "../backend/galatea-staged-backend.js";
import { isStrictUnicode } from "./dispatch-inspection.js";

export interface LiveTurnObservationOptions {
  maximumObservations: number;
  maximumFinalUtf8Bytes: number;
}

export interface LiveStartExpectation {
  readonly threadId: string;
  readonly dispatchId: string;
  readonly taskDigest: string;
}

type FinalValue =
  | { kind: "text"; text: string }
  | { kind: "blank" | "invalid-unicode" | "too-large" };

type FinalSlot =
  | { kind: "none" }
  | { kind: "one"; itemId: string; fingerprint: string; value: FinalValue }
  | { kind: "ambiguous" };

interface TerminalEvidence {
  status: Exclude<Turn["status"], "inProgress">;
  fingerprint: string;
}

interface PendingCompletedEvidence {
  baseFingerprint: string;
}

interface Observation {
  threadId: string;
  turnId: string;
  dispatchId: string;
  taskDigest: string;
  userItemId: string;
  userFingerprint: string;
  explicit: FinalSlot;
  legacy: FinalSlot;
  terminal?: TerminalEvidence;
  pendingCompleted?: PendingCompletedEvidence;
  conflict: boolean;
}

const maximumObservedIdentifierUtf8Bytes = 1_024;

function isBoundedIdentifier(value: string): boolean {
  return value.length > 0 && Buffer.byteLength(value, "utf8") <= maximumObservedIdentifierUtf8Bytes;
}

function utf16Digest(domain: string, value: string): string {
  const bytes = Buffer.allocUnsafe(value.length * 2);
  for (let index = 0; index < value.length; index += 1) {
    bytes.writeUInt16LE(value.charCodeAt(index), index * 2);
  }
  return createHash("sha256").update(domain, "utf8").update(bytes).digest("hex");
}

function taskDigest(task: string): string {
  return utf16Digest("atelia.galatea.live-turn-task.v1\0", task);
}

function valueFingerprint(value: unknown): string {
  return createHash("sha256")
    .update("atelia.galatea.live-turn-evidence.v1\0", "utf8")
    .update(JSON.stringify(value) ?? "undefined", "utf8")
    .digest("hex");
}

function key(threadId: string, turnId: string): string {
  return `${threadId}\0${turnId}`;
}

function exactUser(item: ThreadItem): { id: string; dispatchId: string; task: string } | undefined {
  if (item.type !== "userMessage" || typeof item.clientId !== "string"
      || !isBoundedIdentifier(item.id) || !isBoundedIdentifier(item.clientId)
      || !Array.isArray(item.content) || item.content.length !== 1) return undefined;
  const content = item.content[0];
  if (content?.type !== "text" || !Array.isArray(content.text_elements)
      || content.text_elements.length !== 0) return undefined;
  return { id: item.id, dispatchId: item.clientId, task: content.text };
}

function initialUser(turn: Turn): { id: string; dispatchId: string; task: string } | undefined {
  const users = turn.items.filter((item) => item.type === "userMessage");
  return users.length === 1 ? exactUser(users[0]!) : undefined;
}

function finalValue(text: string, maximumBytes: number): FinalValue {
  if (text.trim().length === 0) return { kind: "blank" };
  if (!isStrictUnicode(text)) return { kind: "invalid-unicode" };
  if (Buffer.byteLength(text, "utf8") > maximumBytes) return { kind: "too-large" };
  return { kind: "text", text };
}

function slotFingerprint(slot: FinalSlot): unknown {
  return slot.kind === "one"
    ? { kind: slot.kind, itemId: slot.itemId, fingerprint: slot.fingerprint }
    : { kind: slot.kind };
}

function finalEvidenceAvailable(observation: Observation): boolean {
  return observation.explicit.kind !== "none" || observation.legacy.kind !== "none";
}

function terminalBaseFingerprint(turn: Turn): string {
  return valueFingerprint({ status: turn.status, error: turn.error });
}

function terminalFingerprint(baseFingerprint: string, observation: Observation): string {
  return valueFingerprint({
    baseFingerprint,
    explicit: slotFingerprint(observation.explicit),
    legacy: slotFingerprint(observation.legacy),
  });
}

export class LiveTurnObservations {
  private readonly observations = new Map<string, Observation>();
  private readonly currentTurnByThread = new Map<string, string>();
  private readonly pendingStarts = new Map<string, LiveStartExpectation>();

  constructor(private readonly options: LiveTurnObservationOptions) {
    if (!Number.isInteger(options.maximumObservations) || options.maximumObservations < 1
        || !Number.isInteger(options.maximumFinalUtf8Bytes) || options.maximumFinalUtf8Bytes < 1) {
      throw new RangeError("Live turn observation bounds must be positive integers.");
    }
  }

  clear(): void {
    this.observations.clear();
    this.currentTurnByThread.clear();
    this.pendingStarts.clear();
  }

  beginStart(threadId: string, dispatchId: string, task: string): LiveStartExpectation | undefined {
    if (!isBoundedIdentifier(threadId) || !isBoundedIdentifier(dispatchId)) return undefined;
    if (!this.pendingStarts.has(threadId)
        && this.pendingStarts.size >= this.options.maximumObservations) return undefined;
    const expectation = { threadId, dispatchId, taskDigest: taskDigest(task) };
    this.pendingStarts.set(threadId, expectation);
    return expectation;
  }

  endStart(expectation: LiveStartExpectation | undefined): void {
    if (expectation && this.pendingStarts.get(expectation.threadId) === expectation) {
      this.pendingStarts.delete(expectation.threadId);
    }
  }

  observeStartResponse(
    threadId: string,
    turn: Turn,
    expectation?: LiveStartExpectation,
  ): boolean {
    if (expectation) {
      const user = initialUser(turn);
      if (this.pendingStarts.get(threadId) !== expectation || !user
          || user.dispatchId !== expectation.dispatchId
          || taskDigest(user.task) !== expectation.taskDigest) return false;
    }
    this.observeStarted(threadId, turn, true);
    return true;
  }

  observeTurnStarted(threadId: string, turn: Turn): void {
    this.observeStarted(threadId, turn, false);
  }

  observeTurnCompleted(threadId: string, turn: Turn): void {
    let observation = this.currentObservation(threadId, turn.id);
    if (!observation) {
      const expected = this.pendingStarts.get(threadId);
      const user = initialUser(turn);
      if (!expected || !user || user.dispatchId !== expected.dispatchId
          || taskDigest(user.task) !== expected.taskDigest) return;
      this.observeStarted(threadId, turn, false);
      observation = this.currentObservation(threadId, turn.id);
    }
    if (!observation || turn.status === "inProgress") return;
    for (const item of turn.items) this.observeItem(threadId, turn.id, item);
    const baseFingerprint = terminalBaseFingerprint(turn);
    if (observation.terminal) {
      const incoming = terminalFingerprint(baseFingerprint, observation);
      if (observation.terminal.status !== turn.status
          || observation.terminal.fingerprint !== incoming) observation.conflict = true;
      return;
    }
    if (observation.pendingCompleted
        && (turn.status !== "completed"
          || observation.pendingCompleted.baseFingerprint !== baseFingerprint)) {
      observation.conflict = true;
      return;
    }
    if (turn.status === "completed" && turn.itemsView !== "full"
        && !finalEvidenceAvailable(observation)) {
      observation.pendingCompleted = {
        baseFingerprint,
      };
      return;
    }
    observation.pendingCompleted = undefined;
    observation.terminal = { status: turn.status, fingerprint: terminalFingerprint(baseFingerprint, observation) };
  }

  observeItem(threadId: string, turnId: string, item: ThreadItem): void {
    const observation = this.currentObservation(threadId, turnId);
    if (!observation || !item || typeof item.id !== "string" || !item.id) return;
    const user = exactUser(item);
    if (item.type === "userMessage") {
      if (!user || user.id !== observation.userItemId
          || user.dispatchId !== observation.dispatchId
          || taskDigest(user.task) !== observation.taskDigest
          || valueFingerprint(user) !== observation.userFingerprint) {
        observation.conflict = true;
      }
      return;
    }
    if (item.type !== "agentMessage" || item.phase === "commentary") return;
    if (!isBoundedIdentifier(item.id)) {
      this.discardObservation(observation);
      return;
    }
    if (item.id === observation.userItemId) {
      observation.conflict = true;
      return;
    }
    const slot = item.phase === "final_answer" ? "explicit" : "legacy";
    const other = slot === "explicit" ? observation.legacy : observation.explicit;
    if (other.kind === "one" && other.itemId === item.id) {
      observation.conflict = true;
      return;
    }
    const before = slotFingerprint(observation[slot]);
    const fingerprint = valueFingerprint({ phase: item.phase, text: item.text });
    const current = observation[slot];
    if (current.kind === "none") {
      observation[slot] = {
        kind: "one",
        itemId: item.id,
        fingerprint,
        value: finalValue(item.text, this.options.maximumFinalUtf8Bytes),
      };
    } else if (current.kind === "one") {
      if (current.itemId === item.id) {
        if (current.fingerprint !== fingerprint) observation.conflict = true;
      } else {
        observation[slot] = { kind: "ambiguous" };
      }
    }
    const changed = valueFingerprint(before) !== valueFingerprint(slotFingerprint(observation[slot]));
    if (observation.terminal && changed) {
      observation.conflict = true;
    } else if (observation.pendingCompleted && finalEvidenceAvailable(observation)) {
      observation.terminal = {
        status: "completed",
        fingerprint: terminalFingerprint(observation.pendingCompleted.baseFingerprint, observation),
      };
      observation.pendingCompleted = undefined;
    }
  }

  inspect(threadId: string, turnId: string, dispatchId: string, task: string): GalateaDispatchInspection | undefined {
    const observation = this.currentObservation(threadId, turnId);
    if (!observation || observation.dispatchId !== dispatchId
        || observation.taskDigest !== taskDigest(task)) return undefined;
    if (observation.conflict) {
      return { kind: "ambiguous", threadId, source: "live", code: "LIVE_OBSERVATION_CONFLICT" };
    }
    if (observation.pendingCompleted) return undefined;
    if (!observation.terminal) return { kind: "running", threadId, turnId, source: "live" };
    if (observation.terminal.status === "failed") {
      return { kind: "failed", threadId, turnId, source: "live", code: "TURN_FAILED" };
    }
    if (observation.terminal.status === "interrupted") {
      return { kind: "failed", threadId, turnId, source: "live", code: "TURN_INTERRUPTED" };
    }
    return this.selectFinal(observation);
  }

  isAwaitingTerminalEvidence(
    threadId: string,
    turnId: string,
    dispatchId: string,
    task: string,
  ): boolean {
    const observation = this.currentObservation(threadId, turnId);
    return observation !== undefined
      && observation.dispatchId === dispatchId
      && observation.taskDigest === taskDigest(task)
      && observation.pendingCompleted !== undefined
      && !observation.conflict;
  }

  private observeStarted(threadId: string, turn: Turn, trustedResponse: boolean): void {
    if (!isBoundedIdentifier(threadId) || !turn || typeof turn.id !== "string"
        || !isBoundedIdentifier(turn.id)) return;
    const currentTurnId = this.currentTurnByThread.get(threadId);
    if (currentTurnId !== turn.id) {
      const user = initialUser(turn);
      const expected = this.pendingStarts.get(threadId);
      const matchesPending = user !== undefined && expected !== undefined
        && user.dispatchId === expected.dispatchId && taskDigest(user.task) === expected.taskDigest;
      if (!trustedResponse && !matchesPending) return;
      if (!user) return;
      if (currentTurnId) {
        this.observations.delete(key(threadId, currentTurnId));
        this.currentTurnByThread.delete(threadId);
      }
      if (this.observations.size >= this.options.maximumObservations) return;
      const observation: Observation = {
        threadId,
        turnId: turn.id,
        dispatchId: user.dispatchId,
        taskDigest: taskDigest(user.task),
        userItemId: user.id,
        userFingerprint: valueFingerprint(user),
        explicit: { kind: "none" },
        legacy: { kind: "none" },
        conflict: false,
      };
      this.observations.set(key(threadId, turn.id), observation);
      this.currentTurnByThread.set(threadId, turn.id);
    }
    for (const item of turn.items) this.observeItem(threadId, turn.id, item);
  }

  private currentObservation(threadId: string, turnId: string): Observation | undefined {
    return this.currentTurnByThread.get(threadId) === turnId
      ? this.observations.get(key(threadId, turnId))
      : undefined;
  }

  private discardObservation(observation: Observation): void {
    this.observations.delete(key(observation.threadId, observation.turnId));
    if (this.currentTurnByThread.get(observation.threadId) === observation.turnId) {
      this.currentTurnByThread.delete(observation.threadId);
    }
  }

  private selectFinal(observation: Observation): GalateaDispatchInspection {
    const selected = observation.explicit.kind !== "none" ? observation.explicit : observation.legacy;
    if (selected.kind === "ambiguous") {
      return { kind: "ambiguous", threadId: observation.threadId, source: "live", code: "FINAL_AMBIGUOUS" };
    }
    if (selected.kind === "none") {
      return { kind: "failed", threadId: observation.threadId, turnId: observation.turnId, source: "live", code: "FINAL_MISSING" };
    }
    switch (selected.value.kind) {
      case "blank":
        return { kind: "failed", threadId: observation.threadId, turnId: observation.turnId, source: "live", code: "FINAL_BLANK" };
      case "invalid-unicode":
        return { kind: "failed", threadId: observation.threadId, turnId: observation.turnId, source: "live", code: "FINAL_INVALID_UNICODE" };
      case "too-large":
        return { kind: "failed", threadId: observation.threadId, turnId: observation.turnId, source: "live", code: "FINAL_TOO_LARGE" };
      case "text":
        return { kind: "completed", threadId: observation.threadId, turnId: observation.turnId, source: "live", final: selected.value.text };
    }
  }
}
