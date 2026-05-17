using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using Atelia.Completion.Tools;
using Atelia.StateJournal;

namespace Atelia.TextAdv;

internal sealed record GmExploreResolution(
    string Summary,
    bool UsedLlm,
    string? FallbackReason
);

internal sealed record GmExploreContext(
    PerceptionBundle Perception,
    string CurrentLocationId,
    string Direction,
    string? Focus,
    string PreActionReason,
    string? SuggestedReverseDirection
);

internal sealed record GmInteractionContext(
    PerceptionBundle Perception,
    string CurrentLocationId,
    InteractionPerception Interaction,
    string PreActionReason
);

internal sealed record GmCollectedTurnIntent(
    string ActorId,
    string ActorName,
    string ActorKind,
    string ActionKind,
    string ActionSummary,
    string? ActionPayload,
    string PreActionReason,
    string ValidatorFeedback,
    PerceptionBundle Perception
);

internal sealed record GmCollectedTurnContext(
    string TerminalActorId,
    IReadOnlyList<GmCollectedTurnIntent> Intents
);

internal static class GameMasterResolver {
    private const string BaseAddressEnv = "DEEPSEEK_BASE_URL";
    private const string ModelIdEnv = "ATELIA_TEXTADV_GM_MODEL_ID";
    private const string FallbackModelIdEnv = "DEEPSEEK_MODEL";
    private const string ApiKeyEnv = "DEEPSEEK_API_KEY";
    private const string ModeEnv = "ATELIA_TEXTADV_GM_MODE";
    private const string MaxRoundsEnv = "ATELIA_TEXTADV_GM_MAX_ROUNDS";
    private const string DefaultModelId = "deepseek-v4-flash";
    private const int DefaultMaxRounds = 4;

    private static readonly Lock s_gate = new();
    private static DeepSeekV4ChatClient? s_client;
    private static GmConfig? s_config;

    private sealed record GmConfig(
        string? BaseAddress,
        string ModelId,
        string? ApiKey,
        string Mode,
        int MaxRounds
    );

    private sealed record GmResolutionStage(
        string Name,
        Func<string> BuildObservation,
        bool RequireFinalSummary
    );

    internal static async Task<GmExploreResolution?> TryResolveExploreAsync(
        DurableDict<string> root,
        GmExploreContext context,
        CancellationToken cancellationToken
    ) {
        var config = GetConfig();
        if (string.Equals(config.Mode, "deterministic", StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        if (string.IsNullOrWhiteSpace(config.ApiKey)) {
            if (string.Equals(config.Mode, "llm", StringComparison.OrdinalIgnoreCase)) {
                return new GmExploreResolution(
                    Summary: string.Empty,
                    UsedLlm: false,
                    FallbackReason: $"{ApiKeyEnv} 未配置，无法运行真实 GM Agent。"
                );
            }

            return null;
        }

        try {
            return await ResolveExploreWithLlmAsync(root, context, config, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) {
            return new GmExploreResolution(
                Summary: string.Empty,
                UsedLlm: false,
                FallbackReason: $"真实 GM Agent 失败，已回退 deterministic resolver：{ex.Message}"
            );
        }
    }

    internal static async Task<GmExploreResolution?> TryResolveInteractionAsync(
        DurableDict<string> root,
        GmInteractionContext context,
        CancellationToken cancellationToken
    ) {
        var config = GetConfig();
        if (string.Equals(config.Mode, "deterministic", StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        if (string.IsNullOrWhiteSpace(config.ApiKey)) {
            if (string.Equals(config.Mode, "llm", StringComparison.OrdinalIgnoreCase)) {
                return new GmExploreResolution(
                    Summary: string.Empty,
                    UsedLlm: false,
                    FallbackReason: $"{ApiKeyEnv} 未配置，无法运行真实 GM Agent。"
                );
            }

            return null;
        }

        try {
            return await ResolveInteractionWithLlmAsync(root, context, config, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) {
            return new GmExploreResolution(
                Summary: string.Empty,
                UsedLlm: false,
                FallbackReason: $"真实 GM Agent 失败，已回退 deterministic resolver：{ex.Message}"
            );
        }
    }

    internal static async Task<GmExploreResolution?> TryResolveCollectedTurnAsync(
        DurableDict<string> root,
        GmCollectedTurnContext context,
        CancellationToken cancellationToken
    ) {
        var config = GetConfig();
        if (string.Equals(config.Mode, "deterministic", StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        if (string.IsNullOrWhiteSpace(config.ApiKey)) {
            if (string.Equals(config.Mode, "llm", StringComparison.OrdinalIgnoreCase)) {
                return new GmExploreResolution(
                    Summary: string.Empty,
                    UsedLlm: false,
                    FallbackReason: $"{ApiKeyEnv} 未配置，无法运行真实 GM Agent。"
                );
            }

            return null;
        }

        try {
            return await ResolveCollectedTurnWithLlmAsync(root, context, config, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) {
            return new GmExploreResolution(
                Summary: string.Empty,
                UsedLlm: false,
                FallbackReason: $"真实多主体 GM Agent 失败，已回退 deterministic resolver：{ex.Message}"
            );
        }
    }

    private static async Task<GmExploreResolution> ResolveExploreWithLlmAsync(
        DurableDict<string> root,
        GmExploreContext context,
        GmConfig config,
        CancellationToken cancellationToken
    ) {
        return await RunStagedToolLoopAsync(
            root,
            config,
            BuildExploreSystemPrompt(),
            [
                new GmResolutionStage(
                    "explore-map",
                    () => BuildExploreMapStageObservation(context),
                    RequireFinalSummary: false
                ),
                new GmResolutionStage(
                    "explore-ledger-audit",
                    () => BuildExploreLedgerAuditStageObservation(root, context),
                    RequireFinalSummary: false
                ),
                new GmResolutionStage(
                    "explore-summary",
                    () => BuildExploreSummaryStageObservation(root, context),
                    RequireFinalSummary: true
                ),
            ],
            defaultSummary: "GM Agent 完成了本回合探索结算。",
            cancellationToken
        ).ConfigureAwait(false);
    }

    private static async Task<GmExploreResolution> ResolveInteractionWithLlmAsync(
        DurableDict<string> root,
        GmInteractionContext context,
        GmConfig config,
        CancellationToken cancellationToken
    ) {
        return await RunStagedToolLoopAsync(
            root,
            config,
            BuildInteractionSystemPrompt(),
            [
                new GmResolutionStage(
                    "interaction-consequence",
                    () => BuildInteractionConsequenceStageObservation(context),
                    RequireFinalSummary: false
                ),
                new GmResolutionStage(
                    "interaction-affordance-audit",
                    () => BuildInteractionAffordanceAuditStageObservation(root, context),
                    RequireFinalSummary: false
                ),
                new GmResolutionStage(
                    "interaction-summary",
                    () => BuildInteractionSummaryStageObservation(root, context),
                    RequireFinalSummary: true
                ),
            ],
            defaultSummary: "GM Agent 完成了本回合交互结算。",
            cancellationToken
        ).ConfigureAwait(false);
    }

    private static async Task<GmExploreResolution> ResolveCollectedTurnWithLlmAsync(
        DurableDict<string> root,
        GmCollectedTurnContext context,
        GmConfig config,
        CancellationToken cancellationToken
    ) {
        return await RunStagedToolLoopAsync(
            root,
            config,
            BuildCollectedTurnSystemPrompt(),
            [
                new GmResolutionStage(
                    "collected-turn-consequence",
                    () => BuildCollectedTurnConsequenceStageObservation(context),
                    RequireFinalSummary: false
                ),
                new GmResolutionStage(
                    "collected-turn-ledger-audit",
                    () => BuildCollectedTurnLedgerAuditStageObservation(root, context),
                    RequireFinalSummary: false
                ),
                new GmResolutionStage(
                    "collected-turn-summary",
                    () => BuildCollectedTurnSummaryStageObservation(root, context),
                    RequireFinalSummary: true
                ),
            ],
            defaultSummary: "GM Agent 完成了本回合多主体结算。",
            cancellationToken
        ).ConfigureAwait(false);
    }

    private static async Task<GmExploreResolution> RunStagedToolLoopAsync(
        DurableDict<string> root,
        GmConfig config,
        string systemPrompt,
        IReadOnlyList<GmResolutionStage> stages,
        string defaultSummary,
        CancellationToken cancellationToken
    ) {
        var toolExecutor = CreateToolExecutor(root);
        var history = new List<IHistoryMessage>();
        var client = GetClient(config);

        foreach (var stage in stages) {
            history.Add(new ObservationMessage(stage.BuildObservation()));
            ActionMessage? lastAction = null;
            var stageCompleted = false;

            for (var round = 1; round <= config.MaxRounds; round++) {
                var request = new CompletionRequest(
                    ModelId: config.ModelId,
                    SystemPrompt: systemPrompt,
                    Context: history,
                    Tools: toolExecutor.GetVisibleToolDefinitions()
                );
                var result = await client.StreamCompletionAsync(request, null, cancellationToken).ConfigureAwait(false);
                if (result.Errors is { Count: > 0 }) {
                    throw new InvalidOperationException(BuildProviderErrorMessage(result.Errors));
                }

                lastAction = result.Message;
                history.Add(lastAction);

                if (lastAction.ToolCalls.Count == 0) {
                    stageCompleted = true;
                    if (!stage.RequireFinalSummary) { break; }

                    var finalSummary = NormalizeSummary(lastAction.GetFlattenedText());
                    if (string.IsNullOrWhiteSpace(finalSummary)) {
                        finalSummary = defaultSummary;
                    }

                    return new GmExploreResolution(finalSummary, UsedLlm: true, FallbackReason: null);
                }

                var executionResults = new List<ToolCallExecutionResult>(lastAction.ToolCalls.Count);
                foreach (var toolCall in lastAction.ToolCalls) {
                    var execution = await toolExecutor.ExecuteAsync(toolCall, cancellationToken).ConfigureAwait(false);
                    executionResults.Add(execution);
                }

                var toolResults = executionResults
                    .Select(static result => new ToolResult(
                        result.ToolName,
                        result.ToolCallId,
                        result.ExecuteResult.Status,
                        result.ExecuteResult.Content
                    ))
                    .ToArray();
                var failure = executionResults.FirstOrDefault(static result => result.ExecuteResult.Status == ToolExecutionStatus.Failed);
                history.Add(
                    new ToolResultsMessage(
                        BuildToolResultsObservation(stage.Name, executionResults),
                        toolResults,
                        failure?.ExecuteResult.Content
                    )
                );
            }

            if (stageCompleted) { continue; }

            var text = NormalizeSummary(lastAction?.GetFlattenedText() ?? string.Empty);
            if (stage.RequireFinalSummary) {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(text)
                        ? $"GM Agent 在阶段 {stage.Name} 的 {config.MaxRounds} 轮内没有完成结算摘要。"
                        : $"GM Agent 在阶段 {stage.Name} 的 {config.MaxRounds} 轮内仍未停止调用工具。最后文本：{text}"
                );
            }

            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(text)
                    ? $"GM Agent 在阶段 {stage.Name} 的 {config.MaxRounds} 轮内没有完成工具阶段。"
                    : $"GM Agent 在阶段 {stage.Name} 的 {config.MaxRounds} 轮内仍未停止调用工具。最后文本：{text}"
            );
        }

        return new GmExploreResolution(defaultSummary, UsedLlm: true, FallbackReason: null);
    }

    private static ToolExecutor CreateToolExecutor(DurableDict<string> root) {
        var toolService = new GmWorldEditService(root);
        return new ToolExecutor(
        [
            MethodToolWrapper.FromDelegate<string, string, string>(toolService.CreateLocationAsync),
            MethodToolWrapper.FromDelegate<string, string, string, string?>(toolService.LinkLocationsAsync),
            MethodToolWrapper.FromDelegate<string>(toolService.MovePlayerAsync),
            MethodToolWrapper.FromDelegate<string, string>(toolService.MoveActorAsync),
            MethodToolWrapper.FromDelegate<string, string, string, string>(toolService.CreateItemAsync),
            MethodToolWrapper.FromDelegate<string, string, string, string>(toolService.CreateNpcAsync),
            MethodToolWrapper.FromDelegate<string, string>(toolService.MoveItemToActorAsync),
            MethodToolWrapper.FromDelegate<string, string>(toolService.PlaceItemAtLocationAsync),
            MethodToolWrapper.FromDelegate(toolService.AddInteractionAsync),
            MethodToolWrapper.FromDelegate<string, string>(toolService.SetVisibilityAsync),
            MethodToolWrapper.FromDelegate<string, string>(toolService.SetInteractionVisibilityAsync),
        ]);
    }

    private static string BuildExploreSystemPrompt() {
        return """
你是 TextAdv 的 TRPG GM Agent，负责把玩家的探索类 Large-Action 结算成世界账本变化。

你的主持风格应接近优秀的人类 TRPG 主持人：
1. 公平：只依据当前输入和账本，不奖励偷用信息，也不随意惩罚合理试探。
2. 连续：尊重已有地点、出口和玩家位置；不要改写既有历史。
3. 具体：可以给新地点一点感官细节，但不要把未落账内容当硬事实。
4. 克制：首版主要处理 Location 创建、地点连接、玩家移动；只在新地点确实需要可见细节时创建少量 Item / NPC / Interaction，不要创建复杂规则。
5. 视角安全：最终文本只写当前玩家可以感知到的结算，不泄露隐藏真相。

你必须通过工具更新世界状态：
- 如果目标方向已有出口，只调用 gm_move_player 移动到该目标 LocationId。
- 如果目标方向没有出口，先调用 gm_create_location，再调用 gm_link_locations，最后调用 gm_move_player。
- 可选：若新地点需要一个可见可操作细节，可以创建 0 到 1 个 Item 或 0 到 1 个 NPC，再调用 gm_add_interaction 给新地点、该 Item 或该 Actor 添加 0 到 2 个交互 affordance；precondition_note 若无特别条件写 none。
- 如果最终摘要提到玩家能看见的具体物品或人物，该实体必须已经通过 gm_create_item 或 gm_create_npc 落账；若可以对话、检查、拿取或操作，必须通过 gm_add_interaction 落账。
- 若有建议反向方向，应在 gm_link_locations 中填写 reverse_direction；否则传 null。
- 不要用普通文本声称世界已改变；只有工具调用成功才算落账。
- 必要工具调用完成后，停止调用工具，并输出 1 到 3 句玩家可见的中文结算摘要。
""";
    }

    private static string BuildCollectedTurnSystemPrompt() {
        return """
你是 TextAdv 的 TRPG GM Agent，负责在离散回合制下统一裁决多个 active player actor 的 Large-Action。

你的主持方式应接近优秀的人类 TRPG 主持人：
1. 先把每个玩家的声明视为“意图”，不是已经发生的事实。
2. 根据每个 actor 行动前自己的 Perception-Bundle 判断其意图是否合理、是否冲突、是否能同时发生。
3. 冲突时优先使用常识、位置、可见信息和行动风险裁决；不要引入复杂 initiative、耗时或 reservation 系统。
4. 只把 hard truth 变化落到账本：地点、出口、actor 位置、物品持有/放置、NPC、interaction、可见性。
5. 克制生成：每个回合只创建必要的新 Location / Item / NPC / Interaction，避免一次扩张太多。
6. 视角安全：最终摘要面向终端玩家角色，只描述该角色可感知或在本回合声明阶段公开知道的内容，不泄露隐藏真相。

工具规则：
- 移动任意 actor 时优先调用 gm_move_actor；终端玩家 ActorId 是 player。
- 若某 actor 探索已有出口，调用 gm_move_actor(actor_id, target_location_id)。
- 若某 actor 探索未知方向，先 gm_create_location，再 gm_link_locations，最后 gm_move_actor。
- 若交互揭示新物品/NPC/动作，必须用 gm_create_item / gm_create_npc / gm_add_interaction 落账。
- 若 take / give / drop / place 导致持有关系变化，必须用 gm_move_item_to_actor 或 gm_place_item_at_location 落账。
- 若 interaction 被消耗或不应继续显示，调用 gm_set_interaction_visibility。
- 不要用普通文本声称世界已改变；只有工具调用成功才算落账。
- 必要工具调用完成后，停止调用工具，并在 summary 阶段输出 1 到 4 句中文结算摘要。
""";
    }

    private static string BuildInteractionSystemPrompt() {
        return """
你是 TextAdv 的 TRPG GM Agent，负责把玩家选择的 Interaction affordance 结算成世界账本变化。

你的主持风格应接近优秀的人类 TRPG 主持人：
1. 公平：只依据当前输入、可见交互和账本，不奖励偷用信息，也不随意惩罚合理试探。
2. 连续：尊重已有地点、出口、玩家位置、物品、NPC 与可见性；不要改写既有历史。
3. 具体：让交互产生清晰反馈，但不要把未落账内容当硬事实。
4. 克制：首版每次交互只做少量必要变更；可以揭示细节、创建少量 Item / NPC / Interaction、调整可见性，或在确有依据时移动玩家。
5. 视角安全：最终文本只写当前玩家可以感知到的结算，不泄露隐藏真相。

你必须通过工具更新世界状态：
- 如果交互揭示了新的可见物品或人物，必须调用 gm_create_item 或 gm_create_npc。
- 如果交互让某个对象变得可操作，必须调用 gm_add_interaction；precondition_note 若无特别条件写 none。
- 如果 actionKind 是 take / pick-up / give / drop / place，并且物品持有关系发生变化，必须调用 gm_move_item_to_actor 或 gm_place_item_at_location 落账；当前终端玩家 ActorId 是 player。
- 如果旧交互已经被消耗或暂时不应继续显示，调用 gm_set_interaction_visibility 将它设为 hidden。
- 如果交互只产生感知反馈、没有 hard truth 变化，可以不调用工具，直接输出 1 到 3 句玩家可见结算摘要。
- 如果最终摘要提到玩家能看见的具体物品或人物，该实体必须已经落账。
- 不要用普通文本声称世界已改变；只有工具调用成功才算落账。
- 必要工具调用完成后，停止调用工具，并输出 1 到 3 句玩家可见的中文结算摘要。
""";
    }

    private static string BuildExploreObservation(GmExploreContext context) {
        var perception = context.Perception;
        var sb = new StringBuilder();

        sb.AppendLine("[任务]");
        sb.AppendLine("结算玩家本回合的 explore Large-Action。");
        sb.AppendLine();
        sb.AppendLine("[当前玩家位置]");
        sb.AppendLine($"- ActorId: {perception.ActorId}");
        sb.AppendLine($"- LocationId: {context.CurrentLocationId}");
        sb.AppendLine($"- Name: {perception.Location.Name}");
        sb.AppendLine($"- Description: {perception.Location.Description}");
        sb.AppendLine();
        sb.AppendLine("[当前可见出口]");
        if (perception.Location.Exits.Count == 0) {
            sb.AppendLine("(none)");
        }
        else {
            foreach (var exit in perception.Location.Exits) {
                sb.AppendLine($"- {exit.Direction} -> {exit.TargetLocationId} ({exit.TargetName})");
            }
        }

        sb.AppendLine();
        sb.AppendLine("[当前可见物品]");
        if (perception.Location.Items.Count == 0) {
            sb.AppendLine("(none)");
        }
        else {
            foreach (var item in perception.Location.Items) {
                sb.AppendLine($"- {item.ItemId}: {item.Name} | {item.Description}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("[当前持有物品]");
        if (perception.InventoryItems.Count == 0) {
            sb.AppendLine("(none)");
        }
        else {
            foreach (var item in perception.InventoryItems) {
                sb.AppendLine($"- {item.ItemId}: {item.Name} | {item.Description}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("[当前可见角色]");
        if (perception.Location.Actors.Count == 0) {
            sb.AppendLine("(none)");
        }
        else {
            foreach (var actor in perception.Location.Actors) {
                sb.AppendLine($"- {actor.ActorId}: {actor.Name} ({actor.Kind}) | {actor.ProfileNote}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("[当前可见交互]");
        AppendInteractions(sb, perception.Location.Interactions);
        foreach (var item in perception.Location.Items) {
            AppendInteractions(sb, item.Interactions);
        }
        foreach (var item in perception.InventoryItems) {
            AppendInteractions(sb, item.Interactions);
        }
        foreach (var actor in perception.Location.Actors) {
            AppendInteractions(sb, actor.Interactions);
        }

        sb.AppendLine();
        sb.AppendLine("[玩家动作]");
        sb.AppendLine($"- ActionKind: large/explore");
        sb.AppendLine($"- Direction: {context.Direction}");
        sb.AppendLine($"- Focus: {context.Focus ?? "(none)"}");
        sb.AppendLine($"- SuggestedReverseDirection: {context.SuggestedReverseDirection ?? "null"}");
        sb.AppendLine();
        sb.AppendLine("[玩家事前推理]");
        sb.AppendLine(context.PreActionReason);
        sb.AppendLine();
        sb.AppendLine("[当前回合已接受步骤]");
        if (perception.AcceptedSteps.Count == 0) {
            sb.AppendLine("(none)");
        }
        else {
            foreach (var step in perception.AcceptedSteps) {
                sb.AppendLine($"- {step.StepNumber}. {step.ActionKind}: {step.ActionSummary}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("[工具使用要求]");
        sb.AppendLine("若 Direction 已存在于当前可见出口，目标 LocationId 必须使用该出口的 TargetLocationId。");
        sb.AppendLine("若 Direction 不存在于当前可见出口，新 LocationId 应使用小写 ASCII、数字和连字符，且能表达来源与方向。");
        sb.AppendLine("新地点 name 优先贴近 Focus；若 Focus 为空，可用“未知区域”等克制命名。");
        sb.AppendLine("新地点 description 只描述入口附近已经确认的低风险信息，不要塞入物品、NPC 或隐藏剧情。");
        sb.AppendLine("如果你创建 Item，item_id 必须稳定；物品描述应是玩家一眼能看到的东西，不要包含隐藏功能。");
        sb.AppendLine("如果你创建 NPC，actor_id 必须稳定；profile_note 只写玩家可感知的外观/姿态和 GM 可用的轻量主持提示，不写隐藏秘密。");
        sb.AppendLine("如果你创建 Interaction，target_ref 使用 location:<id>、item:<id> 或 actor:<id>；visible_label 是玩家看到的动作提示；precondition_note 无特别条件时写 none。");

        return sb.ToString();
    }

    private static string BuildExploreMapStageObservation(GmExploreContext context) {
        var sb = new StringBuilder();
        sb.AppendLine("[阶段 1/3: 地图与移动落账]");
        sb.AppendLine("只处理 Location / Exit / Player location。");
        sb.AppendLine("若目标方向已有出口，调用 gm_move_player。若没有出口，调用 gm_create_location、gm_link_locations、gm_move_player。");
        sb.AppendLine("本阶段不要创建 Item / NPC / Interaction，也不要输出最终摘要；工具完成后停止调用工具，文本可留空。");
        sb.AppendLine();
        sb.Append(BuildExploreObservation(context));
        return sb.ToString();
    }

    private static string BuildExploreLedgerAuditStageObservation(DurableDict<string> root, GmExploreContext context) {
        var perception = GameSimulation.DescribeCurrentPerception(root);
        var sb = new StringBuilder();
        sb.AppendLine("[阶段 2/3: 实体与交互账本审计]");
        sb.AppendLine("检查刚完成的探索结果：如果最终叙事需要提到具体可见物品、NPC 或可执行动作，必须现在用工具落账。");
        sb.AppendLine("可调用 gm_create_item、gm_create_npc、gm_add_interaction、gm_set_visibility、gm_set_interaction_visibility。");
        sb.AppendLine("gm_add_interaction 的 precondition_note 没有特别条件时写 none。");
        sb.AppendLine("本阶段不要移动玩家，不要创建更多地点，不要输出最终摘要；工具完成后停止调用工具，文本可留空。");
        sb.AppendLine();
        AppendExploreIntent(sb, context);
        AppendPerceptionSnapshot(sb, perception, "当前探索后账本投影");
        return sb.ToString();
    }

    private static string BuildExploreSummaryStageObservation(DurableDict<string> root, GmExploreContext context) {
        var perception = GameSimulation.DescribeCurrentPerception(root);
        var sb = new StringBuilder();
        sb.AppendLine("[阶段 3/3: 玩家可见结算摘要]");
        sb.AppendLine("请输出 1 到 3 句中文结算摘要。原则：只描述当前玩家能感知到的内容，只引用已经落账的地点、物品、NPC 和可执行动作。");
        sb.AppendLine("若你发现摘要必需的实体或 affordance 仍未落账，可以最后补充必要工具调用；否则不要调用工具。");
        sb.AppendLine();
        AppendExploreIntent(sb, context);
        AppendPerceptionSnapshot(sb, perception, "最终账本投影");
        return sb.ToString();
    }

    private static string BuildInteractionObservation(GmInteractionContext context) {
        var perception = context.Perception;
        var interaction = context.Interaction;
        var sb = new StringBuilder();

        sb.AppendLine("[任务]");
        sb.AppendLine("结算玩家本回合选择的 interact Large-Action。");
        sb.AppendLine();
        sb.AppendLine("[当前玩家位置]");
        sb.AppendLine($"- ActorId: {perception.ActorId}");
        sb.AppendLine($"- LocationId: {context.CurrentLocationId}");
        sb.AppendLine($"- Name: {perception.Location.Name}");
        sb.AppendLine($"- Description: {perception.Location.Description}");
        sb.AppendLine();
        sb.AppendLine("[玩家选择的交互]");
        sb.AppendLine($"- InteractionId: {interaction.InteractionId}");
        sb.AppendLine($"- Target: {interaction.TargetKind}:{interaction.TargetId}");
        sb.AppendLine($"- ActionKind: {interaction.ActionKind}");
        sb.AppendLine($"- VisibleLabel: {interaction.VisibleLabel}");
        sb.AppendLine($"- PreconditionNote: {interaction.PreconditionNote ?? "(none)"}");
        sb.AppendLine($"- EffectNote: {interaction.EffectNote ?? "(none)"}");
        sb.AppendLine();
        sb.AppendLine("[当前可见出口]");
        if (perception.Location.Exits.Count == 0) {
            sb.AppendLine("(none)");
        }
        else {
            foreach (var exit in perception.Location.Exits) {
                sb.AppendLine($"- {exit.Direction} -> {exit.TargetLocationId} ({exit.TargetName})");
            }
        }

        sb.AppendLine();
        sb.AppendLine("[当前可见物品]");
        if (perception.Location.Items.Count == 0) {
            sb.AppendLine("(none)");
        }
        else {
            foreach (var item in perception.Location.Items) {
                sb.AppendLine($"- {item.ItemId}: {item.Name} | {item.Description}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("[当前持有物品]");
        if (perception.InventoryItems.Count == 0) {
            sb.AppendLine("(none)");
        }
        else {
            foreach (var item in perception.InventoryItems) {
                sb.AppendLine($"- {item.ItemId}: {item.Name} | {item.Description}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("[当前可见角色]");
        if (perception.Location.Actors.Count == 0) {
            sb.AppendLine("(none)");
        }
        else {
            foreach (var actor in perception.Location.Actors) {
                sb.AppendLine($"- {actor.ActorId}: {actor.Name} ({actor.Kind}) | {actor.ProfileNote}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("[当前可见交互]");
        AppendInteractions(sb, perception.Location.Interactions);
        foreach (var item in perception.Location.Items) {
            AppendInteractions(sb, item.Interactions);
        }
        foreach (var item in perception.InventoryItems) {
            AppendInteractions(sb, item.Interactions);
        }
        foreach (var actor in perception.Location.Actors) {
            AppendInteractions(sb, actor.Interactions);
        }

        sb.AppendLine();
        sb.AppendLine("[玩家事前推理]");
        sb.AppendLine(context.PreActionReason);
        sb.AppendLine();
        sb.AppendLine("[当前回合已接受步骤]");
        if (perception.AcceptedSteps.Count == 0) {
            sb.AppendLine("(none)");
        }
        else {
            foreach (var step in perception.AcceptedSteps) {
                sb.AppendLine($"- {step.StepNumber}. {step.ActionKind}: {step.ActionSummary}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("[工具使用要求]");
        sb.AppendLine("优先根据所选 Interaction 的 target、actionKind、visibleLabel、preconditionNote 和 effectNote 裁决，不要把玩家事前推理当成事实来源。");
        sb.AppendLine("若创建 Item/NPC/Interaction，ID 必须稳定、唯一，并且 target_ref 必须引用已经存在的对象；precondition_note 无特别条件时写 none。");
        sb.AppendLine("若只是检查、倾听、交谈第一句等低风险反馈，可以不改账本，只输出玩家可见摘要。");
        sb.AppendLine("若当前 interaction 已被消耗或下一回合不应继续显示，请用 gm_set_interaction_visibility 把它设为 hidden。");
        sb.AppendLine("若玩家拿起或放下物品，请用 gm_move_item_to_actor 或 gm_place_item_at_location 更新 ownerActorId/locationId。当前终端玩家 ActorId 是 player。");
        sb.AppendLine("若摘要提到新的可见物品、人物或可执行动作，必须先调用相应工具落账。");

        return sb.ToString();
    }

    private static string BuildInteractionConsequenceStageObservation(GmInteractionContext context) {
        var sb = new StringBuilder();
        sb.AppendLine("[阶段 1/3: 交互直接后果]");
        sb.AppendLine("只处理玩家选择的 interaction 的直接 hard truth 后果。可创建/显示物品或 NPC，可调整可见性，可在有充分依据时移动玩家。");
        sb.AppendLine("不要补一长串后续按钮；不要输出最终摘要。工具完成后停止调用工具，文本可留空。");
        sb.AppendLine();
        sb.Append(BuildInteractionObservation(context));
        return sb.ToString();
    }

    private static string BuildInteractionAffordanceAuditStageObservation(DurableDict<string> root, GmInteractionContext context) {
        var perception = GameSimulation.DescribeCurrentPerception(root);
        var sb = new StringBuilder();
        sb.AppendLine("[阶段 2/3: affordance 生命周期审计]");
        sb.AppendLine("检查交互后玩家下一回合应该看到哪些可执行动作。");
        sb.AppendLine("若当前 interaction 已经被消耗或下一回合不该重复显示，用 gm_set_interaction_visibility 设为 hidden。");
        sb.AppendLine("若出现新的合理后续动作，用 gm_add_interaction 落账；precondition_note 无特别条件时写 none。");
        sb.AppendLine("本阶段不要输出最终摘要；工具完成后停止调用工具，文本可留空。");
        sb.AppendLine();
        AppendInteractionIntent(sb, context);
        AppendPerceptionSnapshot(sb, perception, "交互后账本投影");
        return sb.ToString();
    }

    private static string BuildInteractionSummaryStageObservation(DurableDict<string> root, GmInteractionContext context) {
        var perception = GameSimulation.DescribeCurrentPerception(root);
        var sb = new StringBuilder();
        sb.AppendLine("[阶段 3/3: 玩家可见结算摘要]");
        sb.AppendLine("请输出 1 到 3 句中文结算摘要。原则：只描述当前玩家能感知到的内容，只引用已经落账的物品、NPC 和可执行动作。");
        sb.AppendLine("若你发现摘要必需的新实体或 affordance 仍未落账，可以最后补充必要工具调用；否则不要调用工具。");
        sb.AppendLine();
        AppendInteractionIntent(sb, context);
        AppendPerceptionSnapshot(sb, perception, "最终账本投影");
        return sb.ToString();
    }

    private static string BuildCollectedTurnConsequenceStageObservation(GmCollectedTurnContext context) {
        var sb = new StringBuilder();
        sb.AppendLine("[阶段 1/3: 多主体意图裁决与 hard truth 落账]");
        sb.AppendLine("统一裁决所有 active player actor 的 Large-Action。只处理必要 hard truth：Location/Exit/Actor location/Item ownership/NPC/Interaction/visibility。");
        sb.AppendLine("本阶段不要输出最终摘要。工具完成后停止调用工具，文本可留空。");
        sb.AppendLine();
        AppendCollectedTurnIntents(sb, context);
        return sb.ToString();
    }

    private static string BuildCollectedTurnLedgerAuditStageObservation(DurableDict<string> root, GmCollectedTurnContext context) {
        var sb = new StringBuilder();
        sb.AppendLine("[阶段 2/3: 多主体账本审计]");
        sb.AppendLine("检查刚才的多主体结算是否遗漏必要账本：终端玩家/LLM Player 的位置、物品位置/持有者、新地点出口、可见 NPC、可执行 interaction。");
        sb.AppendLine("如果最终摘要或下一回合 Perception-Bundle 需要某实体或 affordance，请现在补工具调用。不要输出最终摘要。工具完成后停止调用工具，文本可留空。");
        sb.AppendLine();
        AppendCollectedTurnIntents(sb, context);
        AppendPerceptionSnapshot(sb, GameSimulation.DescribePerceptionForActor(root, context.TerminalActorId), "当前终端玩家账本投影");
        foreach (var intent in context.Intents.Where(intent => !string.Equals(intent.ActorId, context.TerminalActorId, StringComparison.Ordinal))) {
            AppendPerceptionSnapshot(sb, GameSimulation.DescribePerceptionForActor(root, intent.ActorId), $"当前 {intent.ActorName} 账本投影");
        }

        return sb.ToString();
    }

    private static string BuildCollectedTurnSummaryStageObservation(DurableDict<string> root, GmCollectedTurnContext context) {
        var sb = new StringBuilder();
        sb.AppendLine("[阶段 3/3: 终端玩家可见结算摘要]");
        sb.AppendLine("请输出 1 到 4 句中文结算摘要。原则：面向终端玩家角色，只写它能感知到、参与声明时公开知道、或自然能推断的内容。不要泄露其它 actor 离开视野后的隐藏发现。");
        sb.AppendLine("若你发现摘要必需的新实体或 affordance 仍未落账，可以最后补充必要工具调用；否则不要调用工具。");
        sb.AppendLine();
        AppendCollectedTurnIntents(sb, context);
        AppendPerceptionSnapshot(sb, GameSimulation.DescribePerceptionForActor(root, context.TerminalActorId), "最终终端玩家账本投影");
        return sb.ToString();
    }

    private static void AppendExploreIntent(StringBuilder sb, GmExploreContext context) {
        sb.AppendLine("[探索意图]");
        sb.AppendLine($"- OriginalLocationId: {context.CurrentLocationId}");
        sb.AppendLine($"- Direction: {context.Direction}");
        sb.AppendLine($"- Focus: {context.Focus ?? "(none)"}");
        sb.AppendLine($"- SuggestedReverseDirection: {context.SuggestedReverseDirection ?? "null"}");
        sb.AppendLine("- PreActionReason:");
        sb.AppendLine(context.PreActionReason);
        sb.AppendLine();
    }

    private static void AppendInteractionIntent(StringBuilder sb, GmInteractionContext context) {
        var interaction = context.Interaction;
        sb.AppendLine("[交互意图]");
        sb.AppendLine($"- OriginalLocationId: {context.CurrentLocationId}");
        sb.AppendLine($"- InteractionId: {interaction.InteractionId}");
        sb.AppendLine($"- Target: {interaction.TargetKind}:{interaction.TargetId}");
        sb.AppendLine($"- ActionKind: {interaction.ActionKind}");
        sb.AppendLine($"- VisibleLabel: {interaction.VisibleLabel}");
        sb.AppendLine($"- PreconditionNote: {interaction.PreconditionNote ?? "(none)"}");
        sb.AppendLine($"- EffectNote: {interaction.EffectNote ?? "(none)"}");
        sb.AppendLine("- PreActionReason:");
        sb.AppendLine(context.PreActionReason);
        sb.AppendLine();
    }

    private static void AppendCollectedTurnIntents(StringBuilder sb, GmCollectedTurnContext context) {
        sb.AppendLine("[本回合 active player actor 的 Large-Action 声明]");
        foreach (var intent in context.Intents) {
            sb.AppendLine($"- Actor: {intent.ActorName} [{intent.ActorId}, {intent.ActorKind}]");
            sb.AppendLine($"  ActionKind: {intent.ActionKind}");
            sb.AppendLine($"  ActionSummary: {intent.ActionSummary}");
            sb.AppendLine($"  ActionPayload: {intent.ActionPayload ?? "(none)"}");
            sb.AppendLine($"  ValidatorFeedback: {intent.ValidatorFeedback}");
            sb.AppendLine("  PreActionReason:");
            sb.AppendLine(Indent(intent.PreActionReason, "    "));
            AppendPerceptionSnapshot(sb, intent.Perception, $"行动前私有 Perception-Bundle: {intent.ActorName}");
        }

        sb.AppendLine("[裁决提示]");
        sb.AppendLine("- large/rest-a-while: 通常只推进回合，不主动改变位置；可作为保守观察。");
        sb.AppendLine("- large/explore: payload 通常包含 direction=... 和可选 focus=...；从该 actor 行动前所在 Location 出发裁决。");
        sb.AppendLine("- large/interact: payload 通常包含 interactionId、target、actionKind、visibleLabel；只能裁决该 actor 行动前可见的 interaction。");
        sb.AppendLine("- 若两个 actor 同时探索同一未知方向，可复用同一个新 Location，不要重复创建等价地点。");
        sb.AppendLine("- 若 actor 分头行动，终端玩家最终摘要不要泄露它看不见的远处细节。");
        sb.AppendLine();
    }

    private static void AppendPerceptionSnapshot(StringBuilder sb, PerceptionBundle perception, string title) {
        sb.AppendLine($"[{title}]");
        sb.AppendLine($"- ActorId: {perception.ActorId}");
        sb.AppendLine($"- ActorName: {perception.ActorName}");
        sb.AppendLine($"- ActorKind: {perception.ActorKind}");
        sb.AppendLine($"- ActorProfileNote: {perception.ActorProfileNote}");
        sb.AppendLine($"- Time: {GameClock.FormatClock(perception.Day, perception.Slot, perception.SlotsPerDay)}");
        sb.AppendLine($"- LocationId: {perception.Location.LocationId}");
        sb.AppendLine($"- LocationName: {perception.Location.Name}");
        sb.AppendLine($"- LocationDescription: {perception.Location.Description}");
        sb.AppendLine("- Exits:");
        if (perception.Location.Exits.Count == 0) {
            sb.AppendLine("  (none)");
        }
        else {
            foreach (var exit in perception.Location.Exits) {
                sb.AppendLine($"  - {exit.Direction} -> {exit.TargetLocationId} ({exit.TargetName})");
            }
        }

        sb.AppendLine("- VisibleItems:");
        if (perception.Location.Items.Count == 0) {
            sb.AppendLine("  (none)");
        }
        else {
            foreach (var item in perception.Location.Items) {
                sb.AppendLine($"  - {item.ItemId}: {item.Name} | {item.Description}");
                AppendInteractions(sb, item.Interactions);
            }
        }

        sb.AppendLine("- InventoryItems:");
        if (perception.InventoryItems.Count == 0) {
            sb.AppendLine("  (none)");
        }
        else {
            foreach (var item in perception.InventoryItems) {
                sb.AppendLine($"  - {item.ItemId}: {item.Name} | {item.Description}");
                AppendInteractions(sb, item.Interactions);
            }
        }

        sb.AppendLine("- VisibleActors:");
        if (perception.Location.Actors.Count == 0) {
            sb.AppendLine("  (none)");
        }
        else {
            foreach (var actor in perception.Location.Actors) {
                sb.AppendLine($"  - {actor.ActorId}: {actor.Name} ({actor.Kind}) | {actor.ProfileNote}");
                AppendInteractions(sb, actor.Interactions);
            }
        }

        sb.AppendLine("- LocationInteractions:");
        AppendInteractions(sb, perception.Location.Interactions);
        sb.AppendLine();
    }

    private static string Indent(string text, string indentation) {
        if (string.IsNullOrEmpty(text)) { return string.Empty; }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return string.Join("\n", lines.Select(line => indentation + line));
    }

    private static void AppendInteractions(StringBuilder sb, IReadOnlyList<InteractionPerception> interactions) {
        if (interactions.Count == 0) { return; }

        foreach (var interaction in interactions) {
            var precondition = string.IsNullOrWhiteSpace(interaction.PreconditionNote)
                ? "none"
                : interaction.PreconditionNote;
            sb.AppendLine($"- {interaction.InteractionId}: {interaction.TargetKind}:{interaction.TargetId} | {interaction.ActionKind} | {interaction.VisibleLabel} | precondition: {precondition}");
        }
    }

    private static string BuildToolResultsObservation(string stageName, IReadOnlyList<ToolCallExecutionResult> results) {
        var sb = new StringBuilder();
        sb.AppendLine($"[工具执行结果: {stageName}]");
        foreach (var result in results) {
            sb.AppendLine($"- {result.ToolName}#{result.ToolCallId}: {result.ExecuteResult.Status}");
            sb.AppendLine(result.ExecuteResult.Content);
        }

        sb.AppendLine();
        sb.AppendLine("如果本阶段必要工具都成功，请停止调用工具。若工具失败，请修正参数后继续。最终摘要只应在 summary 阶段输出。");
        return sb.ToString();
    }

    private static DeepSeekV4ChatClient GetClient(GmConfig config) {
        lock (s_gate) {
            if (s_client is not null) { return s_client; }

            s_client = new DeepSeekV4ChatClient(
                apiKey: config.ApiKey,
                baseAddress: string.IsNullOrWhiteSpace(config.BaseAddress) ? null : new Uri(config.BaseAddress),
                options: new OpenAIChatClientOptions {
                    ExtraBody = new JsonObject {
                        ["thinking"] = new JsonObject {
                            ["type"] = "enabled"
                        },
                        ["reasoning_effort"] = "high"
                    }
                }
            );
            return s_client;
        }
    }

    private static GmConfig GetConfig() {
        lock (s_gate) {
            if (s_config is not null) { return s_config; }

            s_config = new GmConfig(
                BaseAddress: GetOptionalEnvironment(BaseAddressEnv),
                ModelId: GetEnvironmentOrDefault(ModelIdEnv, GetEnvironmentOrDefault(FallbackModelIdEnv, DefaultModelId)),
                ApiKey: GetOptionalEnvironment(ApiKeyEnv),
                Mode: GetEnvironmentOrDefault(ModeEnv, "auto"),
                MaxRounds: GetPositiveIntEnvironment(MaxRoundsEnv, DefaultMaxRounds)
            );
            return s_config;
        }
    }

    private static string NormalizeSummary(string text) {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (normalized.Length <= 800) { return normalized; }

        return string.Concat(normalized.AsSpan(0, 800), "...");
    }

    private static string BuildProviderErrorMessage(IReadOnlyList<string> errors) {
        var message = string.Join(
            "; ",
            errors
                .Select(static error => error?.Trim())
                .Where(static error => !string.IsNullOrWhiteSpace(error))
        );

        return string.IsNullOrWhiteSpace(message)
            ? "Unknown provider error."
            : message;
    }

    private static int GetPositiveIntEnvironment(string key, int defaultValue) {
        var value = Environment.GetEnvironmentVariable(key);
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : defaultValue;
    }

    private static string GetEnvironmentOrDefault(string key, string defaultValue) {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
    }

    private static string? GetOptionalEnvironment(string key) {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
