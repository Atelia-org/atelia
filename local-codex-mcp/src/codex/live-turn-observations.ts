import { createHash } from "node:crypto";
import type { ThreadItem } from "../../schemas/v2/ThreadItem.js";
import type { Turn } from "../../schemas/v2/Turn.js";
import type { GalateaDispatchInspection } from "../backend/galatea-staged-backend.js";
import { classifyTurnEvidence, hasExactTaskBody } from "./dispatch-inspection.js";

export interface LiveTurnObservationOptions {
  maximumObservations: number;
  maximumFinalUtf8Bytes: number;
}

interface Observation {
  turn: Turn;
  dispatchId: string;
  taskDigest: string;
  items: Map<string, ThreadItem>;
  conflict: boolean;
  terminal: boolean;
  finalTooLarge: boolean;
}

function taskDigest(task: string): string {
  const bytes = Buffer.allocUnsafe(task.length * 2);
  for (let index = 0; index < task.length; index += 1) {
    bytes.writeUInt16LE(task.charCodeAt(index), index * 2);
  }
  return createHash("sha256")
    .update("atelia.galatea.live-turn-task.v1\0", "utf8")
    .update(bytes)
    .digest("hex");
}

function key(threadId: string, turnId: string): string {
  return `${threadId}\0${turnId}`;
}

function bindingFromTurn(turn: Turn): { dispatchId: string; task: string } | undefined {
  const users = turn.items.filter(
    (item): item is Extract<ThreadItem, { type: "userMessage" }> =>
      item.type === "userMessage" && typeof item.clientId === "string",
  );
  if (users.length !== 1) return undefined;
  const content = users[0]!.content;
  if (content.length !== 1 || content[0]?.type !== "text") return undefined;
  const task = content[0].text;
  return hasExactTaskBody(users[0]!, task)
    ? { dispatchId: users[0]!.clientId!, task }
    : undefined;
}

export class LiveTurnObservations {
  private readonly observations = new Map<string, Observation>();

  constructor(private readonly options: LiveTurnObservationOptions) {
    if (!Number.isInteger(options.maximumObservations) || options.maximumObservations < 1) {
      throw new RangeError("maximumObservations must be a positive integer.");
    }
  }

  clear(): void {
    this.observations.clear();
  }

  observeTurn(threadId: string, turn: Turn): void {
    if (!threadId || !turn || typeof turn.id !== "string" || !turn.id) return;
    const observationKey = key(threadId, turn.id);
    let observation = this.observations.get(observationKey);
    if (!observation) {
      const binding = bindingFromTurn(turn);
      if (!binding || this.observations.size >= this.options.maximumObservations) return;
      observation = {
        turn: { ...turn, items: [] },
        dispatchId: binding.dispatchId,
        taskDigest: taskDigest(binding.task),
        items: new Map(),
        conflict: false,
        terminal: turn.status !== "inProgress",
        finalTooLarge: false,
      };
      this.observations.set(observationKey, observation);
    } else {
      const binding = bindingFromTurn(turn);
      if (binding) {
        const digest = taskDigest(binding.task);
        if (binding.dispatchId !== observation.dispatchId || digest !== observation.taskDigest) {
          observation.conflict = true;
        }
      }
      if (!(observation.terminal && turn.status === "inProgress")) {
        observation.turn = { ...turn, items: [] };
        observation.terminal ||= turn.status !== "inProgress";
      }
    }
    for (const item of turn.items) this.observeItem(threadId, turn.id, item);
  }

  observeItem(threadId: string, turnId: string, item: ThreadItem): void {
    if (!item || typeof item.id !== "string" || !item.id) return;
    const observation = this.observations.get(key(threadId, turnId));
    if (!observation) return;
    if (item.type === "agentMessage"
        && item.phase !== "commentary"
        && Buffer.byteLength(item.text, "utf8") > this.options.maximumFinalUtf8Bytes) {
      observation.finalTooLarge = true;
      return;
    }
    const existing = observation.items.get(item.id);
    if (existing !== undefined && JSON.stringify(existing) !== JSON.stringify(item)) {
      observation.conflict = true;
      return;
    }
    observation.items.set(item.id, item);
  }

  inspect(threadId: string, turnId: string, dispatchId: string, task: string): GalateaDispatchInspection | undefined {
    const observation = this.observations.get(key(threadId, turnId));
    if (!observation) return undefined;
    if (observation.dispatchId !== dispatchId || observation.taskDigest !== taskDigest(task)) return undefined;
    if (observation.conflict) {
      return { kind: "ambiguous", threadId, source: "live", code: "LIVE_OBSERVATION_CONFLICT" };
    }
    if (observation.finalTooLarge && observation.turn.status === "completed") {
      return { kind: "failed", threadId, turnId, source: "live", code: "FINAL_TOO_LARGE" };
    }
    return classifyTurnEvidence(
      threadId,
      observation.turn,
      [...observation.items.values()],
      dispatchId,
      task,
      this.options.maximumFinalUtf8Bytes,
      "live",
    );
  }
}
