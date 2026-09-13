import { BridgeError } from "../errors.js";

/** One monotonic deadline shared by every step of an operation. */
export class OperationDeadline {
  private readonly due: number;
  constructor(timeoutMs: number) { this.due = performance.now() + timeoutMs; }

  remainingMs(): number {
    const remaining = this.due - performance.now();
    if (remaining <= 0) throw this.timeout();
    return remaining;
  }

  async wait<T>(operation: Promise<T>): Promise<T> {
    let timer: NodeJS.Timeout | undefined;
    try {
      return await Promise.race([
        operation,
        new Promise<never>((_, reject) => {
          timer = setTimeout(() => reject(this.timeout()), this.remainingMs());
        }),
      ]);
    } finally {
      if (timer) clearTimeout(timer);
    }
  }

  private timeout(): BridgeError {
    return new BridgeError("CODEX_PROTOCOL_ERROR", "Codex operation timed out.", {
      details: { timeout: true },
    });
  }
}
