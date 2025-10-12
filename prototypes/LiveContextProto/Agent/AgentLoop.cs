using System;
using System.Collections.Generic;
using System.Threading;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.Provider;
using Atelia.LiveContextProto.State;
using Atelia.LiveContextProto.State.History;

namespace Atelia.LiveContextProto.Agent;

internal sealed class AgentLoop {
    private readonly AgentState _state;
    private readonly AgentOrchestrator _orchestrator;
    private readonly TextReader _input;
    private readonly TextWriter _output;

    public AgentLoop(AgentState state, AgentOrchestrator orchestrator, TextReader? input = null, TextWriter? output = null) {
        _state = state;
        _orchestrator = orchestrator;
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
        _output.WriteLine("=== LiveContextProto Phase 2 (Provider Stub & Router) ===");
        _output.WriteLine("命令：/history 查看上下文，/reset 清空，/notebook view|set|clear，/stub <script> [文本]，/exit 退出。");
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

        if (result.ToolResults is not null) {
            WriteToolResults(result.ToolResults);
            PrintTokenUsage(result.ToolResults);
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

    private void HandleToolCommand(string commandLine) {
        var payload = commandLine.Length <= 5
            ? string.Empty
            : commandLine[5..];

        var argument = payload.Trim();

        if (argument.Length == 0 || argument.Equals("sample", StringComparison.OrdinalIgnoreCase)) {
            AppendMockToolResults(includeError: false);
            return;
        }

        if (argument.Equals("fail", StringComparison.OrdinalIgnoreCase)) {
            AppendMockToolResults(includeError: true);
            return;
        }

        _output.WriteLine("用法: /tool [sample|fail]");
    }

    private void AppendMockToolResults(bool includeError) {
        ToolResultsEntry entry;

        if (includeError) {
            entry = new ToolResultsEntry(Array.Empty<ToolCallResult>(), "模拟工具执行失败：超时 3.5s");
        }
        else {
            var results = new[] {
                new ToolCallResult(
                    "memory.search",
                    "toolcall-demo-1",
                    ToolExecutionStatus.Success,
                    "找到 2 条相关记忆片段",
                    TimeSpan.FromMilliseconds(180)
                ),
                new ToolCallResult(
                    "planner.summarize",
                    "toolcall-demo-2",
                    ToolExecutionStatus.Skipped,
                    "跳过：策略判断无需调用",
                    TimeSpan.FromMilliseconds(25)
                )
            };

            entry = new ToolResultsEntry(results, null);
        }

        _state.AppendToolResults(entry);
        _output.WriteLine(
            includeError
            ? "[tool] 已记录模拟失败的工具结果。"
            : "[tool] 已记录模拟工具结果。"
        );
    }

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
