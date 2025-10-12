using System.Collections.Generic;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.State;
using Atelia.LiveContextProto.State.History;

namespace Atelia.LiveContextProto.Agent;

internal sealed class AgentLoop {
    private readonly AgentState _state;
    private readonly TextReader _input;
    private readonly TextWriter _output;

    public AgentLoop(AgentState state, TextReader? input = null, TextWriter? output = null) {
        _state = state;
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
        _output.WriteLine("=== LiveContextProto Phase 0 (环境与骨架) ===");
        _output.WriteLine("命令：/history 查看占位历史，/reset 清空，/exit 退出。");
        _output.WriteLine();
        _output.WriteLine("输入任意文本以体验占位 AgentState（当前仅回显）。");
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
            _output.WriteLine($"  [{index}] {message.Timestamp:O} :: {message.Role}");

            switch (message) {
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
        }
    }

    private void AppendUserInput(string text) {
        var sections = new List<KeyValuePair<string, string>>
        {
            new("default", text)
        };

        _state.AppendModelInput(new ModelInputEntry(sections));
        _output.WriteLine($"[state] 已记录用户输入（长度 {text.Length}）。");
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
}
