import type { TaskCommitment } from "../galatea/task-commitment.js";
import { BridgeError, asBridgeError } from "../errors.js";

export type GalateaDispatchState = "not-dispatched" | "may-have-dispatched";

/** Only the controlled start pipeline may provide proof of non-dispatch. */
export class GalateaStartFailure extends BridgeError {
  constructor(error: unknown, readonly dispatchState: GalateaDispatchState) {
    const failure = asBridgeError(error);
    super(failure.code, failure.message, { cause: error, details: failure.details });
  }
}

export interface EnsureGalateaBindingInput {
  cwd: string;
}

export interface GalateaBoundThread {
  threadId: string;
}

export interface StartGalateaBoundTurnInput {
  threadId: string;
  cwd: string;
  dispatchId: string;
  task: string;
}

export interface GalateaStartedTurn {
  threadId: string;
  turnId: string;
}

export interface InspectGalateaDispatchInput extends TaskCommitment {
  threadId: string;
  dispatchId: string;
  expectedTurnId: string | null;
  maximumFinalUtf8Bytes: number;
}

export type GalateaDispatchInspectionSource = "live" | "persistent";

export type GalateaDispatchFailureCode =
  | "TURN_FAILED"
  | "TURN_INTERRUPTED"
  | "FINAL_MISSING"
  | "FINAL_BLANK"
  | "FINAL_INVALID_UNICODE"
  | "FINAL_TOO_LARGE";

export type GalateaDispatchAmbiguityCode =
  | "THREAD_NOT_FOUND"
  | "THREAD_ID_MISMATCH"
  | "THREAD_OWNERSHIP_MISMATCH"
  | "THREAD_SHAPE_INVALID"
  | "INSPECTION_LIMIT_EXCEEDED"
  | "TURN_ID_INVALID"
  | "TURN_ID_NOT_UNIQUE"
  | "TURN_ITEMS_INCOMPLETE"
  | "TURN_ITEMS_INVALID"
  | "ITEM_ID_INVALID"
  | "ITEM_ID_NOT_UNIQUE"
  | "DISPATCH_ID_NOT_UNIQUE"
  | "DISPATCH_TURN_MISMATCH"
  | "DISPATCH_BODY_MISMATCH"
  | "TURN_STATUS_INVALID"
  | "FINAL_AMBIGUOUS"
  | "LIVE_OBSERVATION_CONFLICT"
  | "PAGE_SHAPE_INVALID"
  | "PAGINATION_CURSOR_INVALID"
  | "PAGINATION_CURSOR_LOOP";

export type GalateaDispatchInspection =
  | {
      kind: "not-found";
      threadId: string;
      source: "persistent";
    }
  | {
      kind: "unavailable";
      threadId: string;
      turnId: string;
      source: "persistent";
      code: "ACCEPTED_TURN_NOT_VISIBLE";
    }
  | {
      kind: "running";
      threadId: string;
      turnId: string;
      source: GalateaDispatchInspectionSource;
    }
  | {
      kind: "completed";
      threadId: string;
      turnId: string;
      final: string;
      source: GalateaDispatchInspectionSource;
    }
  | {
      kind: "failed";
      threadId: string;
      turnId: string;
      code: GalateaDispatchFailureCode;
      source: GalateaDispatchInspectionSource;
    }
  | {
      kind: "ambiguous";
      threadId: string;
      code: GalateaDispatchAmbiguityCode;
      source: GalateaDispatchInspectionSource;
    };

export interface GalateaStagedBackend {
  ensureBinding(
    input: EnsureGalateaBindingInput,
  ): Promise<GalateaBoundThread>;

  startBoundTurn(
    input: StartGalateaBoundTurnInput,
  ): Promise<GalateaStartedTurn>;

  inspectDispatch(
    input: InspectGalateaDispatchInput,
  ): Promise<GalateaDispatchInspection>;

  stop(): Promise<void>;
}
