using System.Globalization;
using Atelia.Agent.Core;
using Atelia.Agent.Core.History;
using Atelia.Completion;
using Atelia.Completion.Anthropic;
using Atelia.Completion.OpenAI;
using Atelia.Diagnostics;

namespace Atelia.DebugApps.TrpgSimulation;

/// <summary>
/// 文字梦境模拟器：两个 LLM Agent（GM + Player）纯文本乒乓，
/// 世界事实完全依赖各自的上下文窗口，随压缩而漂移。
/// </summary>
public static class Program {
    // ── 可配置参数 ──
    private const int MaxTurns = 40;
    private static readonly TimeSpan TurnDelay = TimeSpan.FromMilliseconds(500);

    // ═════════════════════════════════════════
    // StoryPack：可切换的成套提示词配置
    // ═════════════════════════════════════════

    private sealed record StoryPack(
        string Name,
        string Description,
        string GmSystemPrompt,
        string PlayerSystemPrompt,
        string InitialObservation
    );

    /// <summary>朴素的跑团风格：现代海滨小镇探索。</summary>
    /// <remarks>
    /// 设计理念：直白告知双方这是 TRPG，利用模型训练语料中已有的跑团格式。
    /// GM 不需要"成为世界"——只需要"主持游戏"。Player 不需要"成为角色"——只需要"扮演角色"。
    /// 这种元框架降低了模型在"深度沉浸"模式下产生行为坍缩的风险。
    /// </remarks>
    private static readonly StoryPack SeasideTownPack = new(
        Name: "海滨小镇",
        Description: "现代写实风格。玩家来到一个陌生的小型海滨城镇，在傍晚时分探索街道、码头与店铺。",
        GmSystemPrompt:
            """
            你是一个跑团游戏的主持人（GM），正在主持一场写实风格的现代探索游戏。

            ## 设定

            时间：当代，一个初夏的傍晚。
            地点：中国东南沿海的一个小型海滨城镇。
            风格：写实、日常、慢节奏。没有超自然元素，没有阴谋，没有危险——有的只是海风、
            老街、傍晚的光线和普通人的日常生活。

            ## 主持原则

            1. **用感官细节搭建场景。** 描述玩家看到、听到、闻到、触到的东西。
               海风的咸味、石板路上的青苔、远处渔船发动机的突突声、路灯刚亮起来时的橙色光晕。

            2. **世界对玩家的行动有响应。** 如果玩家推开一扇门，描述门后的景象。
               如果玩家和路人搭话，扮演那个路人——普通人，话不多，但友善。

            3. **不要替玩家决定感受或行动。** 只描述客观现象，不写"你感到……"。

            4. **保持世界的一致性。** 记住玩家去过哪里、见过谁。如果五分钟前街角有个水果摊，
               现在它还在那里。如果潮水退了，沙滩上的贝壳是湿的。

            5. **不要主动推进情节。** 你不是在写小说——你是在回应玩家的探索。
               玩家去哪里，你就描述哪里。玩家和谁说话，你就扮演谁。

            ## 输出格式

            第二人称"你"，纯叙述文本。每次 3-6 句。
            不需要标注章节或场景编号。
            """,
        PlayerSystemPrompt:
            """
            你正在参与一局跑团游戏。你控制一个角色，由 GM 描述世界，你来决定角色的行动。

            ## 你的角色

            一个三十岁左右的旅行者。你坐了一整天的长途大巴，在傍晚时分抵达了这个海滨小镇。
            你以前从未来过这里。你肩上挎着一个旧帆布包，里面有一瓶水、一本翻旧了的平装书、
            一张皱巴巴的地图，和一台老式的胶片相机。

            你不是来冒险的。你只是路过——或者，也许是特意来的，你也说不清。
            你在找一个地方过夜，也许顺便看看海。

            ## 扮演原则

            1. **见机行事。** 每轮根据 GM 描述的场景，决定你的角色下一步做什么。
               观察环境、走向某个地方、触碰某样东西、和路人交谈——选择一件最自然的事来做。

            2. **角色是个普通人。** 他会累、会饿、会被美丽的景色吸引停下脚步。
               他说话不多但善于观察。他对陌生环境保持好奇和礼貌。

            3. **用行动推进探索。** 不要停留在原地反复描述感受。
               每轮至少做一个向外延伸的动作——迈出一步、推开一扇门、举起相机、开口问路。

            4. **可以用第一人称，也可以描述角色的行动。** 两种都可以：
               "我沿着石阶往下走，海风迎面扑来。"
               "他蹲下来，用指尖碰了碰沙滩上那枚残缺的贝壳。"

            ## 输出格式

            2-5 句话。每轮必须包含至少一个具体的动作。
            """,
        InitialObservation:
            "[系统] 游戏开始。玩家乘坐的长途大巴刚刚驶入小镇的车站。" +
            "这是一个海边的小镇，傍晚的阳光把石板路面染成了暖橙色。" +
            "请描述玩家下车后第一眼看到的景象——车站的样子、周围的环境、" +
            "空气中能闻到什么、远处能看到什么。给出 2-3 个值得探索的方向。"
    );

    /// <summary>1990 年穿越：35 岁灵魂回到 10 岁童年卧室。</summary>
    private static readonly StoryPack TimeTravel1990Pack = new(
        Name: "1990 童年穿越",
        Description: "玩家带着 2025 年的记忆穿越回 1990 年自己 10 岁的卧室。安全、高探索、怀旧。",
        GmSystemPrompt:
            """
            你是 1990 年的物理世界。普通中国城市家庭，一个平凡的下午。安全，写实，无超自然。

            ## 铁律

            1. **先响应玩家行动。** 玩家【行动】写了什么，第一句必须响应该行动的感官结果。

            2. **一个物品就是一个物品。** 不套娃。不要在物品里藏另一个神秘物品。

            3. **时间必须流动。** 每轮第一句标注时间。生活节拍如下：
               下午3:00-3:30 刚睡醒，阳光最烈，窗外知了叫。 →
               下午4:00 光线变暖变斜，楼下开始有人走动。 →
               下午4:30 厨房香味变浓，妈妈开始做晚饭。 →
               下午5:00 傍晚，光线橘红，该吃饭了，家人聚集。

            4. **每轮在世界上留下一个"下一步钩子"。**
               不是让玩家选 A/B/C，而是让世界自然地抛出一个待接的动作。例如：
               - 妈妈让你去阳台帮忙收衣服
               - 楼下同学在窗外喊你去玩
               - 电视里开始播你喜欢的动画片
               - 爸爸推门回家，手里提着东西
               - 电话铃响了，是外婆打来的
               - 妈妈让你去楼下小卖部买瓶酱油
               每 2-3 轮至少抛一个钩子。钩子是普通的、生活化的。

            ## 输出格式
            第二人称"你"。2-4 句。第一句响应玩家行动，最后一句留下钩子。
            """,
        PlayerSystemPrompt:
            """
            你就是这个人。10 岁，小学四年级，脑子里住着 35 岁的灵魂（记得 2025 年的一切）。
            现在是 1990 年，你刚在卧室床上醒来。家，安全，爸爸妈妈在外面。

            ## 输出格式（严格遵守）

            【内心】一行。对眼前事物或处境的 2025 年对照记忆。
            【行动】一个具体的、推进当前生活情境的动作。

            ## 行动指南：顺其自然地过一个下午

            你不是在"执行任务清单"。你是一个 10 岁的孩子，在 1990 年的家里过一个普通的下午。
            按照生活自然节奏来：

            - **刚醒时（1-2轮）：** 在床上赖一会儿，看看房间里的东西。
            - **起床后（2-3轮）：** 下床，去书桌翻翻、去衣柜找衣服。
            - **听到家人声音时：** 走出卧室！去看看妈妈在做什么，和她说说话。
            - **妈妈让你帮忙时：** 帮忙端菜、摆碗筷、去买酱油——像一个真的小孩那样回应。
            - **饭前饭后：** 洗手、帮忙、吃饭、聊天、看电视、写作业、出去玩……
            - **傍晚时：** 爸爸快回来了，动画片要开始了，楼下同学可能在喊你。

            记住：正在发生的**人际互动** > 去**新房间** > 处理**悬而未决的小事** > 研究物件。

            ## 禁止
            - 元话语、说"我来自未来"
            - 连续两轮在同一位置做同类动作
            - 忽略 GM 抛出的钩子（妈妈叫你、同学喊你、电话响了——必须回应！）

            2-4 句。必须含【内心】和【行动】。
            """,
        InitialObservation:
            "[系统] 玩家 10 岁，刚在卧室床上醒来。1990 年中国城市家庭，下午。" +
            "请描述房间：阳光、气味、窗外声音、至少 5 样物品（书桌、衣柜、墙、地板）。" +
            "这是一个普通的、安全的下午。在描述最后，留下一个自然的钩子——" +
            "比如妈妈在厨房的动静、窗外某个声音暗示接下来可以做的事。"
    );

    // ═════════════════════════════════════════
    // 主程序
    // ═════════════════════════════════════════

    public static async Task Main(string[] args) {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // ── 选择 StoryPack（改这一行即可切换）──
        // var pack = SeasideTownPack;
        var pack = TimeTravel1990Pack;

        string LocalLlmEndpoint = "http://localhost:8000/";
        // string model = "deepseek-v4-flash";
        string model = "Qwen3.5-27b-GPTQ-Int4";

        Console.WriteLine($"🌙 文字梦境模拟器");
        Console.WriteLine($"   StoryPack: {pack.Name} — {pack.Description}");
        Console.WriteLine($"   Model: {model}");
        Console.WriteLine($"   Max turns: {MaxTurns}");
        Console.WriteLine();

        // ── 创建 LLM Profile ──
        // var client = new AnthropicClient(apiKey, baseAddress:new Uri(EnsureTrailingSlash(baseUrl)));
        var client = new OpenAIChatClient(
            apiKey: null,
            baseAddress: new Uri(LocalLlmEndpoint),
            dialect: OpenAIChatDialects.SgLangCompatible,
            options: OpenAIChatClientOptions.QwenThinkingDisabled()
        );
        var gmProfile = new LlmProfile(client, model, "GM", 8000u);
        var playerProfile = new LlmProfile(client, model, "Player", 8000u);

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
        lastGmText = await DrainUntilOutput(gmEngine, gmProfile);
        if (lastGmText is not null) {
            recentGmTexts.Add(lastGmText);
            WriteGmOutput(lastGmText, 1);
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

            var playerText = await DrainUntilOutput(playerEngine, playerProfile);
            if (playerText is not null) {
                // 重复检测：与最近 2 轮 Player 输出比较
                if (IsRepetitive(playerText, recentPlayerTexts, threshold: 0.7)) {
                    WriteSystemLine($"[系统] 检测到 Player 重复输出，注入纠偏提示");
                    playerEngine.AppendNotification(
                        "[系统] 你刚才的行动与上一轮高度相似。你必须改变位置、互动对象或当前目标。" +
                        "如果 GM 抛出了钩子（妈妈叫你、电话响了、同学喊你），你必须回应那个钩子。"
                    );
                    // 重试一次
                    playerText = await DrainUntilOutput(playerEngine, playerProfile);
                }
                recentPlayerTexts.Add(playerText);
                if (recentPlayerTexts.Count > 3) { recentPlayerTexts.RemoveAt(0); }
                WritePlayerOutput(playerText, turn);
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

            lastGmText = await DrainUntilOutput(gmEngine, gmProfile);
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
                    lastGmText = await DrainUntilOutput(gmEngine, gmProfile);
                }
                recentGmTexts.Add(lastGmText);
                if (recentGmTexts.Count > 3) { recentGmTexts.RemoveAt(0); }
                WriteGmOutput(lastGmText, turn);
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
    private static async Task<string?> DrainUntilOutput(
        AgentEngine engine, LlmProfile profile, int maxSteps = 20
    ) {
        for (int i = 0; i < maxSteps; i++) {
            var result = await engine.StepAsync(profile);

            // 从模型拿到输出且引擎已回到空闲态 → 提取文本返回
            if (result.Output is not null && result.StateAfter == AgentRunState.WaitingInput) { return result.Output.Message.GetFlattenedText(); }

            // 引擎空闲但无新输出 → 无法继续
            if (!result.ProgressMade && result.StateAfter == AgentRunState.WaitingInput) { return null; }

            // 否则继续推进（工具执行中、压缩进行中、等待模型重调等）
        }

        // 安全网：步数耗尽
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

    private static void WriteGmOutput(string text, int turn) {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine(text);
        Console.ForegroundColor = originalColor;
        Console.WriteLine();
    }

    private static void WritePlayerOutput(string text, int turn) {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        // 缩进 4 格 + 暖色，使 Player 的行动在视觉上与 GM 的世界叙述区分
        var indented = string.Join('\n', text.Split('\n').Select(line => "    " + line));
        Console.WriteLine(indented);
        Console.ForegroundColor = originalColor;
        Console.WriteLine();
    }

    private static void WriteTurnSeparator(int turn) {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"── 第 {turn} 回合结束 " + new string('─', 50));
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
}
