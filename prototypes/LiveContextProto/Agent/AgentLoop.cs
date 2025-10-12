using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.Provider;
using Atelia.LiveContextProto.State;
using Atelia.LiveContextProto.State.History;
using Atelia.LiveContextProto.Tools;

namespace Atelia.LiveContextProto.Agent;

internal sealed class AgentLoop {
    private readonly AgentState _state;
    private readonly AgentOrchestrator _orchestrator;
    private readonly ToolExecutor _toolExecutor;
    private readonly TextReader _input;
    private readonly TextWriter _output;

    public AgentLoop(AgentState state, AgentOrchestrator orchestrator, ToolExecutor toolExecutor, TextReader? input = null, TextWriter? output = null) {
        _state = state;
        _orchestrator = orchestrator;
        _toolExecutor = toolExecutor;
        _input = input ?? Console.In;
        _output = output ?? Console.Out;
    }

    public void Run() {
        PrintIntro();

        while (true) {
            _output.Write("user> ");
            var line = _input.ReadLine();
            if (line is null) {
                DebugUtil.Print("History", "Input stream ended");
                break;
            }

            if (string.Equals(line, "/exit", StringComparison.OrdinalIgnoreCase)) {
                _output.WriteLine("再见！");
                break;
            }

            if (string.Equals(line, "/history", StringComparison.OrdinalIgnoreCase)) {
                PrintHistory();
                continue;
            }

            if (line.StartsWith("/notebook", StringComparison.OrdinalIgnoreCase)) {
                HandleNotebookCommand(line);
                continue;
            }

            if (line.StartsWith("/tool", StringComparison.OrdinalIgnoreCase)) {
                HandleToolCommand(line);
                continue;
            }

            if (line.StartsWith("/demo", StringComparison.OrdinalIgnoreCase)) {
                HandleDemoCommand(line);
                continue;
            }

            if (line.StartsWith("/stub", StringComparison.OrdinalIgnoreCase)) {
                HandleStubCommand(line);
                continue;
            }

            if (line.StartsWith("/liveinfo", StringComparison.OrdinalIgnoreCase)) {
                HandleLiveInfoCommand(line);
                continue;
            }

            if (string.Equals(line, "/reset", StringComparison.OrdinalIgnoreCase)) {
                _state.Reset();
                _output.WriteLine("[state] 已清空历史。");
                continue;
            }

            if (string.IsNullOrWhiteSpace(line)) { continue; }

            AppendUserInput(line);
        }
    }

    private void PrintIntro() {
        _output.WriteLine("=== LiveContextProto Phase 4 (Tools & Diagnostics) ===");
        _output.WriteLine("命令：/history 查看上下文，/reset 清空，/notebook view|set|clear，/liveinfo list|set|clear，/stub <script> [文本]，/tool sample|fail，/exit 退出。");
        _output.WriteLine();
        _output.WriteLine("输入任意文本将通过 Stub Provider 触发一次模型调用，你也可用 /stub 指定脚本。");
        _output.WriteLine();
        _output.WriteLine($"[system] {_state.SystemInstruction}");
        _output.WriteLine();

        DebugUtil.Print("History", "AgentLoop intro displayed");
    }

    private void PrintHistory() {
        var context = _state.RenderLiveContext();
        if (context.Count == 0) {
            _output.WriteLine("[history] 暂无上下文消息。");
            return;
        }

        _output.WriteLine("[history] 当前上下文消息列表：");
        for (var index = 0; index < context.Count; index++) {
            var message = context[index];
            var displayMessage = message;
            string? liveScreen = null;

            if (message is ILiveScreenCarrier carrier) {
                liveScreen = carrier.LiveScreen;
                displayMessage = carrier.InnerMessage;
            }

            _output.WriteLine($"  [{index}] {displayMessage.Timestamp:O} :: {displayMessage.Role}");

            switch (displayMessage) {
                case ISystemMessage system:
                    _output.WriteLine($"      [system] {system.Instruction}");
                    break;
                case IModelInputMessage input:
                    WriteInputSections(input.ContentSections);
                    break;
                case IModelOutputMessage output:
                    WriteOutputContents(output);
                    break;
                case IToolResultsMessage tools:
                    WriteToolResults(tools);
                    break;
            }

            if (!string.IsNullOrWhiteSpace(liveScreen)) {
                _output.WriteLine("      [live-screen]");
                foreach (var line in liveScreen.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)) {
                    _output.WriteLine($"        {line}");
                }
            }

            WriteMetadataBlock(displayMessage.Metadata);
        }
    }

    private void AppendUserInput(string text, string? stubScript = null) {
        var sections = new List<KeyValuePair<string, string>>
        {
            new("default", text)
        };

        _state.AppendModelInput(new ModelInputEntry(sections));
        _output.WriteLine($"[state] 已记录用户输入（长度 {text.Length}）。");

        InvokeStubProvider(stubScript);
    }

    private void InvokeStubProvider(string? stubScript) {
        try {
            var options = new ProviderInvocationOptions(ProviderRouter.DefaultStubStrategy, stubScript);
            var result = _orchestrator.InvokeAsync(options, CancellationToken.None).GetAwaiter().GetResult();
            PrintInvocationResult(result);
        }
        catch (Exception ex) {
            DebugUtil.Print("Provider", $"Stub provider invocation failed: {ex}");
            _output.WriteLine($"[error] 模拟模型调用失败：{ex.Message}");
        }
    }

    private void PrintInvocationResult(AgentInvocationResult result) {
        _output.WriteLine("[assistant] (stub) 输出如下：");
        WriteOutputContents(result.Output);
        PrintTokenUsage(result.Output);
        WriteMetadataBlock(result.Output.Metadata);

        if (result.ToolResults is not null) {
            WriteToolResults(result.ToolResults);
            PrintTokenUsage(result.ToolResults);
            WriteMetadataBlock(result.ToolResults.Metadata);
        }
    }

    private void PrintTokenUsage(IContextMessage message) {
        if (!message.Metadata.TryGetValue("token_usage", out var value)) { return; }
        if (value is not TokenUsage usage) { return; }

        _output.WriteLine(
            $"      [usage] prompt={usage.PromptTokens}, completion={usage.CompletionTokens}, cached={(usage.CachedPromptTokens?.ToString() ?? "0")}"
        );
    }

    private void WriteInputSections(IReadOnlyList<KeyValuePair<string, string>> sections) {
        foreach (var (name, content) in sections) {
            _output.WriteLine($"      [input:{name}] {content}");
        }
    }

    private void WriteOutputContents(IModelOutputMessage output) {
        for (var i = 0; i < output.Contents.Count; i++) {
            _output.WriteLine($"      [assistant:{i}] {output.Contents[i]}");
        }

        if (output.ToolCalls.Count > 0) {
            _output.WriteLine("      [tool-calls]");
            foreach (var call in output.ToolCalls) {
                _output.WriteLine($"        - {call.ToolName} ({call.ToolCallId}) :: args={call.RawArguments}");
            }
        }
    }

    private void WriteToolResults(IToolResultsMessage tools) {
        if (tools.Results.Count == 0) {
            _output.WriteLine($"      [tool-results] (无结果) 错误: {tools.ExecuteError}");
            return;
        }

        _output.WriteLine("      [tool-results]");
        foreach (var result in tools.Results) {
            _output.WriteLine($"        - {result.ToolName} ({result.ToolCallId}) => {result.Status}: {result.Result}");
        }
    }

    private void WriteMetadataBlock(IReadOnlyDictionary<string, object?> metadata) {
        var entries = metadata
            .Where(static pair => !string.Equals(pair.Key, "token_usage", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (entries.Length == 0) { return; }

        _output.WriteLine("      [metadata]");
        foreach (var entry in entries) {
            WriteMetadataValue(entry.Key, entry.Value, "        ");
        }
    }

    private void WriteMetadataValue(string key, object? value, string indent) {
        switch (value) {
            case null:
                _output.WriteLine($"{indent}- {key}: (null)");
                break;
            case TokenUsage usage:
                _output.WriteLine($"{indent}- {key}: prompt={usage.PromptTokens}, completion={usage.CompletionTokens}, cached={(usage.CachedPromptTokens?.ToString(CultureInfo.InvariantCulture) ?? "0")}");
                break;
            case IReadOnlyDictionary<string, object?> nested when nested.Count > 0:
                _output.WriteLine($"{indent}- {key}:");
                WriteMetadataNested(nested, indent + "  ");
                break;
            case IReadOnlyDictionary<string, object?>:
                _output.WriteLine($"{indent}- {key}: {{}}");
                break;
            case IEnumerable sequence when value is not string:
                WriteMetadataSequence(key, sequence, indent);
                break;
            default:
                _output.WriteLine($"{indent}- {key}: {FormatMetadataPrimitive(value)}");
                break;
        }
    }

    private void WriteMetadataNested(IReadOnlyDictionary<string, object?> metadata, string indent) {
        foreach (var entry in metadata.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)) {
            WriteMetadataValue(entry.Key, entry.Value, indent);
        }
    }

    private void WriteMetadataSequence(string key, IEnumerable sequence, string indent) {
        var items = new List<string>();
        foreach (var item in sequence) {
            items.Add(FormatMetadataPrimitive(item));
        }

        if (items.Count == 0) {
            _output.WriteLine($"{indent}- {key}: []");
            return;
        }

        _output.WriteLine($"{indent}- {key}: [{string.Join(", ", items)}]");
    }

    private static string FormatMetadataPrimitive(object? value) {
        return value switch {
            null => "(null)",
            string s => s,
            bool b => b ? "true" : "false",
            double d => d.ToString("0.###", CultureInfo.InvariantCulture),
            float f => f.ToString("0.###", CultureInfo.InvariantCulture),
            decimal m => m.ToString("0.###", CultureInfo.InvariantCulture),
            TimeSpan span => $"{span.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms",
            DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            TokenUsage usage => $"prompt={usage.PromptTokens}, completion={usage.CompletionTokens}, cached={(usage.CachedPromptTokens?.ToString(CultureInfo.InvariantCulture) ?? "0")}",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "(null)"
        };
    }

    private void HandleNotebookCommand(string commandLine) {
        var payload = commandLine.Length <= 9
            ? string.Empty
            : commandLine[9..];

        var argument = payload.Trim();

        if (argument.Length == 0 || argument.Equals("view", StringComparison.OrdinalIgnoreCase)) {
            PrintNotebookSnapshot();
            return;
        }

        if (argument.Equals("clear", StringComparison.OrdinalIgnoreCase)) {
            _state.UpdateMemoryNotebook(null);
            _output.WriteLine("[notebook] 已清空记忆笔记。");
            return;
        }

        if (argument.StartsWith("set", StringComparison.OrdinalIgnoreCase)) {
            var newContent = argument.Length > 3
                ? argument[3..].TrimStart()
                : string.Empty;

            _state.UpdateMemoryNotebook(newContent);
            _output.WriteLine("[notebook] 记忆笔记已更新。");
            return;
        }

        _output.WriteLine("用法: /notebook [view|set <内容>|clear]");
    }

    private void PrintNotebookSnapshot() {
        var snapshot = _state.MemoryNotebookSnapshot;
        _output.WriteLine("[notebook] 当前内容如下：");
        _output.WriteLine(snapshot);
        _output.WriteLine($"[notebook] 长度: {snapshot.Length}");
    }

    private void HandleLiveInfoCommand(string commandLine) {
        var payload = commandLine.Length <= 9
            ? string.Empty
            : commandLine[9..];

        var argument = payload.Trim();

        if (argument.Length == 0 || argument.Equals("list", StringComparison.OrdinalIgnoreCase)) {
            PrintLiveInfoSections();
            return;
        }

        if (argument.StartsWith("set", StringComparison.OrdinalIgnoreCase)) {
            var remainder = argument.Length > 3 ? argument[3..].TrimStart() : string.Empty;
            var splitIndex = remainder.IndexOf(' ');

            if (splitIndex <= 0 || splitIndex == remainder.Length - 1) {
                _output.WriteLine("用法: /liveinfo set <节名称> <内容>");
                return;
            }

            var section = remainder[..splitIndex].Trim();
            var content = remainder[(splitIndex + 1)..];

            if (string.IsNullOrWhiteSpace(section)) {
                _output.WriteLine("节名称不能为空。");
                return;
            }

            _state.UpdateLiveInfoSection(section, content);
            _output.WriteLine($"[liveinfo] 已更新节 '{section}'。");
            return;
        }

        if (argument.StartsWith("clear", StringComparison.OrdinalIgnoreCase)) {
            var remainder = argument.Length > 5 ? argument[5..].TrimStart() : string.Empty;
            if (string.IsNullOrWhiteSpace(remainder)) {
                _output.WriteLine("用法: /liveinfo clear <节名称>");
                return;
            }

            _state.UpdateLiveInfoSection(remainder.Trim(), null);
            _output.WriteLine($"[liveinfo] 已移除节 '{remainder.Trim()}'。");
            return;
        }

        _output.WriteLine("用法: /liveinfo [list|set <节名称> <内容>|clear <节名称>]");
    }

    private void PrintLiveInfoSections() {
        _output.WriteLine("[liveinfo] 当前节一览：");

        var sections = _state.LiveInfoSections
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sections.Count == 0) {
            _output.WriteLine("  (暂无附加 LiveInfo 节)");
        }
        else {
            foreach (var (key, value) in sections) {
                _output.WriteLine($"  - {key} (长度 {value.Length})");
            }
        }

        _output.WriteLine($"  * Memory Notebook: 长度 {_state.MemoryNotebookSnapshot.Length}");
    }

    private void HandleToolCommand(string commandLine) {
        var payload = commandLine.Length <= 5
            ? string.Empty
            : commandLine[5..];

        var argument = payload.Trim();

        if (argument.Length == 0 || argument.Equals("sample", StringComparison.OrdinalIgnoreCase)) {
            ExecuteInteractiveTool(includeError: false);
            return;
        }

        if (argument.Equals("fail", StringComparison.OrdinalIgnoreCase)) {
            ExecuteInteractiveTool(includeError: true);
            return;
        }

        _output.WriteLine("用法: /tool [sample|fail]");
    }

    private void ExecuteInteractiveTool(bool includeError) {
        var requests = includeError
            ? new[] {
                new ToolCallRequest(
                    "diagnostics.raise",
                    GenerateConsoleToolCallId(),
                    "{\"reason\":\"Console trigger\"}",
                    new Dictionary<string, string> { { "reason", "Console trigger" } },
                    null
                )
            }
            : new[] {
                new ToolCallRequest(
                    "memory.search",
                    GenerateConsoleToolCallId(),
                    "{\"query\":\"LiveContextProto 核心阶段\"}",
                    new Dictionary<string, string> { { "query", "LiveContextProto 核心阶段" } },
                    null
                )
            };

        var records = _toolExecutor.ExecuteBatchAsync(requests, CancellationToken.None).GetAwaiter().GetResult();
        if (records.Count == 0) {
            _output.WriteLine("[tool] 工具执行器未返回结果。");
            return;
        }

        var results = records.Select(static record => record.CallResult).ToArray();
        var failure = results.FirstOrDefault(static result => result.Status == ToolExecutionStatus.Failed);
        var entry = new ToolResultsEntry(results, failure?.Result) {
            Metadata = ToolResultMetadataHelper.PopulateSummary(records, ImmutableDictionary<string, object?>.Empty)
        };

        var appended = _state.AppendToolResults(entry);

        _output.WriteLine(
            failure is null
                ? $"[tool] 已执行 {results.Length} 个工具调用。"
                : $"[tool] 工具执行失败：{failure.Result}"
        );

        WriteToolResults(appended);
        PrintTokenUsage(appended);
        WriteMetadataBlock(appended.Metadata);
    }

    private static string GenerateConsoleToolCallId()
        => $"console-{Guid.NewGuid():N}";

    private void HandleDemoCommand(string commandLine) {
        var payload = commandLine.Length <= 5
            ? string.Empty
            : commandLine[5..];

        var argument = payload.Trim();

        if (argument.Length == 0 || argument.Equals("conversation", StringComparison.OrdinalIgnoreCase)) {
            RunDemoConversation();
            return;
        }

        _output.WriteLine("用法: /demo [conversation]");
    }

    private void RunDemoConversation() {
        _output.WriteLine("[demo] 正在重置状态并注入示例历史……");

        _state.Reset();
        _state.UpdateMemoryNotebook("- Phase 1 MVP 已完成\n- 准备进入 Provider Stub 阶段\n- 记得补充工具聚合测试");

        AppendUserInput("我们当前的 LiveContextProto 处于什么阶段？");
        AppendUserInput("下一步需要验证哪些能力？");

        _output.WriteLine("[demo] 示例构造完毕，可使用 /history 查看上下文，或 /notebook view 查看记忆笔记。");
    }

    private void HandleStubCommand(string commandLine) {
        // 形态：/stub <scriptName> [message text]
        var payload = commandLine.Length <= 5 ? string.Empty : commandLine[5..];
        var trimmed = payload.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) {
            _output.WriteLine("用法: /stub <scriptName> [文本] ；示例：/stub default 现在是什么阶段?");
            return;
        }

        var firstSpace = trimmed.IndexOf(' ');
        if (firstSpace < 0) {
            // 仅指定脚本，不追加新输入，直接用当前上下文触发一次调用
            InvokeStubProvider(trimmed);
            return;
        }

        var script = trimmed.Substring(0, firstSpace);
        var message = trimmed.Substring(firstSpace + 1).Trim();
        if (string.IsNullOrWhiteSpace(message)) {
            InvokeStubProvider(script);
            return;
        }

        AppendUserInput(message, script);
    }
}
