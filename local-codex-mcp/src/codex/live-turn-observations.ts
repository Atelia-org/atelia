import { createHash } from "node:crypto";
import type { ThreadItem } from "../../schemas/v2/ThreadItem.js";
import type { Turn } from "../../schemas/v2/Turn.js";
import type { GalateaDispatchInspection } from "../backend/galatea-staged-backend.js";
import { isStrictUnicode } from "./dispatch-inspection.js";

export interface LiveTurnObservationOptions {
  maximumObservations: number;
  maximumFinalUtf8Bytes: number;
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

const maximumObservedIdentifierUtf8Bytes = 1_024;
const maximumSemanticItemIdsPerTurn = 64;

function isBoundedIdentifier(value: string): boolean {
  return value.length > 0 && Buffer.byteLength(value, "utf8") <= maximumObservedIdentifierUtf8Bytes;
}

interface Observation {
  threadId: string;
  turnId: string;
  dispatchId: string;
  taskDigest: string;
  userItemId: string;
  userFingerprint: string;
  semanticItemFingerprints: Map<string, string>;
  explicit: FinalSlot;
  legacy: FinalSlot;
  terminal?: TerminalEvidence;
  conflict: boolean;
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

function terminalEvidence(turn: Turn, observation: Observation): TerminalEvidence | undefined {
  if (turn.status === "inProgress") return undefined;
  return {
    status: turn.status,
    fingerprint: valueFingerprint({
      status: turn.status,
      error: turn.error,
      explicit: slotFingerprint(observation.explicit),
      legacy: slotFingerprint(observation.legacy),
    }),
  };
}

export class LiveTurnObservations {
  private readonly observations = new Map<string, Observation>();

  constructor(private readonly options: LiveTurnObservationOptions) {
    if (!Number.isInteger(options.maximumObservations) || options.maximumObservations < 1
        || !Number.isInteger(options.maximumFinalUtf8Bytes) || options.maximumFinalUtf8Bytes < 1) {
      throw new RangeError("Live turn observation bounds must be positive integers.");
    }
  }

  clear(): void {
    this.observations.clear();
  }

  observeTurn(threadId: string, turn: Turn): void {
    if (!isBoundedIdentifier(threadId) || !turn || typeof turn.id !== "string"
        || !isBoundedIdentifier(turn.id)) return;
    const observationKey = key(threadId, turn.id);
    let observation = this.observations.get(observationKey);
    if (!observation) {
      const user = initialUser(turn);
      if (!user) return;
      this.removeOldTerminalObservations(threadId, turn.id);
      if (this.observations.size >= this.options.maximumObservations) return;
      observation = {
        threadId,
        turnId: turn.id,
        dispatchId: user.dispatchId,
        taskDigest: taskDigest(user.task),
        userItemId: user.id,
        userFingerprint: valueFingerprint(user),
        semanticItemFingerprints: new Map([[user.id, valueFingerprint(user)]]),
        explicit: { kind: "none" },
        legacy: { kind: "none" },
        conflict: false,
      };
      this.observations.set(observationKey, observation);
    }

    for (const item of turn.items) this.observeItem(threadId, turn.id, item);
    const terminal = terminalEvidence(turn, observation);
    if (terminal) this.observeTerminal(observation, terminal);
  }

  observeItem(threadId: string, turnId: string, item: ThreadItem): void {
    const observation = this.observations.get(key(threadId, turnId));
    if (!observation || !item || typeof item.id !== "string" || !item.id) return;
    const user = exactUser(item);
    if (item.type === "userMessage" || item.type === "agentMessage") {
      if (!isBoundedIdentifier(item.id)) {
        observation.conflict = true;
        return;
      }
      const fingerprint = valueFingerprint(user ?? item);
      const existing = observation.semanticItemFingerprints.get(item.id);
      if (existing !== undefined) {
        if (existing !== fingerprint) observation.conflict = true;
        return;
      }
      if (observation.semanticItemFingerprints.size >= maximumSemanticItemIdsPerTurn) {
        observation.conflict = true;
        return;
      }
      observation.semanticItemFingerprints.set(item.id, fingerprint);
    }
    if (user) {
      if (user.id !== observation.userItemId
          || user.dispatchId !== observation.dispatchId
          || taskDigest(user.task) !== observation.taskDigest
          || valueFingerprint(user) !== observation.userFingerprint) {
        observation.conflict = true;
      }
      return;
    }
    if (item.type === "userMessage") {
      observation.conflict = true;
      return;
    }
    if (item.type !== "agentMessage" || item.phase === "commentary") return;
    const slot = item.phase === "final_answer" ? "explicit" : "legacy";
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
    if (observation.terminal && valueFingerprint(before) !== valueFingerprint(slotFingerprint(observation[slot]))) {
      observation.conflict = true;
    }
  }

  inspect(threadId: string, turnId: string, dispatchId: string, task: string): GalateaDispatchInspection | undefined {
    const observation = this.observations.get(key(threadId, turnId));
    if (!observation || observation.dispatchId !== dispatchId
        || observation.taskDigest !== taskDigest(task)) return undefined;
    if (observation.conflict) {
      return { kind: "ambiguous", threadId, source: "live", code: "LIVE_OBSERVATION_CONFLICT" };
    }
    if (!observation.terminal) {
      return { kind: "running", threadId, turnId, source: "live" };
    }
    if (observation.terminal.status === "failed") {
      return { kind: "failed", threadId, turnId, source: "live", code: "TURN_FAILED" };
    }
    if (observation.terminal.status === "interrupted") {
      return { kind: "failed", threadId, turnId, source: "live", code: "TURN_INTERRUPTED" };
    }
    return this.selectFinal(observation);
  }

  private observeTerminal(observation: Observation, terminal: TerminalEvidence): void {
    if (!observation.terminal) {
      observation.terminal = terminal;
    } else if (observation.terminal.status !== terminal.status
        || observation.terminal.fingerprint !== terminal.fingerprint) {
      observation.conflict = true;
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

  private removeOldTerminalObservations(threadId: string, currentTurnId: string): void {
    for (const [observationKey, observation] of this.observations) {
      if (observation.threadId === threadId && observation.turnId !== currentTurnId
          && observation.terminal !== undefined) {
        this.observations.delete(observationKey);
      }
    }
  }
}
