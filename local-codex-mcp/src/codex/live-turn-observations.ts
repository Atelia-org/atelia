import { createHash } from "node:crypto";
import type { ThreadItem } from "../../schemas/v2/ThreadItem.js";
import type { Turn } from "../../schemas/v2/Turn.js";
import type { GalateaDispatchInspection } from "../backend/galatea-staged-backend.js";
import { isStrictUnicode, taskCommitment, sameTaskCommitment, type TaskCommitment } from "../galatea/task-commitment.js";

export interface LiveTurnObservationOptions {
  maximumObservations: number;
  maximumFinalUtf8Bytes: number;
}

export interface LiveStartExpectation {
  readonly threadId: string;
  readonly dispatchId: string;
  readonly taskSha256: string;
  readonly taskUtf8Bytes: number;
  readonly tracked: boolean;
  terminalBarriers?: Map<string, UnassociatedTerminalCandidate>;
  terminalBarrierOverflow?: boolean;
}

interface UnassociatedTerminalCandidate {
  status: Exclude<Turn["status"], "inProgress">;
  baseFingerprint: string;
  conflict: boolean;
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
  taskSha256: string;
  taskUtf8Bytes: number;
  userItemId?: string;
  explicit: FinalSlot;
  legacy: FinalSlot;
  terminal?: TerminalEvidence;
  pendingCompleted?: PendingCompletedEvidence;
  conflict: boolean;
}

const maximumObservedIdentifierUtf8Bytes = 1_024;
const maximumUnassociatedTerminalCandidates = 8;

function isBoundedIdentifier(value: string): boolean {
  return value.length > 0 && Buffer.byteLength(value, "utf8") <= maximumObservedIdentifierUtf8Bytes;
}

function valueFingerprint(value: unknown): string {
  return createHash("sha256")
    .update("atelia.galatea.live-turn-evidence.v1\0", "utf8")
    .update(JSON.stringify(value) ?? "undefined", "utf8")
    .digest("hex");
}

function exactUser(item: ThreadItem): { id: string; dispatchId: string } & TaskCommitment | undefined {
  if (item.type !== "userMessage" || typeof item.clientId !== "string"
      || !isBoundedIdentifier(item.id) || !isBoundedIdentifier(item.clientId)
      || !Array.isArray(item.content) || item.content.length !== 1) return undefined;
  const content = item.content[0];
  if (content?.type !== "text" || !Array.isArray(content.text_elements)
      || content.text_elements.length !== 0 || typeof content.text !== "string"
      || !isStrictUnicode(content.text) || content.text.length === 0) return undefined;
  return { id: item.id, dispatchId: item.clientId, ...taskCommitment(content.text) };
}

function initialUser(turn: Turn): { id: string; dispatchId: string } & TaskCommitment | undefined {
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
  private readonly pendingStarts = new Map<string, LiveStartExpectation>();

  constructor(private readonly options: LiveTurnObservationOptions) {
    if (!Number.isInteger(options.maximumObservations) || options.maximumObservations < 1
        || !Number.isInteger(options.maximumFinalUtf8Bytes) || options.maximumFinalUtf8Bytes < 1) {
      throw new RangeError("Live turn observation bounds must be positive integers.");
    }
  }

  clear(): void {
    this.observations.clear();
    this.pendingStarts.clear();
  }

  beginStart(threadId: string, dispatchId: string, task: string): LiveStartExpectation {
    const tracked = isBoundedIdentifier(threadId) && isBoundedIdentifier(dispatchId)
      && (this.pendingStarts.has(threadId)
        || this.pendingStarts.size < this.options.maximumObservations);
    const expectation: LiveStartExpectation = {
      threadId,
      dispatchId,
      ...taskCommitment(task),
      tracked,
    };
    if (tracked) this.pendingStarts.set(threadId, expectation);
    return expectation;
  }

  endStart(expectation: LiveStartExpectation | undefined): void {
    if (expectation?.tracked && this.pendingStarts.get(expectation.threadId) === expectation) {
      this.pendingStarts.delete(expectation.threadId);
    }
  }

  observeStartResponse(
    threadId: string,
    turn: Turn,
    expectation?: LiveStartExpectation,
  ): boolean {
    if (expectation) {
      if (expectation.tracked && this.pendingStarts.get(threadId) !== expectation) return false;
      // Request/response correlation binds the submitted identity to this turn.
      // Sparse projections are normal; a supplied user item must still agree.
      if (turn.items.some((item) => item.type === "userMessage")) {
        const user = initialUser(turn);
        if (!user
            || user.dispatchId !== expectation.dispatchId
            || !sameTaskCommitment(user, expectation)) return false;
      }
      if (!expectation.tracked) return true;
      const current = this.currentObservation(threadId, turn.id);
      if (current && (current.dispatchId !== expectation.dispatchId
          || !sameTaskCommitment(current, expectation))) return false;
    }
    this.observeStarted(threadId, turn, true, expectation);
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
      if (!expected) return;
      if (!user) {
        this.observeUnassociatedTerminal(expected, turn);
        return;
      }
      if (user.dispatchId !== expected.dispatchId || !sameTaskCommitment(user, expected)) return;
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
      if (!user || user.dispatchId !== observation.dispatchId
          || !sameTaskCommitment(user, observation)
          || (observation.userItemId !== undefined && user.id !== observation.userItemId)
          || (observation.explicit.kind === "one" && user.id === observation.explicit.itemId)
          || (observation.legacy.kind === "one" && user.id === observation.legacy.itemId)) {
        observation.conflict = true;
      } else {
        observation.userItemId = user.id;
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

  inspect(threadId: string, turnId: string, dispatchId: string, task: TaskCommitment): GalateaDispatchInspection | undefined {
    const observation = this.currentObservation(threadId, turnId);
    if (!observation || observation.dispatchId !== dispatchId
        || !sameTaskCommitment(observation, task)) return undefined;
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
    task: TaskCommitment,
  ): boolean {
    const observation = this.currentObservation(threadId, turnId);
    return observation !== undefined
      && observation.dispatchId === dispatchId
      && sameTaskCommitment(observation, task)
      && observation.pendingCompleted !== undefined
      && !observation.conflict;
  }

  private observeStarted(
    threadId: string,
    turn: Turn,
    trustedResponse: boolean,
    responseExpectation?: LiveStartExpectation,
  ): void {
    if (!isBoundedIdentifier(threadId) || !turn || typeof turn.id !== "string"
        || !isBoundedIdentifier(turn.id)) return;
    const current = this.observations.get(threadId);
    if (current?.turnId !== turn.id) {
      const user = initialUser(turn);
      const expected = responseExpectation ?? this.pendingStarts.get(threadId);
      const matchesPending = user !== undefined && expected !== undefined
        && user.dispatchId === expected.dispatchId && sameTaskCommitment(user, expected);
      if (!trustedResponse && !matchesPending) return;
      const identity = responseExpectation ?? (user === undefined ? undefined : {
        dispatchId: user.dispatchId,
        taskSha256: user.taskSha256,
        taskUtf8Bytes: user.taskUtf8Bytes,
      });
      if (!identity) return;
      const terminalCandidate = expected?.terminalBarriers?.get(turn.id);
      if (expected?.terminalBarrierOverflow && !terminalCandidate) return;
      if (!current && this.observations.size >= this.options.maximumObservations) return;
      const observation: Observation = {
        threadId,
        turnId: turn.id,
        dispatchId: identity.dispatchId,
        taskSha256: identity.taskSha256,
        taskUtf8Bytes: identity.taskUtf8Bytes,
        explicit: { kind: "none" },
        legacy: { kind: "none" },
        conflict: false,
      };
      this.observations.set(threadId, observation);
    }
    for (const item of turn.items) this.observeItem(threadId, turn.id, item);
    const expected = this.pendingStarts.get(threadId);
    const terminalCandidate = expected?.terminalBarriers?.get(turn.id);
    const observation = this.currentObservation(threadId, turn.id);
    if (observation && terminalCandidate && !observation.terminal && !observation.pendingCompleted) {
      if (terminalCandidate.conflict) {
        observation.conflict = true;
      } else if (terminalCandidate.status === "completed") {
        observation.pendingCompleted = { baseFingerprint: terminalCandidate.baseFingerprint };
        if (finalEvidenceAvailable(observation)) {
          observation.terminal = {
            status: "completed",
            fingerprint: terminalFingerprint(terminalCandidate.baseFingerprint, observation),
          };
          observation.pendingCompleted = undefined;
        }
      } else {
        observation.terminal = {
          status: terminalCandidate.status,
          fingerprint: terminalFingerprint(terminalCandidate.baseFingerprint, observation),
        };
      }
    }
  }

  private observeUnassociatedTerminal(
    expectation: LiveStartExpectation,
    turn: Turn,
  ): void {
    if (turn.status === "inProgress" || !isBoundedIdentifier(turn.id)) return;
    const barriers = expectation.terminalBarriers ??= new Map();
    const baseFingerprint = terminalBaseFingerprint(turn);
    const existing = barriers.get(turn.id);
    if (existing) {
      if (existing.status !== turn.status || existing.baseFingerprint !== baseFingerprint) {
        existing.conflict = true;
      }
      return;
    }
    if (barriers.size >= maximumUnassociatedTerminalCandidates) {
      expectation.terminalBarrierOverflow = true;
      return;
    }
    barriers.set(turn.id, { status: turn.status, baseFingerprint, conflict: false });
  }

  private currentObservation(threadId: string, turnId: string): Observation | undefined {
    const observation = this.observations.get(threadId);
    return observation?.turnId === turnId ? observation : undefined;
  }

  private discardObservation(observation: Observation): void {
    if (this.observations.get(observation.threadId) === observation) {
      this.observations.delete(observation.threadId);
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
