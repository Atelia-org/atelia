using System.Text.Json;
using Atelia.MutableContextAgentProto.Core;
using Atelia.MutableContextAgentProto.Llm;
using Atelia.MutableContextAgentProto.Maze;
using Atelia.MutableContextAgentProto.Protocol;

namespace Atelia.MutableContextAgentProto;

internal static class Phase1Commands {
    private const int MaxMazeSteps = 24;

    public static int RunRenderDemo() {
        var context = CreateBaseMazeContext("演示 mutable working context 如何渲染为单条 user message。");
        context.RecordAction(
            "为了验证渲染器，创建了一个迷宫任务上下文",
            "这条行动日志会进入近期行动日志，而不是作为原始 message history 回灌。"
        );
        context.Remember(
            "WorkingContext 是当前活跃记忆；EventLog 才保存完整审计痕迹。",
            MemoryKind.Decision,
            "Phase 1 design"
        );
        context.AddTransientView(
            "view-demo-1",
            "临时观察示例",
            "这是一段临时视图内容。后续 micro-wizard 会在选择性记忆后丢弃这类原始视图。",
            "render-demo"
        );

        Console.WriteLine(new SingleUserContextRenderer().Render(context));
        return 0;
    }

    public static int RunMazeDemo() {
        var world = new MazeWorld();
        var look = world.Look();

        Console.WriteLine("Maze demo");
        Console.WriteLine(look.Description);
        Console.WriteLine();
        Console.WriteLine(look.Map);
        Console.WriteLine();
        Console.WriteLine("Tools:");
        foreach (var spec in MazeToolFactory.CreateToolSpecs()) {
            Console.WriteLine($"- {spec.Name}: {spec.Description}");
        }

        return 0;
    }

    public static int RunMazeFake() {
        var eventLog = new EventLog();
        var world = new MazeWorld();
        var policy = new FakeMazePolicy();
        var context = CreateBaseMazeContext("用 scripted fake policy 验证单 user message tool-loop 的工程闭环。");
        var dispatcher = new ToolDispatcher(CreateMazeProtocolTools(world));
        var renderer = new SingleUserContextRenderer();

        eventLog.Append(EventLogEntryKind.Note, "Fake maze run started.", world.RenderMap());
        context.RecordAction("启动 fake maze run", world.Look().Description);

        for (var step = 1; step <= MaxMazeSteps; step++) {
            Console.WriteLine($"== fake step {step} ==");
            Console.WriteLine(Preview(renderer.Render(context), maxLength: 900));
            Console.WriteLine();

            var decision = policy.Next(world);
            if (decision.IsComplete) {
                context.RecordAction("Fake policy completed the maze", decision.Final, ActionStatus.Completed);
                eventLog.Append(EventLogEntryKind.ModelOutput, "Fake policy final.", decision.Final);
                Console.WriteLine(decision.Final);
                Console.WriteLine($"EventLog entries: {eventLog.Count}");
                return 0;
            }

            if (decision.ToolCall is null) {
                Console.Error.WriteLine("Fake policy produced neither final nor tool call.");
                return 1;
            }

            var fakeModelJson = BuildFakeModelJson(decision, step);
            var parsed = ToolCallParser.Parse(fakeModelJson);
            eventLog.Append(EventLogEntryKind.ModelOutput, decision.Thought, fakeModelJson);
            var results = dispatcher.DispatchAsync(parsed.ToolCalls).AsTask().GetAwaiter().GetResult();
            foreach (var result in results) {
                var detail = result.Succeeded ? result.Content : result.Error;
                eventLog.Append(EventLogEntryKind.ToolResult, $"Tool {result.ToolName}: {detail}", detail, result.CallId);
                context.RecordAction($"调用工具 {result.ToolName}", SummarizeToolContent(detail), result.Succeeded ? ActionStatus.Completed : ActionStatus.Failed);
                context.Remember($"迷宫当前位置: {world.Position}; 目标: {world.Goal}; 步数: {world.StepsTaken}.", MemoryKind.Fact, result.ToolName);

                Console.WriteLine($"tool: {result.ToolName} => {SummarizeToolContent(detail)}");
                Console.WriteLine();
            }
        }

        Console.Error.WriteLine($"Fake maze run exceeded {MaxMazeSteps} steps.");
        return 1;
    }

    public static async Task<int> RunPingLlmAsync(CancellationToken cancellationToken = default) {
        using var client = new DeepSeekChatClient(DeepSeekOptions.FromEnvironment());
        var context = new WorkingContext("验证 DeepSeek V4 是否能在单 user message 中按 JSON 协议响应。");
        context.RecordAction("准备 ping-llm 请求", "不提供工具，只要求模型返回 final。");
        context.Remember("输出必须是单个 JSON object，不能使用自然语言包裹。", MemoryKind.Decision);

        var renderer = new SingleUserContextRenderer(
            new SingleUserContextRendererOptions {
                NextStepInstruction = BuildJsonInstruction(allowTools: false)
            }
        );
        var userMessage = renderer.Render(context);
        var raw = await client.SendUserMessageAsync(userMessage, cancellationToken).ConfigureAwait(false);
        var parsed = ToolCallParser.Parse(raw);

        Console.WriteLine("DeepSeek ping succeeded.");
        Console.WriteLine($"thought: {parsed.Thought}");
        Console.WriteLine($"final: {parsed.Final}");
        Console.WriteLine($"tool_calls: {parsed.ToolCalls.Count}");
        return 0;
    }

    public static async Task<int> RunMazeLlmAsync(CancellationToken cancellationToken = default) {
        using var client = new DeepSeekChatClient(DeepSeekOptions.FromEnvironment());
        using var runLog = RunLog.Create("maze-llm-run");
        var eventLog = new EventLog();
        var world = new MazeWorld();
        var context = CreateBaseMazeContext("走出迷宫。你只能通过可用工具观察和移动。");
        var dispatcher = new ToolDispatcher(CreateMazeProtocolTools(world));
        var renderer = new SingleUserContextRenderer(
            new SingleUserContextRendererOptions {
                MaxRecentActions = 16,
                MaxMemories = 20,
                NextStepInstruction = BuildJsonInstruction(allowTools: true)
            }
        );

        context.RecordAction("初始化迷宫任务", world.Look().Description);
        context.Remember("如果已经到达 goal，请返回 final；否则请选择一个 maze 工具调用。", MemoryKind.Decision);
        eventLog.Append(EventLogEntryKind.Note, "LLM maze run started.", world.RenderMap());

        for (var step = 1; step <= MaxMazeSteps; step++) {
            var userMessage = renderer.Render(context);
            eventLog.Append(EventLogEntryKind.UserInput, $"Rendered single user message for LLM step {step}.", userMessage);

            Console.WriteLine($"== llm step {step} ==");
            var raw = await client.SendUserMessageAsync(userMessage, cancellationToken).ConfigureAwait(false);
            await runLog.AppendAsync("request", client.LastRawRequest, cancellationToken).ConfigureAwait(false);
            await runLog.AppendAsync("response", client.LastRawResponse, cancellationToken).ConfigureAwait(false);
            eventLog.Append(EventLogEntryKind.ModelOutput, $"LLM response for step {step}.", raw);

            AgentModelResponse parsed;
            try {
                parsed = ToolCallParser.Parse(raw);
            }
            catch (ToolCallParseException ex) {
                context.RecordAction("模型输出 JSON 解析失败", ex.Message, ActionStatus.Failed);
                context.Remember("上一轮模型输出无法解析；下一轮必须只输出符合协议的 JSON object。", MemoryKind.Warning, "ToolCallParser");
                context.AddTransientView("last-invalid-json", "上一轮无效模型输出", ex.RawText, "DeepSeek response");
                Console.Error.WriteLine(ex.Message);
                Console.Error.WriteLine(Preview(ex.RawText, maxLength: 600));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(parsed.Thought)) {
                context.RecordAction("模型思路", parsed.Thought);
            }

            if (!string.IsNullOrWhiteSpace(parsed.Final) && parsed.ToolCalls.Count == 0) {
                context.RecordAction("模型给出最终答案", parsed.Final);
                Console.WriteLine(parsed.Final);
                Console.WriteLine($"EventLog entries: {eventLog.Count}");
                Console.WriteLine($"Run log: {runLog.Path}");
                return world.IsAtGoal ? 0 : 1;
            }

            if (parsed.ToolCalls.Count == 0) {
                context.RecordAction("模型未给出工具调用", "需要继续要求它按协议行动。", ActionStatus.Failed);
                continue;
            }

            var results = await dispatcher.DispatchAsync(parsed.ToolCalls, cancellationToken).ConfigureAwait(false);
            foreach (var result in results) {
                var detail = result.Succeeded ? result.Content : result.Error;
                eventLog.Append(EventLogEntryKind.ToolResult, $"Tool {result.ToolName}: {detail}", detail, result.CallId);
                context.RecordAction($"调用工具 {result.ToolName}", SummarizeToolContent(detail), result.Succeeded ? ActionStatus.Completed : ActionStatus.Failed);
                context.Remember($"迷宫状态: position={world.Position}, goal={world.Goal}, steps={world.StepsTaken}, atGoal={world.IsAtGoal}.", MemoryKind.Fact, result.ToolName);
                Console.WriteLine($"tool: {result.ToolName} => {SummarizeToolContent(detail)}");
            }

            if (world.IsAtGoal) {
                context.RecordAction("环境确认已到达终点", $"position={world.Position}, steps={world.StepsTaken}");
            }
        }

        Console.Error.WriteLine($"LLM maze run exceeded {MaxMazeSteps} steps.");
        Console.WriteLine($"EventLog entries: {eventLog.Count}");
        Console.WriteLine($"Run log: {runLog.Path}");
        return 1;
    }

    private static WorkingContext CreateBaseMazeContext(string goal) {
        var context = new WorkingContext(goal);
        foreach (var spec in MazeToolFactory.CreateToolSpecs()) {
            var usage = spec.Parameters.Count == 0
                ? "{}"
                : "{ \"direction\": \"north|east|south|west\" }";
            context.AddTool(spec.Name, spec.Description, usage);
        }

        return context;
    }

    private static IReadOnlyList<ITool> CreateMazeProtocolTools(MazeWorld world) {
        var factory = new MazeToolFactory(world);
        return MazeToolFactory.CreateToolSpecs()
            .Select(spec => new MazeProtocolTool(factory, spec))
            .ToArray();
    }

    private static string BuildJsonInstruction(bool allowTools) {
        var toolPart = allowTools
            ? """
            如需行动，返回 tool_calls 数组。可用工具名只允许 `maze.look`、`maze.move`、`maze.status`。
            `maze.move` 参数示例：{ "direction": "east" }。
            """
            : "本轮不允许调用工具，tool_calls 必须是空数组。";

        return string.Concat(
            """
        你是这个任务中的 Agent。请只输出一个 JSON object，不要使用 Markdown，不要添加额外说明。
        JSON shape:
        { "thought": "一句简短思路", "tool_calls": [], "final": "完成时的答案或 null" }
        """, toolPart, """

        如果任务已完成，返回 `final` 字符串，并让 `tool_calls` 为空数组。
        """
        );
    }

    private static string BuildFakeModelJson(FakeMazeDecision decision, int step) {
        if (decision.ToolCall is null) {
            return JsonSerializer.Serialize(
                new {
                    thought = decision.Thought,
                    tool_calls = Array.Empty<object>(),
                    final = decision.Final
                }
            );
        }

        return JsonSerializer.Serialize(
            new {
                thought = decision.Thought,
                tool_calls = new[] {
                    new {
                    id = $"fake-{step}",
                    name = decision.ToolCall.Name,
                    arguments = decision.ToolCall.Arguments
                }
            },
                final = (string?)null
            }
        );
    }

    private static string Preview(string text, int maxLength) {
        if (text.Length <= maxLength) { return text; }

        return text[..maxLength] + "\n... <truncated>";
    }

    private static string? SummarizeToolContent(string? content) {
        if (string.IsNullOrWhiteSpace(content)) { return content; }

        var firstLine = content.Replace("\r\n", "\n").Split('\n', 2)[0].Trim();
        return firstLine.Length <= 240 ? firstLine : firstLine[..240] + "...";
    }

    private sealed class MazeProtocolTool : ITool {
        private readonly MazeToolFactory _factory;

        public MazeProtocolTool(MazeToolFactory factory, MazeToolSpec spec) {
            _factory = factory;
            Definition = new ToolDefinition(
                spec.Name,
                spec.Description,
                JsonSerializer.SerializeToElement(BuildParameterSchema(spec))
            );
        }

        public ToolDefinition Definition { get; }

        public ValueTask<ToolResult> ExecuteAsync(ToolCallRequest request, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();

            var arguments = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var property in request.Arguments.EnumerateObject()) {
                arguments[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.ToString();
            }

            var result = _factory.Execute(request.Name, arguments);
            var content = result.Success
                ? $"{result.Message}\n{JsonSerializer.Serialize(result.Payload)}"
                : null;
            return ValueTask.FromResult(
                result.Success
                    ? ToolResult.Success(request.Id, request.Name, content ?? result.Message)
                    : ToolResult.Failure(request.Id, request.Name, result.Message)
            );
        }

        private static object BuildParameterSchema(MazeToolSpec spec) {
            var properties = spec.Parameters.ToDictionary(
                parameter => parameter.Name,
                parameter => (object)new {
                    type = "string",
                    description = parameter.Description
                },
                StringComparer.Ordinal
            );
            var required = spec.Parameters.Where(static parameter => parameter.Required).Select(static parameter => parameter.Name).ToArray();
            return new {
                type = "object",
                properties,
                required
            };
        }
    }

    private sealed class RunLog : IDisposable {
        private readonly StreamWriter _writer;

        private RunLog(string path) {
            Path = path;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            _writer = new StreamWriter(File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        }

        public string Path { get; }

        public static RunLog Create(string name) {
            var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
            var path = System.IO.Path.Combine(
                ".atelia",
                "debug-logs",
                "MutableContextAgentProto",
                $"{timestamp}-{name}.jsonl"
            );
            return new RunLog(path);
        }

        public async ValueTask AppendAsync(string kind, string? rawJson, CancellationToken cancellationToken) {
            var line = JsonSerializer.Serialize(
                new {
                    ts = DateTimeOffset.Now,
                    kind,
                    raw = rawJson
                }
            );
            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Dispose() {
            _writer.Dispose();
        }
    }
}
