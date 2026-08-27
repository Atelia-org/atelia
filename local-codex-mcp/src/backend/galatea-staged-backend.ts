import type {
  BuiltInToolPolicy,
  TaskMode,
} from "./task-backend.js";

export interface EnsureGalateaBindingInput {
  cwd: string;
  mode: TaskMode;
  tools: BuiltInToolPolicy;
}

export interface GalateaBoundThread {
  threadId: string;
}

export interface StartGalateaBoundTurnInput {
  threadId: string;
  expectedCwd: string;
  dispatchId: string;
  task: string;
  mode: TaskMode;
  localCommandNetwork: boolean;
  tools: BuiltInToolPolicy;
}

export interface GalateaStartedTurn {
  threadId: string;
  turnId: string;
}

export interface InspectGalateaDispatchInput {
  threadId: string;
  expectedCwd: string;
  dispatchId: string;
  task: string;
  maximumFinalUtf8Bytes: number;
}

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
  | "THREAD_CWD_MISMATCH"
  | "THREAD_SHAPE_INVALID"
  | "INSPECTION_LIMIT_EXCEEDED"
  | "TURN_ID_INVALID"
  | "TURN_ID_NOT_UNIQUE"
  | "TURN_ITEMS_INCOMPLETE"
  | "TURN_ITEMS_INVALID"
  | "ITEM_ID_INVALID"
  | "ITEM_ID_NOT_UNIQUE"
  | "DISPATCH_ID_NOT_UNIQUE"
  | "DISPATCH_BODY_MISMATCH"
  | "TURN_STATUS_INVALID"
  | "FINAL_AMBIGUOUS";

export type GalateaDispatchInspection =
  | {
      kind: "not-found";
      threadId: string;
    }
  | {
      kind: "running";
      threadId: string;
      turnId: string;
    }
  | {
      kind: "completed";
      threadId: string;
      turnId: string;
      final: string;
    }
  | {
      kind: "failed";
      threadId: string;
      turnId: string;
      code: GalateaDispatchFailureCode;
    }
  | {
      kind: "ambiguous";
      threadId: string;
      code: GalateaDispatchAmbiguityCode;
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
