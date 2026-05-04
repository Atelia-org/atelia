using System;
using System.Collections.Generic;
using Atelia.Agent;
using Atelia.Agent.Core;
using Atelia.Agent.Core.History;
using Atelia.Completion.Abstractions;
using Atelia.Diagnostics;
using Spectre.Console;

namespace Atelia.LiveContextProto;

internal sealed class ConsoleTui {
    private readonly CharacterAgent _agent;
    private readonly LlmProfile _defaultProfile;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly IAnsiConsole _console;

    public ConsoleTui(CharacterAgent agent, LlmProfile defaultProfile, TextReader? input = null, TextWriter? output = null) {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _defaultProfile = defaultProfile ?? throw new ArgumentNullException(nameof(defaultProfile));
        _input = input ?? System.Console.In;
        _output = output ?? System.Console.Out;
        _console = AnsiConsole.Create(new AnsiConsoleSettings {
            Out = new AnsiConsoleOutput(_output)
        });
    }

    public void Run() {
        PrintIntro();

        while (true) {
            _output.Write("user> ");
            var line = _input.ReadLine();
            if (line is null) {
                DebugUtil.Info("History", "Input stream ended");
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
        var panel = new Panel(
            "[bold]LiveContextProto Agent Runner[/]\n\n" +
            "命令：/history 查看上下文，/notebook view|set，/exit 退出。\n" +
            "输入任意文本将调用当前配置的模型，并把输出回写到历史。\n\n" +
            $"[dim]{Markup.Escape(_agent.SystemPrompt)}[/]"
        ) {
            Border = BoxBorder.Rounded,
            Header = new PanelHeader("Atelia"),
        };
        _console.Write(panel);
        _console.WriteLine();

        DebugUtil.Info("History", "ConsoleTui intro displayed");
    }

    private void PrintHistory() {
        var history = _agent.State.RecentHistory;
        if (history.Count == 0) {
            _console.MarkupLine("[dim](暂无历史记录)[/]");
            return;
        }

        var table = new Table()
            .AddColumn(new TableColumn("#").RightAligned())
            .AddColumn("Kind")
            .AddColumn("Content")
            .RoundedBorder();

        for (var i = 0; i < history.Count; i++) {
            var entry = history[i];
            var kind = FormatKind(entry.Kind);
            var content = FormatHistoryEntry(entry);
            table.AddRow(i.ToString(), kind, Markup.Escape(content));
        }

        _console.Write(table);
    }

    private static string FormatKind(HistoryEntryKind kind) => kind switch {
        HistoryEntryKind.Action => "Action",
        HistoryEntryKind.Observation => "Obs",
        HistoryEntryKind.ToolResults => "Tool",
        HistoryEntryKind.Recap => "Recap",
        _ => kind.ToString()
    };

    private static string FormatHistoryEntry(HistoryEntry entry) {
        var text = entry switch {
            ActionEntry action => action.Message.GetFlattenedText(),
            ToolResultsEntry tr => $"{tr.Results.Count} tool results",
            ObservationEntry obs => obs.GetMessage(LevelOfDetail.Basic, null)?.Content ?? string.Empty,
            RecapEntry recap => recap.Content,
            _ => string.Empty
        };

        const int maxLen = 120;
        return text.Length <= maxLen ? text : text[..maxLen] + "...";
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
            _console.MarkupLine("[dim][[notebook]] 记忆笔记已更新。[/]");
            return;
        }

        _console.MarkupLine("[dim]用法: /notebook [[view|set <内容>]][/]");
    }

    private void PrintNotebookSnapshot() {
        var snapshot = _agent.MemoryNotebookSnapshot;
        var panel = new Panel(Markup.Escape(snapshot))
        {
            Border = BoxBorder.Rounded,
            Header = new PanelHeader("Memory Notebook"),
        };
        _console.Write(panel);
        _console.MarkupLine($"[dim][[notebook]] 长度: {snapshot.Length}[/]");
    }

    private void DrainAgentUntilIdle() {
        while (true) {
            var display = new StreamingDisplay(_output);
            var observer = display.CreateObserver();

            AgentStepResult step;
            try {
                step = _agent.DoStepAsync(_defaultProfile, observer).GetAwaiter().GetResult();
            }
            catch (Exception ex) {
                display.FinishWithError(ex.Message);
                DebugUtil.Error("History", $"[ConsoleTui] DoStep failed profile={_defaultProfile.Name} error={ex.Message}", ex);
                break;
            }

            display.Finish();

            if (!step.ProgressMade) {
                if (step.BlockedOnInput) { break; }

                _console.MarkupLine($"[yellow][[warn]] 状态机在 {step.StateAfter} 状态未能继续推进。[/]");
                break;
            }

            if (step.ToolResults is { } toolResults) {
                foreach (var result in toolResults.Results) {
                    _console.MarkupLine($"  [dim][[tool]] {Markup.Escape(result.ToolName)}#{Markup.Escape(result.ToolCallId)}[/]");
                }

                if (!string.IsNullOrWhiteSpace(toolResults.ExecuteError)) {
                    _console.MarkupLine($"  [red][[tool-error]] {Markup.Escape(toolResults.ExecuteError)}[/]");
                }
            }

            if (step.StateAfter == AgentRunState.WaitingInput) { break; }
        }
    }

    /// <summary>
    /// 管理单次 Agent step 的流式输出显示，实现打字机效果。
    /// 参考 TrpgSimulation.TurnConsoleStreamer 的设计，通过 CompletionStreamObserver
    /// 事件实时将 thinking/reasoning 内容和正文输出到控制台。
    /// </summary>
    private sealed class StreamingDisplay {
        private readonly TextWriter _output;
        private readonly bool _useConsoleColors;
        private bool _lineStart = true;
        private bool _hasOutput;
        private bool _thinkingActive;
        private bool _reasoningObserved;

        public StreamingDisplay(TextWriter output) {
            _output = output ?? throw new ArgumentNullException(nameof(output));
            _useConsoleColors = ReferenceEquals(output, System.Console.Out);
        }

        public CompletionStreamObserver CreateObserver() {
            var observer = new CompletionStreamObserver();
            observer.ReceivedThinkingBegin += OnThinkingBegin;
            observer.ReceivedReasoningDelta += OnReasoningDelta;
            observer.ReceivedThinkingEnd += OnThinkingEnd;
            observer.ReceivedTextDelta += OnTextDelta;
            observer.ReceivedToolCall += OnToolCall;
            return observer;
        }

        public void Finish() {
            if (!_hasOutput) { return; }

            EnsureLineBreak();
            _output.WriteLine();
            _lineStart = true;
        }

        public void FinishWithError(string errorMessage) {
            EnsureLineBreak();
            WriteLine($"[error] 模型调用失败：{errorMessage}", ConsoleColor.Red);
            _lineStart = true;
        }

        private void OnThinkingBegin() {
            EnsureLineBreak();
            _thinkingActive = true;
            _reasoningObserved = false;
        }

        private void OnReasoningDelta(string delta) {
            if (string.IsNullOrEmpty(delta)) { return; }

            _thinkingActive = true;
            _reasoningObserved = true;
            WriteChunk(delta, ConsoleColor.DarkGray);
        }

        private void OnThinkingEnd() {
            if (_thinkingActive && !_reasoningObserved) {
                WriteDiagnosticLine("▌思考完成");
            }
            else if (!_lineStart) {
                EnsureLineBreak();
            }

            _thinkingActive = false;
            _reasoningObserved = false;
        }

        private void OnTextDelta(string delta) {
            if (string.IsNullOrEmpty(delta)) { return; }

            if (_thinkingActive && !_lineStart) {
                EnsureLineBreak();
            }

            _thinkingActive = false;
            WriteChunk(delta, ConsoleColor.Gray);
        }

        private void OnToolCall(RawToolCall toolCall) {
            var args = TruncateJson(toolCall.RawArgumentsJson);
            WriteDiagnosticLine($"[tool] {toolCall.ToolName}#{toolCall.ToolCallId}({args})");
        }

        private void WriteDiagnosticLine(string text) {
            EnsureLineBreak();
            Write("  ", ConsoleColor.DarkGray);
            WriteLine(text, ConsoleColor.DarkGray);
            _lineStart = true;
            _hasOutput = true;
        }

        private void WriteChunk(string text, ConsoleColor color) {
            foreach (var ch in text) {
                Write(ch.ToString(), color);
                if (ch == '\n') {
                    _lineStart = true;
                }
                else {
                    _lineStart = false;
                }
            }

            _hasOutput = true;
        }

        private void EnsureLineBreak() {
            if (_lineStart) { return; }

            _output.WriteLine();
            _lineStart = true;
        }

        private void Write(string text, ConsoleColor color) {
            if (!_useConsoleColors) {
                _output.Write(text);
                return;
            }

            var originalColor = System.Console.ForegroundColor;
            System.Console.ForegroundColor = color;
            _output.Write(text);
            System.Console.ForegroundColor = originalColor;
        }

        private void WriteLine(string text, ConsoleColor color) {
            if (!_useConsoleColors) {
                _output.WriteLine(text);
                return;
            }

            var originalColor = System.Console.ForegroundColor;
            System.Console.ForegroundColor = color;
            _output.WriteLine(text);
            System.Console.ForegroundColor = originalColor;
        }

        private static string TruncateJson(string json) {
            const int maxLen = 80;
            if (string.IsNullOrEmpty(json) || json.Length <= maxLen) { return json; }
            return json[..maxLen] + "...";
        }
    }
}
