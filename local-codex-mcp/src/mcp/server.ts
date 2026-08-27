import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod/v4";
import type { TaskBackend, TaskSnapshot } from "../backend/task-backend.js";
import type { BridgeConfig } from "../config.js";
import { BridgeError, asBridgeError } from "../errors.js";
import type { BridgeLogger } from "../logger.js";

const outputSchema = {
  ok: z.boolean(),
  status: z.enum(["idle", "running", "completed", "failed", "interrupted"]).optional(),
  thread_id: z.string().optional(),
  turn_id: z.string().optional(),
  result: z.string().optional(),
  progress: z.string().optional(),
  changed_files: z.array(z.string()).optional(),
  validation: z.array(z.string()).optional(),
  warnings: z.array(z.string()).optional(),
  interruption_requested: z.boolean().optional(),
  error: z
    .object({
      code: z.string(),
      message: z.string(),
    })
    .optional(),
};

type ToolPayload = {
  ok: boolean;
  status?: TaskSnapshot["status"];
  thread_id?: string;
  turn_id?: string;
  result?: string;
  progress?: string;
  changed_files?: string[];
  validation?: string[];
  warnings?: string[];
  interruption_requested?: boolean;
  error?: { code: string; message: string };
};

function payloadFromSnapshot(snapshot: TaskSnapshot, detail: "summary" | "final" = "summary"): ToolPayload {
  if (snapshot.status === "failed") {
    return {
      ok: false,
      status: snapshot.status,
      thread_id: snapshot.threadId,
      turn_id: snapshot.latestTurnId,
      changed_files: snapshot.changedFiles,
      validation: snapshot.validation,
      warnings: snapshot.warnings,
      error: {
        code: "TURN_FAILED",
        message: snapshot.errorMessage ?? "The Codex turn failed.",
      },
    };
  }

  return {
    ok: true,
    status: snapshot.status,
    thread_id: snapshot.threadId,
    turn_id: snapshot.activeTurnId ?? snapshot.latestTurnId,
    ...(detail === "final" && snapshot.final
      ? { result: snapshot.final }
      : snapshot.result
        ? { result: snapshot.result }
        : {}),
    ...(snapshot.status === "running"
      ? { progress: snapshot.progress ?? "Codex is still working." }
      : {}),
    changed_files: snapshot.changedFiles,
    validation: snapshot.validation,
    warnings: snapshot.warnings,
  };
}

function errorPayload(error: unknown): ToolPayload {
  const bridgeError = asBridgeError(error);
  return {
    ok: false,
    error: { code: bridgeError.code, message: bridgeError.message },
  };
}

function mcpResult(payload: ToolPayload, isError = false) {
  return {
    content: [{ type: "text" as const, text: JSON.stringify(payload) }],
    structuredContent: payload,
    ...(isError ? { isError: true } : {}),
  };
}

async function invoke(
  tool: string,
  logger: BridgeLogger,
  action: () => Promise<ToolPayload>,
) {
  const startedAt = Date.now();
  try {
    const payload = await action();
    return mcpResult(payload, !payload.ok);
  } catch (error) {
    const payload = errorPayload(error);
    logger.log("error", "mcp_tool_error", {
      tool,
      duration_ms: Date.now() - startedAt,
      error_code: payload.error?.code,
    });
    return mcpResult(payload, true);
  }
}

export function createMcpServer(
  backend: TaskBackend,
  config: Pick<BridgeConfig, "defaultWaitMs" | "maxWaitMs">,
  logger: BridgeLogger,
): McpServer {
  const server = new McpServer({
    name: "atelia-local-codex-mcp",
    title: "Local Codex Bridge",
    version: "0.1.0",
  });

  const taskSchema = z.string().trim().min(1).max(100_000).describe("High-level task for the local Codex subagent.");
  const threadIdSchema = z.string().trim().min(1).max(200).describe("Stable thread ID returned by this bridge.");
  const waitSchema = z
    .number()
    .int()
    .min(0)
    .max(config.maxWaitMs)
    .default(config.defaultWaitMs)
    .describe("Bounded wait in milliseconds. A running turn continues after this expires.");

  server.registerTool(
    "codex_delegate",
    {
      title: "Delegate to local Codex",
      description:
        "Create a persistent local Codex thread and delegate a repository investigation or workspace task. Returns a stable thread_id; long work continues after wait_ms.",
      inputSchema: {
        task: taskSchema,
        cwd: z.string().optional().describe("Optional absolute working directory inside configured allowed roots."),
        mode: z
          .enum(["research", "work"])
          .default("work")
          .describe("research is read-only; work allows writes only inside cwd."),
        local_command_network: z.boolean().default(true).describe(
          "Allow sandboxed local commands to access the network for this turn.",
        ),
        web_search: z.enum(["disabled", "cached", "indexed", "live"]).default("live").describe(
          "Configure OpenAI hosted web search independently from local command network access.",
        ),
        image_generation: z.boolean().default(true).describe(
          "Expose OpenAI hosted image generation when the provider supports it.",
        ),
        view_image: z.boolean().default(true).describe(
          "Expose Codex local image viewing for files available to the thread.",
        ),
        wait_ms: waitSchema,
      },
      outputSchema,
      annotations: {
        title: "Delegate to local Codex",
        readOnlyHint: false,
        destructiveHint: true,
        idempotentHint: false,
        openWorldHint: true,
      },
    },
    async ({ task, cwd, mode, local_command_network, web_search, image_generation, view_image, wait_ms }) =>
      invoke("codex_delegate", logger, async () =>
        payloadFromSnapshot(
          await backend.delegate({
            task,
            ...(cwd === undefined ? {} : { cwd }),
            mode,
            localCommandNetwork: local_command_network,
            tools: {
              webSearch: web_search,
              imageGeneration: image_generation,
              viewImage: view_image,
            },
            waitMs: wait_ms,
          }),
        ),
      ),
  );

  server.registerTool(
    "codex_continue",
    {
      title: "Continue local Codex thread",
      description:
        "Start a new turn in a bridge-owned persistent Codex thread, reusing its repository context. Use codex_status instead if the thread is still running.",
      inputSchema: {
        thread_id: threadIdSchema,
        task: taskSchema,
        mode: z.enum(["research", "work"]).default("work"),
        local_command_network: z.boolean().default(true),
        web_search: z.enum(["disabled", "cached", "indexed", "live"]).default("live"),
        image_generation: z.boolean().default(true),
        view_image: z.boolean().default(true),
        wait_ms: waitSchema,
      },
      outputSchema,
      annotations: {
        title: "Continue local Codex thread",
        readOnlyHint: false,
        destructiveHint: true,
        idempotentHint: false,
        openWorldHint: true,
      },
    },
    async ({ thread_id, task, mode, local_command_network, web_search, image_generation, view_image, wait_ms }) =>
      invoke("codex_continue", logger, async () =>
        payloadFromSnapshot(
          await backend.continue({
            threadId: thread_id,
            task,
            mode,
            localCommandNetwork: local_command_network,
            tools: {
              webSearch: web_search,
              imageGeneration: image_generation,
              viewImage: view_image,
            },
            waitMs: wait_ms,
          }),
        ),
      ),
  );

  server.registerTool(
    "codex_status",
    {
      title: "Get local Codex status",
      description: "Read the short current status and latest bounded result for a bridge-owned Codex thread.",
      inputSchema: { thread_id: threadIdSchema },
      outputSchema,
      annotations: {
        title: "Get local Codex status",
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ thread_id }) =>
      invoke("codex_status", logger, async () => payloadFromSnapshot(await backend.status(thread_id))),
  );

  server.registerTool(
    "codex_read",
    {
      title: "Read local Codex result",
      description:
        "Read a model-consumable summary or the bounded final agent report for a bridge-owned thread. Never returns reasoning, command output, diffs, or full history.",
      inputSchema: {
        thread_id: threadIdSchema,
        detail: z.enum(["summary", "final"]).default("summary"),
      },
      outputSchema,
      annotations: {
        title: "Read local Codex result",
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ thread_id, detail }) =>
      invoke("codex_read", logger, async () =>
        payloadFromSnapshot(await backend.read(thread_id, detail), detail),
      ),
  );

  server.registerTool(
    "codex_interrupt",
    {
      title: "Interrupt local Codex turn",
      description: "Interrupt the active turn in a bridge-owned Codex thread. Does nothing if no turn is active.",
      inputSchema: { thread_id: threadIdSchema },
      outputSchema,
      annotations: {
        title: "Interrupt local Codex turn",
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ thread_id }) =>
      invoke("codex_interrupt", logger, async () => {
        const snapshot = await backend.interrupt(thread_id);
        return {
          ...payloadFromSnapshot(snapshot),
          ...(snapshot.status === "running" ? { interruption_requested: true } : {}),
        };
      }),
  );

  return server;
}
