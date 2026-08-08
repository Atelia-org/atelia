export type LogLevel = "debug" | "info" | "warning" | "error";

export interface BridgeLogger {
  log(level: LogLevel, event: string, fields?: Record<string, unknown>): void;
}

export class JsonStderrLogger implements BridgeLogger {
  constructor(private readonly verbose = false) {}

  log(level: LogLevel, event: string, fields: Record<string, unknown> = {}): void {
    if (level === "debug" && !this.verbose) {
      return;
    }

    const safeFields = Object.fromEntries(
      Object.entries(fields).filter(([, value]) => value !== undefined),
    );
    process.stderr.write(
      `${JSON.stringify({ timestamp: new Date().toISOString(), level, event, ...safeFields })}\n`,
    );
  }
}

export class NullLogger implements BridgeLogger {
  log(): void {}
}

