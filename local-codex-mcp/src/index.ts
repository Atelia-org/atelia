import { CodexBackend } from "./codex/backend.js";
import { CodexAppServerClient } from "./codex/client.js";
import { TaskStore } from "./codex/task-store.js";
import { loadConfig } from "./config.js";
import { asBridgeError } from "./errors.js";
import { JsonStderrLogger } from "./logger.js";
import { createMcpServer } from "./mcp/server.js";
import { connectStdio, listenHttp, type HttpMcpHandle } from "./mcp/transports.js";
import { PathPolicy } from "./security/paths.js";

async function main(): Promise<void> {
  const config = loadConfig();
  const logger = new JsonStderrLogger(config.verbose);
  const pathPolicy = await PathPolicy.create(config.allowedRoots, config.defaultCwd);
  const client = new CodexAppServerClient({
    command: config.codexCommand,
    args: config.codexArgs,
    requestTimeoutMs: config.rpcTimeoutMs,
    logger,
  });
  const store = new TaskStore(config.maxResultChars, config.maxProgressChars);
  const backend = new CodexBackend({ client, pathPolicy, store, logger });

  try {
    await backend.start();
  } catch (error) {
    const bridgeError = asBridgeError(error);
    logger.log("warning", "codex_startup_check_failed", { error_code: bridgeError.code });
  }

  let http: HttpMcpHandle | undefined;
  let mcpServer: McpServerHandle | undefined;
  if (config.transport === "stdio") {
    const server = createMcpServer(backend, config, logger);
    mcpServer = { close: () => server.close() };
    await connectStdio(server);
    logger.log("info", "mcp_stdio_ready");
  } else {
    http = await listenHttp(backend, config, logger);
  }

  let shuttingDown = false;
  const shutdown = async () => {
    if (shuttingDown) return;
    shuttingDown = true;
    await http?.close();
    await mcpServer?.close();
    await backend.stop();
  };
  process.once("SIGINT", () => void shutdown().finally(() => process.exit(0)));
  process.once("SIGTERM", () => void shutdown().finally(() => process.exit(0)));
  if (config.transport === "stdio") {
    process.stdin.once("end", () => void shutdown());
  }
}

interface McpServerHandle {
  close(): Promise<void>;
}

main().catch((error) => {
  const bridgeError = asBridgeError(error);
  process.stderr.write(
    `${JSON.stringify({
      timestamp: new Date().toISOString(),
      level: "error",
      event: "bridge_start_failed",
      error_code: bridgeError.code,
      message: bridgeError.message,
    })}\n`,
  );
  process.exitCode = 1;
});

