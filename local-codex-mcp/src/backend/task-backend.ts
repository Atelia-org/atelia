export type TaskMode = "research" | "work";
export type TaskStatus = "idle" | "running" | "completed" | "failed" | "interrupted";

export interface DelegateTaskInput {
  task: string;
  cwd?: string;
  mode: TaskMode;
  network: boolean;
  waitMs: number;
  clientUserMessageId?: string;
}

export interface ContinueTaskInput {
  threadId: string;
  expectedCwd?: string;
  task: string;
  mode: TaskMode;
  network: boolean;
  waitMs: number;
  clientUserMessageId?: string;
}

export interface TaskSnapshot {
  threadId: string;
  status: TaskStatus;
  activeTurnId?: string;
  latestTurnId?: string;
  result?: string;
  final?: string;
  finalTruncated?: boolean;
  progress?: string;
  changedFiles: string[];
  validation: string[];
  warnings: string[];
  errorMessage?: string;
}

export interface TaskBackend {
  start(): Promise<void>;
  stop(): Promise<void>;
  delegate(input: DelegateTaskInput): Promise<TaskSnapshot>;
  continue(input: ContinueTaskInput): Promise<TaskSnapshot>;
  status(threadId: string): Promise<TaskSnapshot>;
  read(threadId: string, detail: "summary" | "final"): Promise<TaskSnapshot>;
  interrupt(threadId: string): Promise<TaskSnapshot>;
}
