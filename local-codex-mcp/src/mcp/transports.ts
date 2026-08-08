import { randomUUID } from "node:crypto";
import type { Server as HttpServer } from "node:http";
import { isInitializeRequest } from "@modelcontextprotocol/sdk/types.js";
import { createMcpExpressApp } from "@modelcontextprotocol/sdk/server/express.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";
import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import type { TaskBackend } from "../backend/task-backend.js";
import type { BridgeConfig } from "../config.js";
import type { BridgeLogger } from "../logger.js";
import { createMcpServer } from "./server.js";

export async function connectStdio(server: McpServer): Promise<void> {
  await server.connect(new StdioServerTransport());
}

export interface HttpMcpHandle {
  close(): Promise<void>;
}

interface Session {
  transport: StreamableHTTPServerTransport;
  server: McpServer;
}

export async function listenHttp(
  backend: TaskBackend,
  config: BridgeConfig,
  logger: BridgeLogger,
): Promise<HttpMcpHandle> {
  const app = createMcpExpressApp({ host: config.httpHost });
  const sessions = new Map<string, Session>();

  app.post("/mcp", async (req, res) => {
    try {
      const sessionId = req.headers["mcp-session-id"];
      if (typeof sessionId === "string") {
        const session = sessions.get(sessionId);
        if (!session) {
          res.status(404).json({
            jsonrpc: "2.0",
            error: { code: -32000, message: "Unknown MCP session" },
            id: null,
          });
          return;
        }
        await session.transport.handleRequest(req, res, req.body);
        return;
      }

      if (!isInitializeRequest(req.body)) {
        res.status(400).json({
          jsonrpc: "2.0",
          error: { code: -32000, message: "Initialize request required" },
          id: null,
        });
        return;
      }

      let session: Session;
      const transport = new StreamableHTTPServerTransport({
        sessionIdGenerator: randomUUID,
        onsessioninitialized: (newSessionId) => {
          sessions.set(newSessionId, session);
        },
      });
      const server = createMcpServer(backend, config, logger);
      session = { transport, server };
      transport.onclose = () => {
        if (transport.sessionId) sessions.delete(transport.sessionId);
      };
      await server.connect(transport);
      await transport.handleRequest(req, res, req.body);
    } catch {
      logger.log("error", "mcp_http_request_failed");
      if (!res.headersSent) {
        res.status(500).json({
          jsonrpc: "2.0",
          error: { code: -32603, message: "Internal server error" },
          id: null,
        });
      }
    }
  });

  const withSession = async (
    req: Parameters<Parameters<typeof app.get>[1]>[0],
    res: Parameters<Parameters<typeof app.get>[1]>[1],
  ) => {
    const sessionId = req.headers["mcp-session-id"];
    const session = typeof sessionId === "string" ? sessions.get(sessionId) : undefined;
    if (!session) {
      res.status(400).send("Invalid or missing MCP session ID");
      return;
    }
    await session.transport.handleRequest(req, res);
  };

  app.get("/mcp", withSession);
  app.delete("/mcp", withSession);

  const listener = await new Promise<HttpServer>((resolve, reject) => {
    const value = app.listen(config.httpPort, config.httpHost, () => resolve(value));
    value.once("error", reject);
  });
  logger.log("info", "mcp_http_listening", {
    host: config.httpHost,
    port: config.httpPort,
    endpoint: "/mcp",
  });

  return {
    async close() {
      await Promise.all([...sessions.values()].map((session) => session.transport.close()));
      await new Promise<void>((resolve, reject) =>
        listener.close((error) => (error ? reject(error) : resolve())),
      );
    },
  };
}
