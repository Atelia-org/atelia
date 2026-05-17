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

            var parsed = BuildFakeModelResponse(decision, step);
            eventLog.Append(EventLogEntryKind.ModelOutput, decision.Thought, parsed.RawResponse);
            var results = dispatcher.DispatchAsync(parsed.ToolCalls).AsTask().GetAwaiter().GetResult();
            foreach (var result in results) {
                var detail = result.Succeeded ? result.Content : result.Error;
                eventLog.Append(EventLogEntryKind.ToolResult, $"Tool {result.ToolName}: {detail}", detail, result.CallId);
                context.RecordAction($"调用工具 {result.ToolName}", SummarizeToolContent(detail), result.Succeeded ? ActionStatus.Completed : ActionStatus.Failed);
                context.UpsertMemory("maze.current_state", $"迷宫当前位置: {world.Position}; 目标: {world.Goal}; 步数: {world.StepsTaken}.", MemoryKind.Fact, result.ToolName);

                Console.WriteLine($"tool: {result.ToolName} => {SummarizeToolContent(detail)}");
                Console.WriteLine();
            }
        }

        Console.Error.WriteLine($"Fake maze run exceeded {MaxMazeSteps} steps.");
        return 1;
    }

    public static async Task<int> RunPingLlmAsync(CancellationToken cancellationToken = default) {
        using var client = new DeepSeekChatClient(DeepSeekOptions.FromEnvironment());
        var context = new WorkingContext("验证 DeepSeek V4 Chat Completions 连通性。");
        context.RecordAction("准备 ping-llm 请求", "不提供工具，只要求模型用正文给出简短回复。");

        var renderer = new SingleUserContextRenderer(
            new SingleUserContextRendererOptions {
                NextStepInstruction = "请用一句话回复 ping ok。"
            }
        );
        var userMessage = renderer.Render(context);
        var response = await client.SendTurnAsync(new ChatTurnRequest(userMessage, [], ChatToolChoice.None), cancellationToken).ConfigureAwait(false);

        Console.WriteLine("DeepSeek ping succeeded.");
        Console.WriteLine($"content: {response.Content}");
        Console.WriteLine($"tool_calls: {response.ToolCalls.Count}");
        Console.WriteLine($"finish_reason: {response.FinishReason}");
        return 0;
    }

    public static async Task<int> RunMazeLlmAsync(CancellationToken cancellationToken = default) {
        using var client = new DeepSeekChatClient(DeepSeekOptions.FromEnvironment());
        using var runLog = RunLog.Create("maze-llm-run");
        var eventLog = new EventLog();
        var world = new MazeWorld();
        var context = CreateBaseMazeContext("走出迷宫。你只能通过可用工具观察和移动。");
        var tools = CreateMazeProtocolTools(world);
        var dispatcher = new ToolDispatcher(tools);
        var renderer = new SingleUserContextRenderer(
            new SingleUserContextRendererOptions {
                MaxRecentActions = 16,
                MaxMemories = 20,
                NextStepInstruction = BuildNativeToolInstruction()
            }
        );

        context.RecordAction("初始化迷宫任务", world.Look().Description);
        context.Remember("如果已经到达 goal，请直接给出最终答复；否则调用一个 maze 工具继续探索或移动。", MemoryKind.Decision);
        eventLog.Append(EventLogEntryKind.Note, "LLM maze run started.", world.RenderMap());

        for (var step = 1; step <= MaxMazeSteps; step++) {
            var userMessage = renderer.Render(context);
            eventLog.Append(EventLogEntryKind.UserInput, $"Rendered single user message for LLM step {step}.", userMessage);

            Console.WriteLine($"== llm step {step} ==");
            var response = await client.SendTurnAsync(
                new ChatTurnRequest(userMessage, tools.Select(tool => tool.Definition).ToArray(), ChatToolChoice.Auto),
                cancellationToken
            ).ConfigureAwait(false);
            await runLog.AppendAsync("request", client.LastRawRequest, cancellationToken).ConfigureAwait(false);
            await runLog.AppendAsync("response", client.LastRawResponse, cancellationToken).ConfigureAwait(false);
            eventLog.Append(EventLogEntryKind.ModelOutput, $"LLM response for step {step}.", response.RawResponse);

            if (!string.IsNullOrWhiteSpace(response.Content)) {
                context.RecordAction("模型正文", response.Content);
            }

            if (!string.IsNullOrWhiteSpace(response.Content) && response.ToolCalls.Count == 0) {
                context.RecordAction("模型给出最终答案", response.Content);
                Console.WriteLine(response.Content);
                Console.WriteLine($"EventLog entries: {eventLog.Count}");
                Console.WriteLine($"Run log: {runLog.Path}");
                return world.IsAtGoal ? 0 : 1;
            }

            if (response.ToolCalls.Count == 0) {
                context.RecordAction("模型未给出工具调用", "需要继续要求它调用服务端工具或给出最终答案。", ActionStatus.Failed);
                continue;
            }

            var results = await dispatcher.DispatchAsync(response.ToolCalls, cancellationToken).ConfigureAwait(false);
            foreach (var result in results) {
                var detail = result.Succeeded ? result.Content : result.Error;
                eventLog.Append(EventLogEntryKind.ToolResult, $"Tool {result.ToolName}: {detail}", detail, result.CallId);
                context.RecordAction($"调用工具 {result.ToolName}", SummarizeToolContent(detail), result.Succeeded ? ActionStatus.Completed : ActionStatus.Failed);
                context.UpsertMemory("maze.current_state", $"迷宫状态: position={world.Position}, goal={world.Goal}, steps={world.StepsTaken}, atGoal={world.IsAtGoal}.", MemoryKind.Fact, result.ToolName);
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

    private static string BuildNativeToolInstruction()
        => """
        你是这个任务中的 Agent。需要观察、移动或确认状态时，请调用服务端提供的工具。
        如果任务已经完成，请不要再调用工具，直接用一句话给出最终结果。
        """;

    private static ChatTurnResponse BuildFakeModelResponse(FakeMazeDecision decision, int step) {
        if (decision.ToolCall is null) {
            var rawFinal = JsonSerializer.Serialize(
                new {
                    content = decision.Final,
                    tool_calls = Array.Empty<object>()
                }
            );
            return new ChatTurnResponse(decision.Final, [], "stop", rawFinal);
        }

        var arguments = JsonSerializer.SerializeToElement(decision.ToolCall.Arguments);
        var call = new ToolCallRequest($"fake-{step}", decision.ToolCall.Name, arguments);
        var rawToolCall = JsonSerializer.Serialize(
            new {
                content = (string?)null,
                tool_calls = new[] {
                    new {
                        id = call.Id,
                        type = "function",
                        function = new {
                            name = call.Name,
                            arguments = call.Arguments
                        }
                    }
                }
            }
        );
        return new ChatTurnResponse(null, [call], "tool_calls", rawToolCall);
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
