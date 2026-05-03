using System.Text.Json;
using Atelia.Agent.Core;
using Atelia.Agent.Core.History;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;

namespace Atelia.DebugApps.TrpgSimulation;

/// <summary>
/// 文字梦境模拟器：两个 LLM Agent（GM + Player）纯文本乒乓，
/// 世界事实完全依赖各自的上下文窗口，随压缩而漂移。
/// </summary>
public static class Program {
    // ── 可配置参数 ──
    private const int MaxTurns = 32;
    private static readonly TimeSpan TurnDelay = TimeSpan.FromMilliseconds(500);
    private static readonly StreamStyle GmStyle = new("GM", ConsoleColor.DarkCyan, ConsoleColor.DarkGray, string.Empty);
    private static readonly StreamStyle PlayerStyle = new("Player", ConsoleColor.Yellow, ConsoleColor.DarkYellow, "    ");

    // ═════════════════════════════════════════
    // 主程序
    // ═════════════════════════════════════════

    public static async Task Main(string[] args) {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // ── 选择 StoryPack（改这一行即可切换）──
        // var pack = SeasideTownPack;
        var pack = StoryPacks.TimeTravel1990Pack;

        Console.WriteLine($"🌙 文字梦境模拟器");
        Console.WriteLine($"   StoryPack: {pack.Name} — {pack.Description}");
        Console.WriteLine($"   Max turns: {MaxTurns}");
        Console.WriteLine();

        // ── 创建 LLM Profile ──
        // var client = new AnthropicClient(apiKey, baseAddress:new Uri(EnsureTrailingSlash(baseUrl)));
        var deepSeekV4Cient = new DeepSeekV4ChatClient(
            apiKey: Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY"),
            baseAddress: new Uri("https://api.deepseek.com/")
        );
        var localCient = new OpenAIChatClient(
            apiKey: null,
            baseAddress: new Uri("http://localhost:8000/"),
            dialect: OpenAIChatDialects.SgLangCompatible,
            options: OpenAIChatClientOptions.QwenThinkingDisabled()
        );

        var gmProfile = new LlmProfile(deepSeekV4Cient, "deepseek-v4-flash", "GM", 8000u);
        // var playerProfile = new LlmProfile(localCient, "Qwen3.5-27b-GPTQ-Int4", "Player", 8000u);
        var playerProfile = new LlmProfile(deepSeekV4Cient, "deepseek-v4-flash", "Player", 8000u);

        // ── 创建两个独立的 AgentEngine ──
        var gmState = AgentState.CreateDefault();
        gmState.SetSystemPrompt(pack.GmSystemPrompt);
        var gmEngine = new AgentEngine(state: gmState);

        var playerState = AgentState.CreateDefault();
        playerState.SetSystemPrompt(pack.PlayerSystemPrompt);
        var playerEngine = new AgentEngine(state: playerState);

        // ── 上下文压缩 panel 由 AgentEngine 内建挂载 ──

        Console.WriteLine("   SoftContextTokenCap: 8,000 (方便早期触发压缩观察现象)");
        Console.WriteLine();

        // 禁用 idle provider 的心跳——我们不希望引擎在没输入时自己生成内容
        // （通过确保每次 StepAsync 前都有 pending notification 来避免触发）

        // ── 初始输入 ──
        gmEngine.AppendNotification(pack.InitialObservation);

        // ── 重复检测与时间追踪 ——
        var recentGmTexts = new List<string>();  // 最近 3 轮 GM 输出
        var recentPlayerTexts = new List<string>();  // 最近 3 轮 Player 输出
        int timeSlot = 0;  // 0=下午3:00, 1=3:30, 2=4:00, 3=4:30, 4=5:00

        // ── 主循环 ──
        string? lastGmText = null;

        // 第一轮：GM 先描述初始场景
        lastGmText = await DrainUntilOutput(gmEngine, gmProfile, GmStyle);
        if (lastGmText is not null) {
            recentGmTexts.Add(lastGmText);
        }

        for (int turn = 2; turn <= MaxTurns; turn++) {
            await Task.Delay(TurnDelay);

            // ─── 时间推进 ───
            // 每 2 轮推进一次时间槽
            if (turn % 2 == 0 && timeSlot < 4) { timeSlot++; }
            string[] timeLabels = ["下午3:00", "下午3:30", "下午4:00", "下午4:30", "下午5:00"];
            string timeLabel = timeLabels[timeSlot];

            // ─── Player 回合 ───
            if (lastGmText is not null) {
                // 注入当前时间状态
                var playerInput = $"[系统] 当前时间：{timeLabel}。\n{lastGmText}";
                playerEngine.AppendNotification(playerInput);
            }
            else {
                playerEngine.AppendNotification(
                    $"[系统] 当前时间：{timeLabel}。周围环境似乎凝固了片刻。继续你的探索——你注意到什么？你想做什么？"
                );
            }

            var playerText = await DrainUntilOutput(playerEngine, playerProfile, PlayerStyle);
            if (playerText is not null) {
                // 重复检测：与最近 2 轮 Player 输出比较
                if (IsRepetitive(playerText, recentPlayerTexts, threshold: 0.7)) {
                    WriteSystemLine($"[系统] 检测到 Player 重复输出，注入纠偏提示");
                    playerEngine.AppendNotification(
                        "[系统] 你刚才的行动与上一轮高度相似。你必须改变位置、互动对象或当前目标。" +
                        "如果 GM 抛出了钩子（妈妈叫你、电话响了、同学喊你），你必须回应那个钩子。"
                    );
                    // 重试一次
                    playerText = await DrainUntilOutput(playerEngine, playerProfile, PlayerStyle);
                }

                if (playerText is null) {
                    WriteSystemLine($"[Player 回合 {turn}: 重试后仍无文本输出，跳过]");
                }
                else {
                    recentPlayerTexts.Add(playerText);
                    if (recentPlayerTexts.Count > 3) { recentPlayerTexts.RemoveAt(0); }
                }
            }
            else {
                WriteSystemLine($"[Player 回合 {turn}: 无文本输出，跳过]");
            }

            await Task.Delay(TurnDelay);

            // ─── GM 回合 ───
            if (playerText is not null) {
                var gmInput = $"[系统] 当前时间：{timeLabel}。\n{playerText}";
                gmEngine.AppendNotification(gmInput);
            }

            lastGmText = await DrainUntilOutput(gmEngine, gmProfile, GmStyle);
            if (lastGmText is not null) {
                // 重复检测：与最近 2 轮 GM 输出比较
                if (IsRepetitive(lastGmText, recentGmTexts, threshold: 0.7)) {
                    WriteSystemLine($"[系统] 检测到 GM 重复输出，注入纠偏提示");
                    gmEngine.AppendNotification(
                        "[系统] 你刚才的叙述与上一轮高度相似。立即引入一个新事件打破重复——" +
                        "妈妈推门进来、窗外有人喊、电话铃响了、爸爸回家了、电视里动画片开始了——" +
                        "选一个日常事件。时间必须推进，场景必须变化。"
                    );
                    // 重试一次
                    lastGmText = await DrainUntilOutput(gmEngine, gmProfile, GmStyle);
                }

                if (lastGmText is null) {
                    WriteSystemLine($"[GM 回合 {turn}: 重试后仍无文本输出，跳过]");
                }
                else {
                    recentGmTexts.Add(lastGmText);
                    if (recentGmTexts.Count > 3) { recentGmTexts.RemoveAt(0); }
                }
            }
            else {
                WriteSystemLine($"[GM 回合 {turn}: 无文本输出，跳过]");
            }

            // ── Token 用量 ──
            WriteTokenUsage(gmEngine, gmProfile, playerEngine, playerProfile);

            // ── 回合结束 ──
            WriteTurnSeparator(turn);

            await Task.Delay(TurnDelay);
        }

        Console.WriteLine();
        WriteSystemLine("🌅 模拟结束。梦境消散。");
    }

    // ─────────────────────────────────────────
    // 引擎推进辅助
    // ─────────────────────────────────────────

    /// <summary>
    /// 持续调用 <see cref="AgentEngine.StepAsync"/> 直到引擎输出模型的 ActionEntry 并回到 WaitingInput，
    /// 或直到引擎卡住无法推进。
    /// </summary>
    /// <remarks>
    /// 当 Agent 调用工具（如 ctx_compress）时，引擎会进入
    /// WaitingToolResults → ToolResultsReady → Compacting → PendingToolResults → 模型重调
    /// 的多步链条。固定步数的循环无法覆盖这种场景——本方法会持续推进直至链条终结。
    /// <c>maxSteps</c> 防止死循环（理论上不应发生，但作为安全网）。
    /// </remarks>
    private static async Task<string?> DrainUntilOutput(AgentEngine engine, LlmProfile profile, StreamStyle style, int maxSteps = 20) {
        var stream = new TurnConsoleStreamer(style);

        for (int i = 0; i < maxSteps; i++) {
            var observer = stream.CreateObserver();
            var result = await engine.StepAsync(profile, observer);

            if (result.ToolResults is not null) {
                stream.WriteToolResults(result.ToolResults);
            }

            // 从模型拿到输出且引擎已回到空闲态 → 提取文本返回
            if (result.Output is not null && result.StateAfter == AgentRunState.WaitingInput) {
                stream.FinishTurn();
                return result.Output.Message.GetFlattenedText();
            }

            // 引擎空闲但无新输出 → 无法继续
            if (!result.ProgressMade && result.StateAfter == AgentRunState.WaitingInput) {
                stream.FinishTurn();
                return null;
            }

            // 否则继续推进（工具执行中、压缩进行中、等待模型重调等）
        }

        // 安全网：步数耗尽
        stream.FinishTurn();
        WriteSystemLine("[警告] DrainUntilOutput 步数耗尽，强制返回 null");
        return null;
    }

    /// <summary>
    /// 简易重复检测：将文本归一化后与历史输出比较字符级相似度。
    /// 如果最近 2 条中有任何一条相似度超过阈值，视为重复。
    /// </summary>
    private static bool IsRepetitive(string text, List<string> history, double threshold = 0.7) {
        if (history.Count == 0) { return false; }

        // 归一化：取前 200 字符，去除空白差异
        static string Normalize(string s) {
            var trimmed = s.Trim();
            if (trimmed.Length > 200) { trimmed = trimmed[..200]; }
            // 折叠所有连续空白为单个空格
            return System.Text.RegularExpressions.Regex.Replace(trimmed, @"\s+", " ");
        }

        var norm = Normalize(text);
        // 只检查最近 2 条
        var recent = history.Skip(history.Count - 2).ToList();
        foreach (var h in recent) {
            var hNorm = Normalize(h);
            if (hNorm.Length == 0) { continue; }
            // 简单的 Jaccard-like 字符重叠度
            var common = norm.Intersect(hNorm).Count();
            var union = norm.Union(hNorm).Count();
            var similarity = union > 0 ? (double)common / union : 0.0;
            if (similarity >= threshold) { return true; }
        }
        return false;
    }

    private static string FormatToolArguments(string rawArgumentsJson) {
        if (string.IsNullOrWhiteSpace(rawArgumentsJson) || rawArgumentsJson == "{}") { return "<none>"; }

        try {
            using var document = JsonDocument.Parse(rawArgumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) { return TruncateSingleLine(rawArgumentsJson); }

            var parts = new List<string>();
            foreach (var property in document.RootElement.EnumerateObject()) {
                parts.Add($"{property.Name}={FormatJsonValue(property.Value)}");
            }

            return parts.Count == 0 ? "<none>" : string.Join(", ", parts);
        }
        catch (JsonException) {
            return TruncateSingleLine(rawArgumentsJson);
        }
    }

    private static string FormatJsonValue(JsonElement element) {
        return element.ValueKind switch {
            JsonValueKind.String => $"\"{TruncateSingleLine(element.GetString() ?? string.Empty)}\"",
            JsonValueKind.Object or JsonValueKind.Array => TruncateSingleLine(element.GetRawText()),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => TruncateSingleLine(element.GetRawText())
        };
    }

    private static string FormatToolResult(string? text) {
        if (string.IsNullOrWhiteSpace(text)) { return "<empty>"; }
        return TruncateSingleLine(text);
    }

    private static string TruncateSingleLine(string text, int maxLength = 0) {
        var normalized = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (maxLength <= 0 || normalized.Length <= maxLength) { return normalized; }
        return normalized[..maxLength] + "...";
    }

    private static void WriteTurnSeparator(int turn) {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"── 第 {turn} 回合结束 " + new string('─', 40));
        Console.ForegroundColor = originalColor;
        Console.WriteLine();
    }

    private static void WriteTokenUsage(
        AgentEngine gmEngine, LlmProfile gmProfile,
        AgentEngine playerEngine, LlmProfile playerProfile
    ) {
        var gmTokens = gmEngine.EstimateCurrentContextTokens();
        var playerTokens = playerEngine.EstimateCurrentContextTokens();
        var gmCap = (ulong)gmProfile.SoftContextTokenCap;
        var playerCap = (ulong)playerProfile.SoftContextTokenCap;
        var gmPct = (double)gmTokens / gmCap * 100.0;
        var playerPct = (double)playerTokens / playerCap * 100.0;

        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"[Token: GM {gmTokens,5} / {gmCap,5} ({gmPct,5:F1}%)");
        Console.Write($" | Player {playerTokens,5} / {playerCap,5} ({playerPct,5:F1}%)");

        // 压缩状态标记
        if (gmEngine.HasPendingCompaction) { Console.Write(" ⚡GM压缩待执行"); }
        if (playerEngine.HasPendingCompaction) { Console.Write(" ⚡Player压缩待执行"); }

        Console.WriteLine("]");
        Console.ForegroundColor = originalColor;
    }

    private static void WriteSystemLine(string text) {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(text);
        Console.ForegroundColor = originalColor;
    }

    private readonly record struct StreamStyle(string Label, ConsoleColor TextColor, ConsoleColor ReasoningColor, string ContentPrefix);

    private sealed class TurnConsoleStreamer {
        private readonly StreamStyle _style;
        private bool _lineStart = true;
        private bool _hasOutput;
        private bool _thinkingActive;
        private bool _reasoningObserved;

        public TurnConsoleStreamer(StreamStyle style) {
            _style = style;
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

        public void WriteToolResults(ToolResultsEntry toolResults) {
            foreach (var result in toolResults.Results) {
                var elapsedText = result.Elapsed is { } elapsed && elapsed > TimeSpan.Zero
                    ? $", {elapsed.TotalMilliseconds:F0}ms"
                    : string.Empty;
                WriteDiagnosticLine(
                    $"[{_style.Label}/tool-result] {result.ToolName}#{result.ToolCallId} [{result.ExecuteResult.Status}{elapsedText}] => {FormatToolResult(result.ExecuteResult.Result.Basic)}"
                );
            }

            if (!string.IsNullOrWhiteSpace(toolResults.ExecuteError)) {
                WriteDiagnosticLine($"[{_style.Label}/tool-error] {FormatToolResult(toolResults.ExecuteError)}");
            }
        }

        public void FinishTurn() {
            if (!_hasOutput) { return; }

            EnsureLineBreak();
            Console.WriteLine();
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
            WriteChunk(delta, _style.ReasoningColor, _style.ContentPrefix + $"[{_style.Label} thinking] ");
        }

        private void OnThinkingEnd() {
            if (_thinkingActive && !_reasoningObserved) {
                WriteDiagnosticLine($"[{_style.Label}/thinking] ...");
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
            _reasoningObserved = false;
            WriteChunk(delta, _style.TextColor, _style.ContentPrefix);
        }

        private void OnToolCall(RawToolCall toolCall) {
            WriteDiagnosticLine(
                $"[{_style.Label}/tool-call] {toolCall.ToolName}#{toolCall.ToolCallId}({FormatToolArguments(toolCall.RawArgumentsJson)})"
            );
        }

        private void WriteDiagnosticLine(string text) {
            EnsureLineBreak();
            var originalColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(_style.ContentPrefix);
            Console.WriteLine(text);
            Console.ForegroundColor = originalColor;
            _lineStart = true;
            _hasOutput = true;
        }

        private void WriteChunk(string text, ConsoleColor color, string linePrefix) {
            var originalColor = Console.ForegroundColor;
            Console.ForegroundColor = color;

            foreach (var ch in text) {
                if (_lineStart) {
                    Console.Write(linePrefix);
                    _lineStart = false;
                }

                Console.Write(ch);
                if (ch == '\n') {
                    _lineStart = true;
                }
            }

            Console.ForegroundColor = originalColor;
            _hasOutput = true;
        }

        private void EnsureLineBreak() {
            if (_lineStart) { return; }

            Console.WriteLine();
            _lineStart = true;
        }
    }
}
