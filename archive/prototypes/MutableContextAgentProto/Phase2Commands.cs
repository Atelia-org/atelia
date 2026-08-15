using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Atelia.MutableContextAgentProto.Llm;
using Atelia.MutableContextAgentProto.Llm.ChatHistory;
using Atelia.MutableContextAgentProto.Phase2.Fake;
using Atelia.MutableContextAgentProto.Phase2.Model;
using Atelia.MutableContextAgentProto.Phase2.Tools;
using Atelia.MutableContextAgentProto.Protocol;

namespace Atelia.MutableContextAgentProto;

internal static class Phase2Commands {
    private const string ViewFileToolName = "view_file";
    private const string SelectRememberToolName = "select_remember";
    private const int MaxLlmSteps = 12;

    public static int RunFakeWizard() {
        var workspaceRoot = Phase2FakeScenario.WorkspaceRoot;
        var viewFile = new ViewFileToolLogic(workspaceRoot);
        var selector = new SelectRememberToolLogic();

        Console.WriteLine("Phase 2 fake wizard");
        Console.WriteLine($"Task: {Phase2FakeScenario.TaskText}");
        Console.WriteLine();

        foreach (var path in Phase2FakeScenario.RecommendedFiles) {
            var intention = BuildIntentionFor(path);
            var fullView = viewFile.ViewAsync(intention, path).AsTask().GetAwaiter().GetResult();
            var fakeView = new Phase2FakeNumberedFileView(
                fullView.Path,
                fullView.Intention,
                fullView.Lines.Select(static line => line.Text).ToArray()
            );
            var fakeSelection = FakeSelectionPolicy.Select(path, intention, fakeView);
            var selected = selector.Select(fullView, fakeSelection.Ranges, fakeSelection.Summary, fakeSelection.Notes);

            Console.WriteLine($"== {path} ==");
            Console.WriteLine($"full lines: {fullView.LineCount}");
            Console.WriteLine($"selected ranges: {string.Join(", ", selected.Memory.Ranges)}");
            Console.WriteLine(Preview(selected.ReducedViewText, maxLength: 1200));
            Console.WriteLine();
        }

        return 0;
    }

    public static async Task<int> RunLlmWizardAsync(CancellationToken cancellationToken = default) {
        using var client = new DeepSeekHistoryChatClient(DeepSeekOptions.FromEnvironment());
        using var runLog = RunLog.Create("phase2-llm-wizard");

        var workspaceRoot = Phase2FakeScenario.WorkspaceRoot;
        var viewLogic = new ViewFileToolLogic(workspaceRoot);
        var selectLogic = new SelectRememberToolLogic();
        var mainTools = new[] { BuildViewFileDefinition() };
        var selectTools = new[] { BuildSelectRememberDefinition() };

        var messages = new List<ChatHistoryMessage> {
            new SystemChatHistoryMessage(BuildPhase2SystemPrompt()),
            new UserChatHistoryMessage(BuildPhase2UserPrompt())
        };

        for (var step = 1; step <= MaxLlmSteps; step++) {
            Console.WriteLine($"== phase2 llm step {step} ==");
            var response = await client.SendAsync(
                new ChatHistoryRequest(messages.ToArray(), mainTools, ChatToolChoice.Auto),
                cancellationToken
            ).ConfigureAwait(false);
            await runLog.AppendAsync("main-request", client.LastRawRequest, cancellationToken).ConfigureAwait(false);
            await runLog.AppendAsync("main-response", client.LastRawResponse, cancellationToken).ConfigureAwait(false);

            var assistant = response.AssistantMessage;
            if (assistant.ToolCalls is { Count: > 0 }) {
                var call = assistant.ToolCalls.FirstOrDefault(call => string.Equals(call.Name, ViewFileToolName, StringComparison.Ordinal));
                if (call is null) {
                    var unsupported = assistant.ToolCalls[0];
                    messages.Add(new AssistantChatHistoryMessage(assistant.Content, assistant.ReasoningContent, [unsupported]));
                    messages.Add(new ToolChatHistoryMessage(unsupported.Id, unsupported.Name, $"Unsupported tool in main timeline: {unsupported.Name}"));
                    continue;
                }

                if (assistant.ToolCalls.Count > 1) {
                    Console.WriteLine($"model emitted {assistant.ToolCalls.Count} tool calls; Phase 2 v1 handles only the first view_file call.");
                }

                var singleCallAssistant = new AssistantChatHistoryMessage(
                    assistant.Content,
                    assistant.ReasoningContent,
                    [call]
                );
                var wizard = await RunViewFileWizardAsync(
                    client,
                    runLog,
                    messages,
                    singleCallAssistant,
                    call,
                    viewLogic,
                    selectLogic,
                    selectTools,
                    cancellationToken
                ).ConfigureAwait(false);

                messages = wizard.MainTimeline;
                Console.WriteLine($"view_file {wizard.Path}: {wizard.Outcome}; fullLines={wizard.FullLineCount}; selected={wizard.SelectedRanges}");
                continue;
            }

            messages.Add(assistant);
            if (!string.IsNullOrWhiteSpace(assistant.Content)) {
                Console.WriteLine(assistant.Content);
                Console.WriteLine($"Run log: {runLog.Path}");
                return 0;
            }

            messages.Add(new UserChatHistoryMessage("请继续。若需要查看文件，请调用 view_file；若已经足够，请直接回答。"));
        }

        Console.Error.WriteLine($"Phase2 LLM wizard exceeded {MaxLlmSteps} steps.");
        Console.WriteLine($"Run log: {runLog.Path}");
        return 1;
    }

    private static async Task<ViewWizardOutcome> RunViewFileWizardAsync(
        DeepSeekHistoryChatClient client,
        RunLog runLog,
        IReadOnlyList<ChatHistoryMessage> wizardStart,
        AssistantChatHistoryMessage viewAssistant,
        ToolCallRequest viewCall,
        ViewFileToolLogic viewLogic,
        SelectRememberToolLogic selectLogic,
        IReadOnlyList<ToolDefinition> selectTools,
        CancellationToken cancellationToken
    ) {
        var path = GetRequiredStringArgument(viewCall.Arguments, "path");
        var intention = GetRequiredStringArgument(viewCall.Arguments, "intention");
        var fullView = await viewLogic.ViewAsync(intention, path, cancellationToken).ConfigureAwait(false);
        var fullToolMessage = new ToolChatHistoryMessage(viewCall.Id, ViewFileToolName, fullView.Render());

        var sideTimeline = new List<ChatHistoryMessage>(wizardStart) {
            viewAssistant,
            fullToolMessage,
            new AssistantChatHistoryMessage(
                "我已经看到了文件的完整内容。接下来我必须调用 select_remember，选择和本次 intention 最相关、需要保留到主时间线的行号区间。"
            )
        };

        var selectResponse = await client.SendAsync(
            new ChatHistoryRequest(sideTimeline.ToArray(), selectTools, ChatToolChoice.Auto),
            cancellationToken
        ).ConfigureAwait(false);
        await runLog.AppendAsync("wizard-request", client.LastRawRequest, cancellationToken).ConfigureAwait(false);
        await runLog.AppendAsync("wizard-response", client.LastRawResponse, cancellationToken).ConfigureAwait(false);

        var selectCall = selectResponse.AssistantMessage.ToolCalls?
            .FirstOrDefault(call => string.Equals(call.Name, SelectRememberToolName, StringComparison.Ordinal));
        if (selectCall is null) {
            var fallback = new List<ChatHistoryMessage>(wizardStart) {
                viewAssistant,
                fullToolMessage,
                new UserChatHistoryMessage("注意：select_remember 未被调用，本轮回退为完整 view_file 结果。")
            };
            return new ViewWizardOutcome(fallback, path, "fallback-full-result", fullView.LineCount, "(none)");
        }

        var selectedPath = GetRequiredStringArgument(selectCall.Arguments, "path");
        var selectedIntention = GetRequiredStringArgument(selectCall.Arguments, "intention");
        if (!IsLatestViewSelection(fullView, selectedPath, selectedIntention)) {
            var fallback = new List<ChatHistoryMessage>(wizardStart) {
                viewAssistant,
                fullToolMessage,
                new UserChatHistoryMessage("注意：select_remember 未匹配最近一次 view_file，本轮回退为完整 view_file 结果。")
            };
            return new ViewWizardOutcome(fallback, path, "fallback-selection-mismatch", fullView.LineCount, "(none)");
        }

        var ranges = GetStringArrayArgument(selectCall.Arguments, "ranges");
        var summary = GetRequiredStringArgument(selectCall.Arguments, "summary");
        var notes = GetOptionalStringArgument(selectCall.Arguments, "notes");
        var selected = selectLogic.Select(fullView, ranges, summary, notes);

        var reducedToolMessage = new ToolChatHistoryMessage(viewCall.Id, ViewFileToolName, selected.ReducedViewText);
        var mainTimeline = new List<ChatHistoryMessage>(wizardStart) {
            viewAssistant,
            reducedToolMessage
        };
        return new ViewWizardOutcome(
            mainTimeline,
            path,
            "rewritten-reduced-result",
            fullView.LineCount,
            string.Join(", ", selected.Memory.Ranges)
        );
    }

    private static ToolDefinition BuildViewFileDefinition()
        => new(
            ViewFileToolName,
            "View a workspace file for a specific intention. The tool returns numbered lines. Always provide a clear intention.",
            JsonSerializer.SerializeToElement(
                new {
                    type = "object",
                    properties = new {
                        intention = new {
                            type = "string",
                            description = "Why you need to inspect this file and what information you are looking for."
                        },
                        path = new {
                            type = "string",
                            description = "Workspace-relative file path, for example src/WidgetClient.cs."
                        }
                    },
                    required = new[] { "intention", "path" }
                }
            )
        );

    private static ToolDefinition BuildSelectRememberDefinition()
        => new(
            SelectRememberToolName,
            "Select the line ranges from the just-viewed file that should remain in the main timeline.",
            JsonSerializer.SerializeToElement(
                new {
                    type = "object",
                    properties = new {
                        path = new {
                            type = "string",
                            description = "The same path as the latest view_file call."
                        },
                        intention = new {
                            type = "string",
                            description = "The same intention as the latest view_file call."
                        },
                        ranges = new {
                            type = "array",
                            items = new { type = "string" },
                            description = "Closed virtual line ranges such as 3, 8-12. Select only relevant lines."
                        },
                        summary = new {
                            type = "string",
                            description = "Short summary of why these lines were selected."
                        },
                        notes = new {
                            type = "string",
                            description = "Optional notes about omitted content or how to use the selected lines."
                        }
                    },
                    required = new[] { "path", "intention", "ranges", "summary" }
                }
            )
        );

    private static string BuildPhase2SystemPrompt()
        => """
        你是一个代码阅读助手。你需要回答用户关于 workspace 中 WidgetClient 用法的问题。
        你只能通过 view_file 工具查看文件内容。view_file 的 intention 参数必须说明你要找什么。
        每轮最多调用一个 view_file 工具；看完一个文件并得到结果后，再决定是否需要查看下一个文件。
        如果已经掌握足够信息，请直接回答并给出简短 C# 示例代码。
        """;

    private static string BuildPhase2UserPrompt()
        => $"""
        {Phase2FakeScenario.TaskText}

        Workspace files:
        {string.Join("\n", Phase2FakeScenario.RecommendedFiles.Select(static path => "- " + path))}
        """;

    private static string BuildIntentionFor(string path)
        => path switch {
            "README.md" => "了解 workspace 中哪些文件与 WidgetClient 的 timeout 和 retry policy 配置有关。",
            "src/WidgetClient.cs" => "了解 WidgetClient 构造函数如何接收配置，以及请求时如何使用 timeout 和 retry policy。",
            "src/WidgetOptions.cs" => "了解 WidgetOptions 中 timeout 的属性名、类型和默认值。",
            "src/WidgetRetryPolicy.cs" => "了解 WidgetRetryPolicy 中 retry count 和 delay 的属性名、类型和默认值。",
            _ => "判断这个文件是否包含回答 WidgetClient timeout 和 retry policy 用法所需的信息。"
        };

    private static string GetRequiredStringArgument(JsonElement arguments, string name) {
        var value = GetOptionalStringArgument(arguments, name);
        if (string.IsNullOrWhiteSpace(value)) { throw new InvalidOperationException($"Tool argument '{name}' is required."); }

        return value;
    }

    private static string? GetOptionalStringArgument(JsonElement arguments, string name) {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) { return null; }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static IReadOnlyList<string> GetStringArrayArgument(JsonElement arguments, string name) {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) { throw new InvalidOperationException($"Tool argument '{name}' must be an array of strings."); }

        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item!)
            .ToArray();
    }

    private static bool IsLatestViewSelection(NumberedFileView view, string selectedPath, string selectedIntention)
        => string.Equals(NormalizeToolPath(selectedPath), view.Path, StringComparison.Ordinal) &&
           string.Equals(selectedIntention.Trim(), view.Intention, StringComparison.Ordinal);

    private static string NormalizeToolPath(string path)
        => path.Trim().Replace('\\', '/');

    private static string Preview(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "\n... <truncated>";

    private sealed record ViewWizardOutcome(
        List<ChatHistoryMessage> MainTimeline,
        string Path,
        string Outcome,
        int FullLineCount,
        string SelectedRanges
    );

    private sealed class RunLog : IDisposable {
        private static readonly JsonSerializerOptions JsonLineOptions = new() {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

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
                },
                JsonLineOptions
            );
            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Dispose() {
            _writer.Dispose();
        }
    }
}
