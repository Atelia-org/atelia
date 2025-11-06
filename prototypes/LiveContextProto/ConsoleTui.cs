using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Atelia.Diagnostics;
using Atelia.Agent;
using Atelia.Agent.Core;
using Atelia.Agent.Core.History;
using Atelia.Completion.Abstractions;

namespace Atelia.LiveContextProto;

internal sealed class ConsoleTui {
    private readonly CharacterAgent _agent;
    private readonly LlmProfile _defaultProfile;
    private readonly TextReader _input;
    private readonly TextWriter _output;

    public ConsoleTui(CharacterAgent agent, LlmProfile defaultProfile, TextReader? input = null, TextWriter? output = null) {
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

            if (string.IsNullOrWhiteSpace(line)) { continue; }

            var notificationText = $"你收到消息:\n``````\n{line}\n``````";
            _agent.AppendNotification(notificationText);

            DrainAgentUntilIdle();
        }
    }

    private void PrintIntro() {
        _output.WriteLine("=== LiveContextProto Anthropic Runner ===");
        _output.WriteLine("命令：/history 查看上下文，/notebook view|set，/exit 退出。");
        _output.WriteLine();
        _output.WriteLine("输入任意文本将调用当前配置的模型，并把输出回写到历史。");
        _output.WriteLine();
        _output.WriteLine($"[system] {_agent.SystemPrompt}");
        _output.WriteLine();

        DebugUtil.Print("History", "ConsoleTui intro displayed");
    }

    private void PrintHistory() {
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

            if (step.Output is { } outputEntry) {
                Console.WriteLine(outputEntry.Content);
                foreach (var call in outputEntry.ToolCalls) {
                    Console.WriteLine($"  [ToolCall] {call.ToolCallId} {call.ToolName}");
                }
            }

            if (step.StateAfter == AgentRunState.WaitingInput) { break; }
        }
    }
}
