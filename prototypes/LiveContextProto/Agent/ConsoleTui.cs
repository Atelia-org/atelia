using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.Provider;
using Atelia.LiveContextProto.State.History;
using Atelia.LiveContextProto.Context;
using Atelia.LiveContextProto.Profile;

namespace Atelia.LiveContextProto.Agent;

internal sealed class ConsoleTui {
    private readonly LlmAgent _agent;
    private readonly LlmProfile _defaultProfile;
    private readonly TextReader _input;
    private readonly TextWriter _output;

    public ConsoleTui(LlmAgent agent, LlmProfile defaultProfile, TextReader? input = null, TextWriter? output = null) {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _defaultProfile = defaultProfile ?? throw new ArgumentNullException(nameof(defaultProfile));
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

            if (string.Equals(line, "/reset", StringComparison.OrdinalIgnoreCase)) {
                _agent.Reset();
                _output.WriteLine("[state] 已清空历史。");
                continue;
            }

            if (string.IsNullOrWhiteSpace(line)) { continue; }

            ProcessUserInput(line);
        }
    }

    private void PrintIntro() {
        _output.WriteLine("=== LiveContextProto Anthropic Runner ===");
        _output.WriteLine("命令：/history 查看上下文，/reset 清空，/notebook view|set，/exit 退出。");
        _output.WriteLine();
        _output.WriteLine("输入任意文本将调用当前配置的模型，并把输出回写到历史。");
        _output.WriteLine();
        _output.WriteLine($"[system] {_agent.SystemInstruction}");
        _output.WriteLine();

        DebugUtil.Print("History", "ConsoleTui intro displayed");
    }

    private void PrintHistory() {
        WriteSystemInstructionBlock();

        var context = _agent.RenderLiveContext();
        if (context.Count == 0) {
            _output.WriteLine("[history] 暂无上下文消息。");
            return;
        }

        _output.WriteLine("[history] 当前上下文消息列表：");
        for (var index = 0; index < context.Count; index++) {
            var message = context[index];

            _output.WriteLine($"  [{index}] {message.Timestamp:O} :: {message.Role}");

            switch (message) {
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

            WriteMetadataBlock(message.Metadata);
        }
    }

    private void ProcessUserInput(string text) {
        DebugUtil.Print("History", $"[ConsoleTui] Received user input length={text.Length}");
        _agent.EnqueueUserInput(text);
        _output.WriteLine($"[state] 已记录用户输入（长度 {text.Length}）。");

        DrainAgentUntilIdle();
    }

    private void PrintModelOutput(ModelOutputEntry output) {
        _output.WriteLine("[assistant] 输出如下：");
        WriteOutputContents(output);
        PrintTokenUsage(output);
        WriteMetadataBlock(output.Metadata);
    }

    private void PrintToolResults(ToolResultsEntry entry) {
        var toolMessage = new ToolResultsMessage(entry, LevelOfDetail.BasicAndExtra);
        WriteToolResults(toolMessage);
        PrintTokenUsage(toolMessage);
        WriteMetadataBlock(toolMessage.Metadata);
    }

    private void PrintTokenUsage(IContextMessage message) {
        if (!message.Metadata.TryGetValue("token_usage", out var value)) { return; }
        if (value is not TokenUsage usage) { return; }

        _output.WriteLine(
            $"      [usage] prompt={usage.PromptTokens}, completion={usage.CompletionTokens}, cached={(usage.CachedPromptTokens?.ToString() ?? "0")}"
        );
    }

    private void WriteSystemInstructionBlock() {
        _output.WriteLine("[history] 当前 System Instruction：");
        var instruction = _agent.SystemInstruction;
        var lines = instruction.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        foreach (var line in lines) {
            _output.WriteLine($"      {line}");
        }
    }

    private void WriteInputSections(IReadOnlyList<KeyValuePair<string, string>> sections) {
        var filtered = sections.WithoutLiveScreen(out var liveScreen);

        foreach (var (name, content) in filtered) {
            _output.WriteLine($"      [input:{name}] {content}");
        }

        if (!string.IsNullOrWhiteSpace(liveScreen)) {
            WriteLiveScreenBlock(liveScreen);
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
        string? liveScreen = null;
        foreach (var result in tools.Results) {
            var sections = result.Result.WithoutLiveScreen(out var resultLiveScreen);
            if (!string.IsNullOrWhiteSpace(resultLiveScreen)) {
                liveScreen ??= resultLiveScreen;
            }

            var content = LevelOfDetailSections.ToPlainText(sections);
            _output.WriteLine($"        - {result.ToolName} ({result.ToolCallId}) => {result.Status}: {content}");
        }

        if (!string.IsNullOrWhiteSpace(tools.ExecuteError)) {
            _output.WriteLine($"      [tool-error] {tools.ExecuteError}");
        }

        if (!string.IsNullOrWhiteSpace(liveScreen)) {
            WriteLiveScreenBlock(liveScreen);
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

    private void WriteLiveScreenBlock(string liveScreen) {
        _output.WriteLine("      [live-screen]");
        foreach (var line in liveScreen.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)) {
            _output.WriteLine($"        {line}");
        }
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

        if (argument.StartsWith("set", StringComparison.OrdinalIgnoreCase)) {
            var newContent = argument.Length > 3
                ? argument[3..].TrimStart()
                : string.Empty;

            _agent.UpdateMemoryNotebook(newContent);
            _output.WriteLine("[notebook] 记忆笔记已更新。");
            return;
        }

        _output.WriteLine("用法: /notebook [view|set <内容>]");
    }

    private void PrintNotebookSnapshot() {
        var snapshot = _agent.MemoryNotebookSnapshot;
        _output.WriteLine("[notebook] 当前内容如下：");
        _output.WriteLine(snapshot);
        _output.WriteLine($"[notebook] 长度: {snapshot.Length}");
    }

    private void DrainAgentUntilIdle() {
        while (true) {
            AgentStepResult step;
            try {
                step = _agent.DoStepAsync(_defaultProfile).GetAwaiter().GetResult();
            }
            catch (Exception ex) {
                _output.WriteLine($"[error] 模型调用失败：{ex.Message}");
                DebugUtil.Print("History", $"[ConsoleTui] DoStep failed profile={_defaultProfile.Name} error={ex.Message}");
                break;
            }

            if (!step.ProgressMade) {
                if (step.BlockedOnInput) { break; }

                _output.WriteLine($"[warn] 状态机在 {step.StateAfter} 状态未能继续推进。");
                break;
            }

            if (step.Output is not null) {
                PrintModelOutput(step.Output);
            }

            if (step.ToolResults is not null) {
                PrintToolResults(step.ToolResults);
            }

            if (step.StateAfter == AgentRunState.WaitingInput) { break; }
        }
    }
}
