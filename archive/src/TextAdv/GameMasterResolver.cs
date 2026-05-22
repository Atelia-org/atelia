using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using Atelia.Completion.Transport;
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

internal sealed record GmInteractionEffectContext(
    PerceptionBundle ActorPerception,
    string CurrentLocationId,
    InteractionPerception Interaction,
    string PreActionReason,
    string EffectSlot,
    PerceptionBundle TerminalPerception,
    bool TerminalCanObserveActor
);

internal sealed record GmInteractionEffectResolution(
    string ActorFacingSummary,
    string? TerminalVisibleSummary,
    bool UsedLlm,
    string? FallbackReason
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

internal sealed record GameMasterStub(
    Func<DurableDict<string>, GmExploreContext, CancellationToken, Task<GmExploreResolution>>? ExploreResolver = null,
    Func<DurableDict<string>, GmInteractionContext, CancellationToken, Task<GmExploreResolution>>? InteractionResolver = null,
    Func<DurableDict<string>, GmInteractionEffectContext, CancellationToken, Task<GmInteractionEffectResolution>>? InteractionEffectResolver = null,
    Func<DurableDict<string>, GmInteractionContext, CancellationToken, Task<GmExploreResolution>>? ImmediateSelfInteractionResolver = null,
    Func<DurableDict<string>, GmCollectedTurnContext, CancellationToken, Task<GmExploreResolution>>? CollectedTurnResolver = null
);

internal static class GameMasterResolver {
    private const string BaseAddressEnv = "DEEPSEEK_BASE_URL";
    private const string ModelIdEnv = "ATELIA_TEXTADV_GM_MODEL_ID";
    private const string FallbackModelIdEnv = "DEEPSEEK_MODEL";
    private const string ApiKeyEnv = "DEEPSEEK_API_KEY";
    private const string MaxRoundsEnv = "ATELIA_TEXTADV_GM_MAX_ROUNDS";
    private const string DefaultModelId = "deepseek-v4-flash";
    private const string DefaultBaseAddress = "https://api.deepseek.com/";
    private const int DefaultMaxRounds = 4;
    private const string ViewSafeNamingContractPromptLine =
        "所有会暴露给玩家的命名都必须视角安全：location / item 的名称与描述，以及 visibleLabel / interaction_id，都不能在识别前剧透真相。";
    private static readonly string ItemRefreshWithUpdateToolPromptLine =
        $"若这一步确认了既有 item 的真实身份，或其外观状态已变化，调用 {GmToolCatalog.UpdateItemToolName} 刷新名称或描述。";

    private static readonly Lock s_gate = new();
    private static DeepSeekV4ChatClient? s_client;
    private static HttpClient? s_httpClient;
    private static GmConfig? s_config;
    private static GameMasterStub? s_stub;

    private sealed record GmConfig(
        string? BaseAddress,
        string ModelId,
        string? ApiKey,
        int MaxRounds
    );

    private sealed record GmResolutionStage(
        string Name,
        Func<string> BuildObservation,
        bool RequireFinalSummary,
        GmToolCatalog.ToolSet ToolSet,
        Func<GmStageRunResult, string?>? ValidatePostcondition = null
    );

    private readonly record struct GmStageRunResult(
        bool Completed,
        string RawText,
        string LastText
    );

    private sealed record GmResolutionFlow<TResult>(string SystemPrompt, IReadOnlyList<GmResolutionStage> Stages, GmFinalOutputPolicy<TResult> OutputPolicy);

    private readonly record struct GmFinalOutputPolicy<TResult>(
        Func<string, TResult> ParseFinalText,
        Func<TResult> BuildDefaultResult
    );

    private static GmResolutionStage ToolStage(
        string name,
        Func<string> buildObservation,
        GmToolCatalog.ToolSet toolSet,
        Func<GmStageRunResult, string?>? validatePostcondition = null
    ) => new(name, buildObservation, RequireFinalSummary: false, toolSet, validatePostcondition);

    private static GmResolutionStage SummaryStage(
        string name,
        Func<string> buildObservation,
        GmToolCatalog.ToolSet toolSet,
        Func<GmStageRunResult, string?>? validatePostcondition = null
    ) => new(name, buildObservation, RequireFinalSummary: true, toolSet, validatePostcondition);

    private static GmResolutionFlow<TResult> CreateThreeStageFlow<TResult>(
        string systemPrompt,
        GmResolutionStage firstStage,
        GmResolutionStage secondStage,
        GmResolutionStage finalStage,
        GmFinalOutputPolicy<TResult> outputPolicy
    ) => new(systemPrompt, [firstStage, secondStage, finalStage], outputPolicy);

    internal static async Task<GmExploreResolution> ResolveExploreAsync(
        DurableDict<string> root,
        GmExploreContext context,
        CancellationToken cancellationToken
    ) {
        var stub = GetStub();
        if (stub?.ExploreResolver is not null) { return await stub.ExploreResolver(root, context, cancellationToken).ConfigureAwait(false); }

        var config = GetConfig();
        return await ResolveExploreWithLlmAsync(root, context, config, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<GmExploreResolution> ResolveInteractionAsync(
        DurableDict<string> root,
        GmInteractionContext context,
        CancellationToken cancellationToken
    ) {
        var stub = GetStub();
        if (stub?.InteractionResolver is not null) { return await stub.InteractionResolver(root, context, cancellationToken).ConfigureAwait(false); }

        var config = GetConfig();
        return await ResolveInteractionWithLlmAsync(root, context, config, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<GmInteractionEffectResolution> ResolveInteractionEffectAsync(
        DurableDict<string> root,
        GmInteractionEffectContext context,
        CancellationToken cancellationToken
    ) {
        var stub = GetStub();
        if (stub?.InteractionEffectResolver is not null) { return await stub.InteractionEffectResolver(root, context, cancellationToken).ConfigureAwait(false); }

        var config = GetConfig();
        return await ResolveInteractionEffectWithLlmAsync(root, context, config, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<GmExploreResolution> ResolveImmediateSelfInteractionAsync(
        DurableDict<string> root,
        GmInteractionContext context,
        CancellationToken cancellationToken
    ) {
        var stub = GetStub();
        if (stub?.ImmediateSelfInteractionResolver is not null) { return await stub.ImmediateSelfInteractionResolver(root, context, cancellationToken).ConfigureAwait(false); }

        var config = GetConfig();
        return await ResolveImmediateSelfInteractionWithLlmAsync(root, context, config, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<GmExploreResolution> ResolveCollectedTurnAsync(
        DurableDict<string> root,
        GmCollectedTurnContext context,
        CancellationToken cancellationToken
    ) {
        var stub = GetStub();
        if (stub?.CollectedTurnResolver is not null) { return await stub.CollectedTurnResolver(root, context, cancellationToken).ConfigureAwait(false); }

        var config = GetConfig();
        return await ResolveCollectedTurnWithLlmAsync(root, context, config, cancellationToken).ConfigureAwait(false);
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
            CreateExploreFlow(root, context),
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
            CreateInteractionFlow(root, context),
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
            CreateCollectedTurnFlow(root, context),
            cancellationToken
        ).ConfigureAwait(false);
    }

    private static async Task<GmExploreResolution> ResolveImmediateSelfInteractionWithLlmAsync(
        DurableDict<string> root,
        GmInteractionContext context,
        GmConfig config,
        CancellationToken cancellationToken
    ) {
        return await RunStagedToolLoopAsync(
            root,
            config,
            CreateImmediateSelfInteractionFlow(root, context),
            cancellationToken
        ).ConfigureAwait(false);
    }

    private static async Task<GmInteractionEffectResolution> ResolveInteractionEffectWithLlmAsync(
        DurableDict<string> root,
        GmInteractionEffectContext context,
        GmConfig config,
        CancellationToken cancellationToken
    ) {
        return await RunStagedToolLoopAsync(
            root,
            config,
            CreateInteractionEffectFlow(root, context),
            cancellationToken
        ).ConfigureAwait(false);
    }

    private static async Task<TResult> RunStagedToolLoopAsync<TResult>(
        DurableDict<string> root,
        GmConfig config,
        GmResolutionFlow<TResult> flow,
        CancellationToken cancellationToken
    ) {
        var history = new List<IHistoryMessage>();
        var client = GetClient(config);
        var toolExecutors = new Dictionary<GmToolCatalog.ToolSet, ToolExecutor>();

        foreach (var stage in flow.Stages) {
            var toolExecutor = GetOrCreateToolExecutor(root, stage.ToolSet, toolExecutors);
            var visibleToolDefinitions = toolExecutor.GetVisibleToolDefinitions();
            history.Add(new ObservationMessage(BuildStageObservation(stage, visibleToolDefinitions)));
            var stageResult = await RunStageAsync(
                client,
                config,
                flow.SystemPrompt,
                history,
                stage,
                toolExecutor,
                visibleToolDefinitions,
                cancellationToken
            ).ConfigureAwait(false);

            if (stageResult.Completed) {
                if (stage.ValidatePostcondition is not null) {
                    var validationError = stage.ValidatePostcondition(stageResult);
                    if (!string.IsNullOrWhiteSpace(validationError)) { throw new InvalidOperationException(validationError); }
                }

                if (!stage.RequireFinalSummary) { continue; }
                return flow.OutputPolicy.ParseFinalText(stageResult.RawText);
            }

            if (stage.RequireFinalSummary) {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(stageResult.LastText)
                        ? $"GM Agent 在阶段 {stage.Name} 的 {config.MaxRounds} 轮内没有完成结算摘要。"
                        : $"GM Agent 在阶段 {stage.Name} 的 {config.MaxRounds} 轮内仍未停止调用工具。最后文本：{stageResult.LastText}"
                );
            }

            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(stageResult.LastText)
                    ? $"GM Agent 在阶段 {stage.Name} 的 {config.MaxRounds} 轮内没有完成工具阶段。"
                    : $"GM Agent 在阶段 {stage.Name} 的 {config.MaxRounds} 轮内仍未停止调用工具。最后文本：{stageResult.LastText}"
            );
        }

        return flow.OutputPolicy.BuildDefaultResult();
    }

    private static GmFinalOutputPolicy<GmExploreResolution> CreateSummaryOutputPolicy(string defaultSummary) {
        return new GmFinalOutputPolicy<GmExploreResolution>(
            ParseFinalText: text => {
                var finalSummary = NormalizeSummary(text);
                if (string.IsNullOrWhiteSpace(finalSummary)) {
                    finalSummary = defaultSummary;
                }

                return new GmExploreResolution(finalSummary, UsedLlm: true, FallbackReason: null);
            },
            BuildDefaultResult: () => new GmExploreResolution(defaultSummary, UsedLlm: true, FallbackReason: null)
        );
    }

    private static GmFinalOutputPolicy<GmInteractionEffectResolution> CreateInteractionEffectOutputPolicy(
        bool terminalCanObserveActor
    ) {
        return new GmFinalOutputPolicy<GmInteractionEffectResolution>(
            ParseFinalText: text => {
                var parsed = ParseInteractionEffectSummary(text, terminalCanObserveActor);
                return new GmInteractionEffectResolution(
                    parsed.ActorFacingSummary,
                    parsed.TerminalVisibleSummary,
                    UsedLlm: true,
                    FallbackReason: null
                );
            },
            BuildDefaultResult: () => new GmInteractionEffectResolution(
                ActorFacingSummary: "这一步效果已经产生，只是细节暂时不足。",
                TerminalVisibleSummary: terminalCanObserveActor ? "你注意到这一步效果已经产生，只是细节暂时不足。" : null,
                UsedLlm: true,
                FallbackReason: null
            )
        );
    }

    private static GmResolutionFlow<GmExploreResolution> CreateExploreFlow(
        DurableDict<string> root,
        GmExploreContext context
    ) {
        return CreateThreeStageFlow(
            systemPrompt: BuildExploreSystemPrompt(),
            firstStage: ToolStage(
                "explore-map",
                () => BuildExploreMapStageObservation(context),
                GmToolCatalog.ToolSets.ExploreMap,
                validatePostcondition: _ => ValidateExploreMapStageCompletion(root, context)
            ),
            secondStage: ToolStage(
                "explore-ledger-audit",
                () => BuildExploreLedgerAuditStageObservation(root, context),
                GmToolCatalog.ToolSets.ExploreAudit
            ),
            finalStage: SummaryStage(
                "explore-summary",
                () => BuildExploreSummaryStageObservation(root, context),
                GmToolCatalog.ToolSets.ExploreAudit
            ),
            outputPolicy: CreateSummaryOutputPolicy("GM Agent 完成了本回合探索结算。")
        );
    }

    private static GmResolutionFlow<GmExploreResolution> CreateInteractionFlow(
        DurableDict<string> root,
        GmInteractionContext context
    ) {
        return CreateThreeStageFlow(
            systemPrompt: BuildInteractionSystemPrompt(),
            firstStage: ToolStage(
                "interaction-consequence",
                () => BuildInteractionConsequenceStageObservation(context),
                GmToolCatalog.ToolSets.InteractionConsequence
            ),
            secondStage: ToolStage(
                "interaction-affordance-audit",
                () => BuildInteractionAffordanceAuditStageObservation(root, context),
                GmToolCatalog.ToolSets.InteractionAudit
            ),
            finalStage: SummaryStage(
                "interaction-summary",
                () => BuildInteractionSummaryStageObservation(root, context),
                GmToolCatalog.ToolSets.InteractionConsequence
            ),
            outputPolicy: CreateSummaryOutputPolicy("GM Agent 完成了本回合交互结算。")
        );
    }

    private static GmResolutionFlow<GmExploreResolution> CreateCollectedTurnFlow(
        DurableDict<string> root,
        GmCollectedTurnContext context
    ) {
        return CreateThreeStageFlow(
            systemPrompt: BuildCollectedTurnSystemPrompt(),
            firstStage: ToolStage(
                "collected-turn-consequence",
                () => BuildCollectedTurnConsequenceStageObservation(context),
                GmToolCatalog.ToolSets.CollectedTurnCore
            ),
            secondStage: ToolStage(
                "collected-turn-ledger-audit",
                () => BuildCollectedTurnLedgerAuditStageObservation(root, context),
                GmToolCatalog.ToolSets.CollectedTurnCore
            ),
            finalStage: SummaryStage(
                "collected-turn-summary",
                () => BuildCollectedTurnSummaryStageObservation(root, context),
                GmToolCatalog.ToolSets.CollectedTurnSummary,
                validatePostcondition: _ => ValidateCollectedTurnSummaryStageCompletion(root, context)
            ),
            outputPolicy: CreateSummaryOutputPolicy("GM Agent 完成了本回合多主体结算。")
        );
    }

    private static GmResolutionFlow<GmExploreResolution> CreateImmediateSelfInteractionFlow(
        DurableDict<string> root,
        GmInteractionContext context
    ) {
        return CreateThreeStageFlow(
            systemPrompt: BuildImmediateSelfInteractionSystemPrompt(),
            firstStage: ToolStage(
                "immediate-self-consequence",
                () => BuildImmediateSelfInteractionConsequenceStageObservation(context),
                GmToolCatalog.ToolSets.ImmediateSelfConsequence
            ),
            secondStage: ToolStage(
                "immediate-self-affordance-audit",
                () => BuildImmediateSelfInteractionAffordanceAuditStageObservation(root, context),
                GmToolCatalog.ToolSets.ImmediateSelfAudit
            ),
            finalStage: SummaryStage(
                "immediate-self-summary",
                () => BuildImmediateSelfInteractionSummaryStageObservation(root, context),
                GmToolCatalog.ToolSets.ImmediateSelfSummary
            ),
            outputPolicy: CreateSummaryOutputPolicy("你顺手做了这个动作，并得到了一点当前能确认的即时反馈。")
        );
    }

    private static GmResolutionFlow<GmInteractionEffectResolution> CreateInteractionEffectFlow(
        DurableDict<string> root,
        GmInteractionEffectContext context
    ) {
        return CreateThreeStageFlow(
            systemPrompt: BuildInteractionEffectSystemPrompt(),
            firstStage: ToolStage(
                "interaction-effect-consequence",
                () => BuildInteractionEffectConsequenceStageObservation(context),
                GmToolCatalog.ToolSets.InteractionEffectConsequence
            ),
            secondStage: ToolStage(
                "interaction-effect-audit",
                () => BuildInteractionEffectAuditStageObservation(root, context),
                GmToolCatalog.ToolSets.InteractionEffectAudit
            ),
            finalStage: SummaryStage(
                "interaction-effect-summary",
                () => BuildInteractionEffectSummaryStageObservation(root, context),
                GmToolCatalog.ToolSets.InteractionEffectConsequence
            ),
            outputPolicy: CreateInteractionEffectOutputPolicy(context.TerminalCanObserveActor)
        );
    }

    private static string BuildExploreSystemPrompt() {
        return $$"""
你是 TextAdv 的 TRPG GM Agent，负责把玩家的探索类 Large-Action 结算成世界账本变化。

你的主持风格应接近优秀的人类 TRPG 主持人：
1. 公平：只依据当前输入和账本，不奖励偷用信息，也不随意惩罚合理试探。
2. 连续：尊重已有地点、出口和玩家位置；不要改写既有历史。
3. 具体：可以给新地点一点感官细节，但不要把未落账内容当硬事实。
4. 克制：首版主要处理 Location 创建、地点连接、玩家移动；只在新地点确实需要可见细节时创建少量 Item / NPC / Interaction，不要创建复杂规则。
5. 视角安全：最终文本只写当前玩家可以感知到的结算，不泄露隐藏真相。

你必须通过工具更新世界状态：
- 如果目标方向已有出口，只调用 {{GmToolCatalog.MoveActorToolName}}(player, target_location_id) 移动到该目标 LocationId。
- 如果目标方向没有出口，先调用 {{GmToolCatalog.CreateLocationToolName}}，再调用 {{GmToolCatalog.LinkLocationsToolName}}，最后调用 {{GmToolCatalog.MoveActorToolName}}(player, new_location_id)。
- 可选：若新地点需要一个可见可操作细节，可以创建 0 到 1 个 Item 或 0 到 1 个 NPC，再调用 {{GmToolCatalog.AddInteractionToolName}} 给新地点、该 Item 或该 Actor 添加 0 到 2 个交互 affordance；precondition_note 若无特别条件写 none，且必须同时给出 turn_cost / effect_scope / effect_slots。
- {{ViewSafeNamingContractPromptLine}} {{ItemRefreshWithUpdateToolPromptLine}}
- 编写 location description 时避免写死“晨光”“黄昏”“昏暗天光”这类会随时段失真的锚定词，除非它描述的是恒定光源或不随时间变化的事实。
- 如果最终摘要提到玩家能看见的具体物品或人物，该实体必须已经通过 {{GmToolCatalog.CreateItemToolName}} 或 {{GmToolCatalog.CreateNpcToolName}} 落账；若可以对话、检查、拿取或操作，必须通过 {{GmToolCatalog.AddInteractionToolName}} 落账。
- 若有建议反向方向，应在 {{GmToolCatalog.LinkLocationsToolName}} 中填写 reverse_direction；否则传 null。
- 不要用普通文本声称世界已改变；只有工具调用成功才算落账。
- 必要工具调用完成后，停止调用工具，并输出 1 到 3 句玩家可见的中文结算摘要。
""";
    }

    private static string BuildCollectedTurnSystemPrompt() {
        return $$"""
你是 TextAdv 的 TRPG GM Agent，负责在离散回合制下统一裁决多个 active player actor 的 Large-Action。

你的主持方式应接近优秀的人类 TRPG 主持人：
1. 先把每个玩家的声明视为“意图”，不是已经发生的事实。
2. 根据每个 actor 行动前自己的 Perception-Bundle 判断其意图是否合理、是否冲突、是否能同时发生。
3. 冲突时优先使用常识、位置、可见信息和行动风险裁决；不要引入复杂 initiative、耗时或 reservation 系统。
4. 只把 hard truth 变化落到账本：地点、出口、actor 位置、物品持有/放置、NPC、interaction、可见性。
5. 克制生成：每个回合只创建必要的新 Location / Item / NPC / Interaction，避免一次扩张太多。
6. 视角安全：最终摘要面向终端玩家角色，只描述该角色可感知或在本回合声明阶段公开知道的内容，不泄露隐藏真相。

工具规则：
- 移动任意 actor 时优先调用 {{GmToolCatalog.MoveActorToolName}}；终端玩家 ActorId 是 player。
- 若某 actor 探索已有出口，调用 {{GmToolCatalog.MoveActorToolName}}(actor_id, target_location_id)。
- 若某 actor 探索未知方向，先 {{GmToolCatalog.CreateLocationToolName}}，再 {{GmToolCatalog.LinkLocationsToolName}}，最后 {{GmToolCatalog.MoveActorToolName}}。
- 若交互揭示新物品/NPC/动作，必须用 {{GmToolCatalog.CreateItemToolName}} / {{GmToolCatalog.CreateNpcToolName}} / {{GmToolCatalog.AddInteractionToolName}} 落账；{{GmToolCatalog.AddInteractionToolName}} 时必须明确给出 turn_cost / effect_scope / effect_slots。
- {{ViewSafeNamingContractPromptLine}} {{ItemRefreshWithUpdateToolPromptLine}}
- 若 take / give / drop / place 导致持有关系变化，必须用 {{GmToolCatalog.MoveItemToActorToolName}} 或 {{GmToolCatalog.PlaceItemAtLocationToolName}} 落账。
- 若 interaction 被消耗或不应继续显示，调用 {{GmToolCatalog.SetInteractionVisibilityToolName}}。
- 在 summary 阶段必须为每个 active player actor 调用 {{GmToolCatalog.SetActorResolutionToolName}}，写入它下一回合可见的私有反馈；不同 actor 只能知道自己可感知或合理推断的信息。
- 不要用普通文本声称世界已改变；只有工具调用成功才算落账。
- 必要工具调用完成后，停止调用工具，并在 summary 阶段输出 1 到 4 句中文结算摘要。
""";
    }

    private static string BuildInteractionSystemPrompt() {
        return $$"""
你是 TextAdv 的 TRPG GM Agent，负责把玩家选择的 Interaction affordance 结算成世界账本变化。

你的主持风格应接近优秀的人类 TRPG 主持人：
1. 公平：只依据当前输入、可见交互和账本，不奖励偷用信息，也不随意惩罚合理试探。
2. 连续：尊重已有地点、出口、玩家位置、物品、NPC 与可见性；不要改写既有历史。
3. 具体：让交互产生清晰反馈，但不要把未落账内容当硬事实。
4. 克制：首版每次交互只做少量必要变更；可以揭示细节、创建少量 Item / NPC / Interaction、调整可见性，或在确有依据时移动玩家。
5. 视角安全：最终文本只写当前玩家可以感知到的结算，不泄露隐藏真相。

你必须通过工具更新世界状态：
- 如果交互揭示了新的可见物品或人物，必须调用 {{GmToolCatalog.CreateItemToolName}} 或 {{GmToolCatalog.CreateNpcToolName}}。
- 如果交互让某个对象变得可操作，必须调用 {{GmToolCatalog.AddInteractionToolName}}；precondition_note 若无特别条件写 none，且必须同时给出 turn_cost / effect_scope / effect_slots。
- {{ViewSafeNamingContractPromptLine}} {{ItemRefreshWithUpdateToolPromptLine}}
- 如果 actionKind 是 take / pick-up / give / drop / place，并且物品持有关系发生变化，必须调用 {{GmToolCatalog.MoveItemToActorToolName}} 或 {{GmToolCatalog.PlaceItemAtLocationToolName}} 落账；当前终端玩家 ActorId 是 player。
- 如果旧交互已经被消耗或暂时不应继续显示，调用 {{GmToolCatalog.SetInteractionVisibilityToolName}} 将它设为 hidden。
- 如果交互只产生感知反馈、没有 hard truth 变化，可以不调用工具，直接输出 1 到 3 句玩家可见结算摘要。
- 如果最终摘要提到玩家能看见的具体物品或人物，该实体必须已经落账。
- 不要用普通文本声称世界已改变；只有工具调用成功才算落账。
- 必要工具调用完成后，停止调用工具，并输出 1 到 3 句玩家可见的中文结算摘要。
""";
    }

    private static string BuildImmediateSelfInteractionSystemPrompt() {
        return $$"""
你是 TextAdv 的 TRPG GM Agent，负责结算一个 `TurnCost=0`、且只影响 acting actor 当前局部状态的即时小动作。

约束：
1. 只描述当前 actor 当下能确认的即时结果。
2. 不推进时间，不结束回合。
3. 只允许落账与 acting actor 及其眼前局部对象直接相关的必要 hard truth；不要扩写成整回合结算。
4. 不创建地点、不连接出口、不移动 actor、不创建 NPC，也不处理远处世界的连锁变化。
5. 若物品持有关系发生变化，必须调用 {{GmToolCatalog.MoveItemToActorToolName}} 或 {{GmToolCatalog.PlaceItemAtLocationToolName}}。
6. {{ItemRefreshWithUpdateToolPromptLine}}
7. 若当前 interaction 已经被消耗或不该继续显示，必须调用 {{GmToolCatalog.SetInteractionVisibilityToolName}}。
8. 若当前摘要提到玩家现在看得见、摸得着的新物品或新可执行动作，该实体或 affordance 必须已经通过工具落账。
9. {{ViewSafeNamingContractPromptLine}}
10. 输出 1 到 2 句简洁中文反馈即可，不要解释系统规则，不要泄露隐藏真相。

如果当前输入明显不符合“self + immediate”的约束，也仍然只在当前工具约束内给出克制反馈，不要擅自扩写成整回合结算。
""";
    }

    private static string BuildInteractionEffectSystemPrompt() {
        return """
你是 TextAdv 的 TRPG GM Agent，负责结算一个已经进入 effect-slot 阶段的 interaction 效果。

这不是新的 Large-Action，也不是新的即时小动作；你正在处理的是既有 interaction 在指定槽位上的结果：
- turn-end
- per-turn-end
- on-completion

你的职责有两部分：
1. 如有必要，用工具把这一 effect-slot 造成的 hard truth 变化落到账本。
2. 产出两个分离的结算文本：
   - actor_facing_summary
   - terminal_visible_summary

约束：
1. actor_facing_summary 只面向 acting actor 自己，可写它此刻能确认的结果。
2. terminal_visible_summary 只写 terminal player 在当前观察条件下能直接看到、听到、或自然确认的内容；若此刻不该让 terminal 知道，就写空字符串。
3. 不要泄露幕后机制，不要写“GM”“effect-slot”“validator”“系统”等元概念。
4. 若你提到新实体、可见性变化、物品转移、interaction 消耗或新增 affordance，必须先通过工具落账。
5. 输出必须是一个 JSON object，格式固定为：
   {"actor_facing_summary":"...","terminal_visible_summary":"..."}
6. terminal_visible_summary 可以是空字符串，但 actor_facing_summary 不应为空。
""";
    }

    private static string BuildExploreObservation(GmExploreContext context) {
        var perception = context.Perception;
        var sb = new StringBuilder();

        sb.AppendLine("[任务]");
        sb.AppendLine("结算玩家本回合的 explore Large-Action。");
        sb.AppendLine();
        sb.AppendLine("[当前玩家私有 Perception-Bundle]");
        sb.AppendLine(PerceptionEvidenceRenderer.RenderForPrompt(perception));

        sb.AppendLine();
        sb.AppendLine("[玩家动作]");
        sb.AppendLine($"- ActionKind: {TerminalActionKinds.LargeExplore}");
        sb.AppendLine($"- Direction: {context.Direction}");
        sb.AppendLine($"- Focus: {context.Focus ?? "(none)"}");
        sb.AppendLine($"- SuggestedReverseDirection: {context.SuggestedReverseDirection ?? "null"}");
        sb.AppendLine();
        sb.AppendLine("[玩家事前推理]");
        sb.AppendLine(context.PreActionReason);

        sb.AppendLine();
        sb.AppendLine("[工具使用要求]");
        sb.AppendLine("若 Direction 已存在于当前可见出口，目标 LocationId 必须使用该出口的 TargetLocationId。");
        sb.AppendLine("若 Direction 不存在于当前可见出口，新 LocationId 应使用小写 ASCII、数字和连字符，且能表达来源与方向。");
        sb.AppendLine("新地点 name 优先贴近 Focus；若 Focus 为空，可用“未知区域”等克制命名。");
        sb.AppendLine("新地点 description 只描述入口附近已经确认的低风险信息，不要塞入物品、NPC 或隐藏剧情。");
        sb.AppendLine("如果你创建 Item，item_id 必须稳定；物品描述应是玩家一眼能看到的东西，不要包含隐藏功能。");
        sb.AppendLine("如果你创建 NPC，actor_id 必须稳定；profile_note 只写玩家可感知的外观/姿态和 GM 可用的轻量主持提示，不写隐藏秘密。");
        sb.AppendLine("如果你创建 Interaction，target_ref 使用 location:<id>、item:<id> 或 actor:<id>；visible_label 是玩家看到的动作提示；precondition_note 无特别条件时写 none。");
        sb.AppendLine("每个新 interaction 还必须明确给出 turn_cost、effect_scope、effect_slots。只有 effect_scope=self 的 interaction 才能使用 immediate 槽位。");

        return sb.ToString();
    }

    private static string BuildExploreMapStageObservation(GmExploreContext context) {
        return BuildStagePrompt(
            "[阶段 1/3: 地图与移动落账]",
            sb => {
                sb.AppendLine("只处理 Location / Exit / Player location。");
                sb.AppendLine($"若目标方向已有出口，调用 {GmToolCatalog.MoveActorToolName}(player, target_location_id)。若没有出口，调用 {GmToolCatalog.CreateLocationToolName}、{GmToolCatalog.LinkLocationsToolName}、{GmToolCatalog.MoveActorToolName}(player, new_location_id)。");
                sb.AppendLine("本阶段不要创建 Item / NPC / Interaction，也不要输出最终摘要；工具完成后停止调用工具，文本可留空。");
            },
            sb => sb.Append(BuildExploreObservation(context))
        );
    }

    private static string BuildExploreLedgerAuditStageObservation(DurableDict<string> root, GmExploreContext context) {
        var perception = GameSimulation.DescribeCurrentPerception(root);
        return BuildStagePrompt(
            "[阶段 2/3: 实体与交互账本审计]",
            sb => {
                sb.AppendLine("检查刚完成的探索结果：如果最终叙事需要提到具体可见物品、NPC 或可执行动作，必须现在用工具落账。");
                sb.AppendLine($"可调用 {GmToolCatalog.FormatVisibleToolNames(GmToolCatalog.ToolSets.ExploreAudit)}。");
                sb.AppendLine($"{GmToolCatalog.AddInteractionToolName} 的 precondition_note 没有特别条件时写 none，并明确填写 turn_cost / effect_scope / effect_slots。");
                sb.AppendLine("本阶段不要移动玩家，不要创建更多地点，不要输出最终摘要；工具完成后停止调用工具，文本可留空。");
            },
            sb => {
                AppendExploreIntent(sb, context);
                AppendPerceptionSnapshot(sb, perception, "当前探索后账本投影");
            }
        );
    }

    private static string BuildExploreSummaryStageObservation(DurableDict<string> root, GmExploreContext context) {
        var perception = GameSimulation.DescribeCurrentPerception(root);
        return BuildStagePrompt(
            "[阶段 3/3: 玩家可见结算摘要]",
            sb => {
                sb.AppendLine("请输出 1 到 3 句中文结算摘要。原则：只描述当前玩家能感知到的新增结果与直接确认，只引用已经落账的地点、物品、NPC 和可执行动作。");
                sb.AppendLine("不要写时间推进，不要复述下一屏结构化 Perception 已直接展示的地点名、出口清单、地上物品清单、在场角色清单、背包清单或可执行动作清单。");
                sb.AppendLine("可以保留必要的桥接叙事，例如“你拨开树枝后确认前方有路”；但不要把最终清单再整段讲一遍。");
                sb.AppendLine("若你发现摘要必需的实体或 affordance 仍未落账，可以最后补充必要工具调用；否则不要调用工具。");
            },
            sb => {
                AppendExploreIntent(sb, context);
                AppendPerceptionSnapshot(sb, perception, "最终账本投影");
            }
        );
    }

    private static string BuildInteractionObservation(GmInteractionContext context) {
        var perception = context.Perception;
        var interaction = context.Interaction;
        var sb = new StringBuilder();

        sb.AppendLine("[任务]");
        sb.AppendLine("结算玩家本回合选择的 interact Large-Action。");
        sb.AppendLine();
        sb.AppendLine("[当前玩家私有 Perception-Bundle]");
        sb.AppendLine(PerceptionEvidenceRenderer.RenderForPrompt(perception));
        sb.AppendLine();
        sb.AppendLine("[玩家选择的交互]");
        sb.AppendLine($"- InteractionId: {interaction.InteractionId}");
        sb.AppendLine($"- Target: {interaction.TargetKind}:{interaction.TargetId}");
        sb.AppendLine($"- ActionKind: {interaction.ActionKind}");
        sb.AppendLine($"- VisibleLabel: {interaction.VisibleLabel}");
        sb.AppendLine($"- PreconditionNote: {interaction.PreconditionNote ?? "(none)"}");
        sb.AppendLine($"- EffectNote: {interaction.EffectNote ?? "(none)"}");
        sb.AppendLine($"- TurnCost: {interaction.TurnCost}");
        sb.AppendLine($"- EffectScope: {interaction.EffectScope}");
        sb.AppendLine($"- EffectSlots: {string.Join(", ", interaction.EffectSlots)}");

        sb.AppendLine();
        sb.AppendLine("[玩家事前推理]");
        sb.AppendLine(context.PreActionReason);

        sb.AppendLine();
        sb.AppendLine("[工具使用要求]");
        sb.AppendLine("优先根据所选 Interaction 的 target、actionKind、visibleLabel、preconditionNote、effectNote、turnCost、effectScope 和 effectSlots 裁决，不要把玩家事前推理当成事实来源。");
        sb.AppendLine("若创建 Item/NPC/Interaction，ID 必须稳定、唯一，并且 target_ref 必须引用已经存在的对象；precondition_note 无特别条件时写 none。");
        sb.AppendLine("若创建 Interaction，还必须明确填写 turn_cost、effect_scope、effect_slots。只有 effect_scope=self 的 interaction 才能使用 immediate 槽位。");
        AppendViewSafeNamingContract(sb);
        AppendItemRefreshGuidance(sb);
        sb.AppendLine("若只是检查、倾听、交谈第一句等低风险反馈，可以不改账本，只输出玩家可见摘要。");
        sb.AppendLine($"若当前 interaction 已被消耗或下一回合不应继续显示，请用 {GmToolCatalog.SetInteractionVisibilityToolName} 把它设为 hidden。");
        sb.AppendLine($"若玩家拿起或放下物品，请用 {GmToolCatalog.MoveItemToActorToolName} 或 {GmToolCatalog.PlaceItemAtLocationToolName} 更新 ownerActorId/locationId。当前终端玩家 ActorId 是 player。");
        sb.AppendLine("若摘要提到新的可见物品、人物或可执行动作，必须先调用相应工具落账。");

        return sb.ToString();
    }

    private static string BuildInteractionEffectObservation(GmInteractionEffectContext context) {
        var interaction = context.Interaction;
        var sb = new StringBuilder();
        sb.AppendLine("[任务]");
        sb.AppendLine("结算一个 interaction 在指定 effect-slot 阶段上的结果，并分别产出 acting actor 与 terminal player 的可见文本。");
        sb.AppendLine();
        sb.AppendLine("[acting actor 的私有 Perception-Bundle]");
        sb.AppendLine(PerceptionEvidenceRenderer.RenderForPrompt(context.ActorPerception));
        sb.AppendLine();
        sb.AppendLine("[terminal player 当前 Perception-Bundle]");
        sb.AppendLine(PerceptionEvidenceRenderer.RenderForPrompt(context.TerminalPerception));
        sb.AppendLine();
        sb.AppendLine("[effect-slot 上下文]");
        sb.AppendLine($"- InteractionId: {interaction.InteractionId}");
        sb.AppendLine($"- Target: {interaction.TargetKind}:{interaction.TargetId}");
        sb.AppendLine($"- ActionKind: {interaction.ActionKind}");
        sb.AppendLine($"- VisibleLabel: {interaction.VisibleLabel}");
        sb.AppendLine($"- PreconditionNote: {interaction.PreconditionNote ?? "(none)"}");
        sb.AppendLine($"- EffectNote: {interaction.EffectNote ?? "(none)"}");
        sb.AppendLine($"- TurnCost: {interaction.TurnCost}");
        sb.AppendLine($"- EffectScope: {interaction.EffectScope}");
        sb.AppendLine($"- EffectSlots: {string.Join(", ", interaction.EffectSlots)}");
        sb.AppendLine($"- CurrentEffectSlot: {context.EffectSlot}");
        sb.AppendLine($"- TerminalCanObserveActor: {context.TerminalCanObserveActor}");
        sb.AppendLine();
        sb.AppendLine("[acting actor 的原始事前推理]");
        sb.AppendLine(context.PreActionReason);
        sb.AppendLine();
        sb.AppendLine("[输出要求]");
        sb.AppendLine("最终只输出 JSON object，不要输出解释文字、markdown 或代码块。");
        return sb.ToString();
    }

    private static string BuildInteractionEffectConsequenceStageObservation(GmInteractionEffectContext context) {
        return BuildStagePrompt(
            "[阶段 1/3: effect-slot 直接后果]",
            sb => {
                sb.AppendLine("只处理这一 effect-slot 直接引发的 hard truth 变化。若只需要叙事反馈而无账本变化，可以不调用工具。");
                sb.AppendLine("本阶段不要输出最终 JSON；工具完成后停止调用工具，文本可留空。");
            },
            sb => sb.Append(BuildInteractionEffectObservation(context))
        );
    }

    private static string BuildInteractionEffectAuditStageObservation(DurableDict<string> root, GmInteractionEffectContext context) {
        return BuildStagePrompt(
            "[阶段 2/3: effect-slot affordance / visibility 审计]",
            sb => {
                sb.AppendLine("检查这一效果是否需要消耗旧 interaction、改变可见性、或补充新的 affordance。");
                sb.AppendLine("若需要变更，使用工具落账；本阶段不要输出最终 JSON。");
            },
            sb => {
                sb.Append(BuildInteractionEffectObservation(context));
                sb.AppendLine();
                AppendPerceptionSnapshot(sb, GameSimulation.DescribePerceptionForActor(root, context.ActorPerception.ActorId), "acting actor 当前账本投影");
                AppendPerceptionSnapshot(sb, GameSimulation.DescribeCurrentPerception(root), "terminal player 当前账本投影");
            }
        );
    }

    private static string BuildInteractionEffectSummaryStageObservation(DurableDict<string> root, GmInteractionEffectContext context) {
        return BuildStagePrompt(
            "[阶段 3/3: 输出 actor-facing / terminal-visible JSON]",
            sb => {
                sb.AppendLine("请只输出一个 JSON object：{\"actor_facing_summary\":\"...\",\"terminal_visible_summary\":\"...\"}");
                sb.AppendLine("若 terminal 在当前条件下不该得知结果，terminal_visible_summary 写空字符串。");
                sb.AppendLine("两个 summary 都只写这一 effect-slot 新增发生的结果，不要写时间推进，不要复述结构化 Perception 已直接列出的地点/物品/角色/交互清单。");
                sb.AppendLine("若需要提及新发现，请用简短桥接句说明“因此确认了什么”，而不是把最终画面完整重讲。");
                sb.AppendLine("如发现摘要依赖的账本变更尚未落地，可以最后补工具调用；否则不要调用工具。");
            },
            sb => {
                sb.Append(BuildInteractionEffectObservation(context));
                sb.AppendLine();
                AppendPerceptionSnapshot(sb, GameSimulation.DescribePerceptionForActor(root, context.ActorPerception.ActorId), "acting actor 最终账本投影");
                AppendPerceptionSnapshot(sb, GameSimulation.DescribeCurrentPerception(root), "terminal player 最终账本投影");
            }
        );
    }

    private static string BuildInteractionConsequenceStageObservation(GmInteractionContext context) {
        return BuildStagePrompt(
            "[阶段 1/3: 交互直接后果]",
            sb => {
                sb.AppendLine("只处理玩家选择的 interaction 的直接 hard truth 后果。可创建/显示物品或 NPC，可调整可见性，可在有充分依据时移动玩家。");
                sb.AppendLine("不要补一长串后续按钮；不要输出最终摘要。工具完成后停止调用工具，文本可留空。");
            },
            sb => sb.Append(BuildInteractionObservation(context))
        );
    }

    private static string BuildInteractionAffordanceAuditStageObservation(DurableDict<string> root, GmInteractionContext context) {
        var perception = GameSimulation.DescribeCurrentPerception(root);
        return BuildStagePrompt(
            "[阶段 2/3: affordance 生命周期审计]",
            sb => {
                sb.AppendLine("检查交互后玩家下一回合应该看到哪些可执行动作。");
                sb.AppendLine($"若当前 interaction 已经被消耗或下一回合不该重复显示，用 {GmToolCatalog.SetInteractionVisibilityToolName} 设为 hidden。");
                sb.AppendLine($"若出现新的合理后续动作，用 {GmToolCatalog.AddInteractionToolName} 落账；precondition_note 无特别条件时写 none，并明确填写 turn_cost / effect_scope / effect_slots。");
                sb.AppendLine("本阶段不要输出最终摘要；工具完成后停止调用工具，文本可留空。");
            },
            sb => {
                AppendInteractionIntent(sb, context);
                AppendPerceptionSnapshot(sb, perception, "交互后账本投影");
            }
        );
    }

    private static string BuildInteractionSummaryStageObservation(DurableDict<string> root, GmInteractionContext context) {
        var perception = GameSimulation.DescribeCurrentPerception(root);
        return BuildStagePrompt(
            "[阶段 3/3: 玩家可见结算摘要]",
            sb => {
                sb.AppendLine("请输出 1 到 3 句中文结算摘要。原则：只描述当前玩家能感知到的新增结果与直接确认，只引用已经落账的物品、NPC 和可执行动作。");
                sb.AppendLine("不要写时间推进，不要复述下一屏结构化 Perception 已直接展示的地点、地上物品、在场角色、背包或可执行动作清单。");
                sb.AppendLine("可以保留必要的因果桥接，例如“你掀开叶堆后摸到一枚金属片”；但不要把新清单重新完整朗读一遍。");
                sb.AppendLine("若你发现摘要必需的新实体或 affordance 仍未落账，可以最后补充必要工具调用；否则不要调用工具。");
            },
            sb => {
                AppendInteractionIntent(sb, context);
                AppendPerceptionSnapshot(sb, perception, "最终账本投影");
            }
        );
    }

    private static string BuildImmediateSelfInteractionObservation(GmInteractionContext context) {
        var interaction = context.Interaction;
        var sb = new StringBuilder();
        sb.AppendLine("[任务]");
        sb.AppendLine("给当前 actor 的即时 self/private 小动作写一段当前可见反馈。");
        sb.AppendLine();
        sb.AppendLine("[当前玩家私有 Perception-Bundle]");
        sb.AppendLine(PerceptionEvidenceRenderer.RenderForPrompt(context.Perception));
        sb.AppendLine();
        sb.AppendLine("[玩家选择的交互]");
        sb.AppendLine($"- InteractionId: {interaction.InteractionId}");
        sb.AppendLine($"- Target: {interaction.TargetKind}:{interaction.TargetId}");
        sb.AppendLine($"- ActionKind: {interaction.ActionKind}");
        sb.AppendLine($"- VisibleLabel: {interaction.VisibleLabel}");
        sb.AppendLine($"- PreconditionNote: {interaction.PreconditionNote ?? "(none)"}");
        sb.AppendLine($"- EffectNote: {interaction.EffectNote ?? "(none)"}");
        sb.AppendLine($"- TurnCost: {interaction.TurnCost}");
        sb.AppendLine($"- EffectScope: {interaction.EffectScope}");
        sb.AppendLine($"- EffectSlots: {string.Join(", ", interaction.EffectSlots)}");
        sb.AppendLine();
        sb.AppendLine("[玩家事前推理]");
        sb.AppendLine(context.PreActionReason);
        sb.AppendLine();
        sb.AppendLine("[输出要求]");
        sb.AppendLine("只写当前 actor 能确认的即时结果，不要写系统术语，不要写“回合”“validator”“GM”等幕后概念。");
        sb.AppendLine("若 effectNote 已经足够清楚，可贴近它，但不要机械复述。");
        return sb.ToString();
    }

    private static string BuildImmediateSelfInteractionConsequenceStageObservation(GmInteractionContext context) {
        return BuildStagePrompt(
            "[阶段 1/3: immediate-local consequence]",
            sb => {
                sb.AppendLine("只处理当前 acting actor 与眼前目标直接相关的必要 hard truth。");
                sb.AppendLine("如果拿起、放下、收入口袋或从身上取出某个物品，必须用工具更新 item 持有关系。");
                sb.AppendLine("本阶段不要输出最终摘要；必要工具调用完成后停止调用工具，文本可留空。");
            },
            sb => sb.Append(BuildImmediateSelfInteractionObservation(context))
        );
    }

    private static string BuildImmediateSelfInteractionAffordanceAuditStageObservation(
        DurableDict<string> root,
        GmInteractionContext context
    ) {
        var perception = GameSimulation.DescribeCurrentPerception(root);
        return BuildStagePrompt(
            "[阶段 2/3: immediate-local affordance audit]",
            sb => {
                sb.AppendLine("检查这个即时小动作完成后，下一次 look-around 时玩家现在应看到哪些局部 affordance。");
                sb.AppendLine($"若当前 interaction 已被消耗或不该重复显示，用 {GmToolCatalog.SetInteractionVisibilityToolName} 设为 hidden。");
                AppendItemRefreshGuidance(sb);
                sb.AppendLine($"若物品到了玩家手中且需要新的后续动作，用 {GmToolCatalog.AddInteractionToolName} 落账；无特别前提写 none。");
                sb.AppendLine("本阶段不要输出最终摘要；必要工具调用完成后停止调用工具，文本可留空。");
            },
            sb => {
                AppendInteractionIntent(sb, context);
                AppendPerceptionSnapshot(sb, perception, "即时动作后账本投影");
            }
        );
    }

    private static void AppendViewSafeNamingContract(StringBuilder sb) {
        sb.AppendLine("interaction_id、visible_label、item 名称与 item 描述都会暴露给玩家；未确认身份前必须使用不剧透的通用叫法。");
    }

    private static void AppendItemRefreshGuidance(StringBuilder sb) {
        sb.AppendLine($"若这一步确认了既有 item 的真实身份，或其外观状态已变化，用 {GmToolCatalog.UpdateItemToolName} 刷新现有 item 的名称或描述。");
    }

    private static string BuildImmediateSelfInteractionSummaryStageObservation(
        DurableDict<string> root,
        GmInteractionContext context
    ) {
        var perception = GameSimulation.DescribeCurrentPerception(root);
        return BuildStagePrompt(
            "[阶段 3/3: immediate-local summary]",
            sb => {
                sb.AppendLine("请输出 1 到 2 句中文即时反馈。只写 acting actor 此刻能确认的结果，只引用已经落账的物品与动作。");
                sb.AppendLine("不要写时间推进，不要写整回合总结，不要复述下一屏结构化清单里已经显而易见的大段信息。");
                sb.AppendLine("若你发现摘要依赖的新物品、可执行动作或可见性变更仍未落账，可以最后补充必要工具调用；否则不要调用工具。");
            },
            sb => {
                AppendInteractionIntent(sb, context);
                AppendPerceptionSnapshot(sb, perception, "最终账本投影");
            }
        );
    }

    private static string BuildCollectedTurnConsequenceStageObservation(GmCollectedTurnContext context) {
        return BuildStagePrompt(
            "[阶段 1/3: 多主体意图裁决与 hard truth 落账]",
            sb => {
                sb.AppendLine("统一裁决所有 active player actor 的 Large-Action。只处理必要 hard truth：Location/Exit/Actor location/Item ownership/NPC/Interaction/visibility。");
                sb.AppendLine("本阶段不要输出最终摘要。工具完成后停止调用工具，文本可留空。");
            },
            sb => AppendCollectedTurnIntents(sb, context)
        );
    }

    private static string BuildCollectedTurnLedgerAuditStageObservation(DurableDict<string> root, GmCollectedTurnContext context) {
        return BuildStagePrompt(
            "[阶段 2/3: 多主体账本审计]",
            sb => {
                sb.AppendLine("检查刚才的多主体结算是否遗漏必要账本：终端玩家/LLM Player 的位置、物品位置/持有者、新地点出口、可见 NPC、可执行 interaction。");
                sb.AppendLine("如果最终摘要或下一回合 Perception-Bundle 需要某实体或 affordance，请现在补工具调用。不要输出最终摘要。工具完成后停止调用工具，文本可留空。");
            },
            sb => {
                AppendCollectedTurnIntents(sb, context);
                AppendPerceptionSnapshot(sb, GameSimulation.DescribePerceptionForActor(root, context.TerminalActorId), "当前终端玩家账本投影");
                foreach (var intent in context.Intents.Where(intent => !string.Equals(intent.ActorId, context.TerminalActorId, StringComparison.Ordinal))) {
                    AppendPerceptionSnapshot(sb, GameSimulation.DescribePerceptionForActor(root, intent.ActorId), $"当前 {intent.ActorName} 账本投影");
                }
            }
        );
    }

    private static string BuildCollectedTurnSummaryStageObservation(DurableDict<string> root, GmCollectedTurnContext context) {
        return BuildStagePrompt(
            "[阶段 3/3: 各 actor 私有结算反馈与终端玩家摘要]",
            sb => {
                sb.AppendLine($"先为每个 active player actor 调用 {GmToolCatalog.SetActorResolutionToolName}(actor_id, summary)，写入该 actor 下一回合可见的私有反馈。");
                sb.AppendLine("每份私有反馈 1 到 4 句中文，只写该 actor 这一回合新增感知到、参与声明时公开知道、或自然能推断的内容；不要泄露其它 actor 离开视野后的隐藏发现。");
                sb.AppendLine("所有 actor resolution 都写入后，停止调用工具，并输出 1 到 4 句终端玩家可见中文摘要。");
                sb.AppendLine("私有反馈与终端摘要都不要写时间推进，不要把结构化 Perception 已直接展示的地点、出口、物品、角色、背包或交互清单再完整复述一遍。");
                sb.AppendLine("若需要提及新发现，请优先写“这一步发生了什么，因此确认了什么”的桥接信息，而不是重新描述整张场景卡。");
                sb.AppendLine("若你发现摘要必需的新实体或 affordance 仍未落账，可以最后补充必要工具调用；否则不要调用工具。");
                sb.AppendLine();
                sb.AppendLine("[必须写入私有反馈的 ActorId]");
                foreach (var intent in context.Intents) {
                    sb.AppendLine($"- {intent.ActorId}: {intent.ActorName} ({intent.ActorKind})");
                }
            },
            sb => {
                AppendCollectedTurnIntents(sb, context);
                AppendPerceptionSnapshot(sb, GameSimulation.DescribePerceptionForActor(root, context.TerminalActorId), "最终终端玩家账本投影");
                foreach (var intent in context.Intents.Where(intent => !string.Equals(intent.ActorId, context.TerminalActorId, StringComparison.Ordinal))) {
                    AppendPerceptionSnapshot(sb, GameSimulation.DescribePerceptionForActor(root, intent.ActorId), $"最终 {intent.ActorName} 账本投影");
                }
            }
        );
    }

    private static string BuildStagePrompt(
        string stageTitle,
        Action<StringBuilder> appendGuidance,
        Action<StringBuilder> appendContext
    ) {
        var sb = new StringBuilder();
        sb.AppendLine(stageTitle);
        appendGuidance(sb);
        sb.AppendLine();
        appendContext(sb);
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
        sb.AppendLine($"- TurnCost: {interaction.TurnCost}");
        sb.AppendLine($"- EffectScope: {interaction.EffectScope}");
        sb.AppendLine($"- EffectSlots: {string.Join(", ", interaction.EffectSlots)}");
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
        sb.AppendLine(PerceptionEvidenceRenderer.RenderForPrompt(perception));
        sb.AppendLine();
    }

    internal static string? ValidateExploreMapStageCompletion(DurableDict<string> root, GmExploreContext context) {
        var actorPerception = GameSimulation.DescribePerceptionForActor(root, context.Perception.ActorId);
        var currentLocationId = actorPerception.Location.LocationId;
        var existingExit = context.Perception.Location.Exits.FirstOrDefault(
            exit => string.Equals(exit.Direction, context.Direction, StringComparison.Ordinal)
        );

        if (existingExit is not null) {
            if (!string.Equals(currentLocationId, existingExit.TargetLocationId, StringComparison.Ordinal)) {
                return
                    $"GM Agent 在阶段 explore-map 结束后仍未把 actor '{context.Perception.ActorId}' 移动到已知出口 '{context.Direction}' 的目标地点 '{existingExit.TargetLocationId}'。当前地点：{currentLocationId}";
            }

            return null;
        }

        if (string.Equals(currentLocationId, context.CurrentLocationId, StringComparison.Ordinal)) {
            return
                $"GM Agent 在阶段 explore-map 结束后仍未把 actor '{context.Perception.ActorId}' 从原地点 '{context.CurrentLocationId}' 移开。";
        }

        var linkedTargetLocationId = TryGetExitTargetLocationId(root, context.CurrentLocationId, context.Direction);
        if (!string.Equals(linkedTargetLocationId, currentLocationId, StringComparison.Ordinal)) {
            return
                $"GM Agent 在阶段 explore-map 结束后未把原地点 '{context.CurrentLocationId}' 的出口 '{context.Direction}' 正确连接到当前地点 '{currentLocationId}'。当前账本中的目标地点：{linkedTargetLocationId ?? "(missing)"}";
        }

        if (!string.IsNullOrWhiteSpace(context.SuggestedReverseDirection)) {
            var reverseTargetLocationId = TryGetExitTargetLocationId(root, currentLocationId, context.SuggestedReverseDirection);
            if (!string.Equals(reverseTargetLocationId, context.CurrentLocationId, StringComparison.Ordinal)) {
                return
                    $"GM Agent 在阶段 explore-map 结束后未为新地点 '{currentLocationId}' 写入建议反向出口 '{context.SuggestedReverseDirection}' -> '{context.CurrentLocationId}'。当前账本中的目标地点：{reverseTargetLocationId ?? "(missing)"}";
            }
        }

        return null;
    }

    private static string? ValidateCollectedTurnSummaryStageCompletion(
        DurableDict<string> root,
        GmCollectedTurnContext context
    ) {
        foreach (var intent in context.Intents) {
            var actorPerception = GameSimulation.DescribePerceptionForActor(root, intent.ActorId);
            if (string.IsNullOrWhiteSpace(actorPerception.LastResolution)) { return $"GM Agent 在阶段 collected-turn-summary 结束后仍未为 actor '{intent.ActorId}' 写入私有结算反馈。"; }
        }

        return null;
    }

    private static string Indent(string text, string indentation) {
        if (string.IsNullOrEmpty(text)) { return string.Empty; }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return string.Join("\n", lines.Select(line => indentation + line));
    }

    private static string? TryGetExitTargetLocationId(
        DurableDict<string> root,
        string locationId,
        string direction
    ) {
        if (string.IsNullOrWhiteSpace(locationId) || string.IsNullOrWhiteSpace(direction)) { return null; }

        var world = root.GetOrThrow<DurableDict<string>>("world");
        if (world is null || !world.TryGet("locations", out DurableDict<string>? locations) || locations is null) { return null; }
        if (!locations.TryGet(locationId, out DurableDict<string>? location) || location is null) { return null; }
        if (!location.TryGet("exits", out DurableDict<string>? exits) || exits is null) { return null; }

        return exits.TryGet(direction, out string? targetLocationId) && !string.IsNullOrWhiteSpace(targetLocationId)
            ? targetLocationId
            : null;
    }

    private static string BuildStageObservation(
        GmResolutionStage stage,
        ImmutableArray<ToolDefinition> visibleToolDefinitions
    ) {
        var sb = new StringBuilder();
        sb.Append(stage.BuildObservation().TrimEnd());
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("[本阶段当前可用工具]");
        if (visibleToolDefinitions.Length == 0) {
            sb.AppendLine("- (none)");
            return sb.ToString();
        }

        foreach (var tool in visibleToolDefinitions) {
            sb.AppendLine($"- {tool.Name}: {tool.Description}");
        }

        return sb.ToString();
    }

    private static ToolExecutor GetOrCreateToolExecutor(
        DurableDict<string> root,
        GmToolCatalog.ToolSet toolSet,
        IDictionary<GmToolCatalog.ToolSet, ToolExecutor> cache
    ) {
        if (cache.TryGetValue(toolSet, out var executor)) { return executor; }

        executor = GmToolCatalog.CreateExecutor(root, toolSet);
        cache.Add(toolSet, executor);
        return executor;
    }

    private static async Task<GmStageRunResult> RunStageAsync(
        DeepSeekV4ChatClient client,
        GmConfig config,
        string systemPrompt,
        List<IHistoryMessage> history,
        GmResolutionStage stage,
        ToolExecutor toolExecutor,
        ImmutableArray<ToolDefinition> visibleToolDefinitions,
        CancellationToken cancellationToken
    ) {
        ActionMessage? lastAction = null;

        for (var round = 1; round <= config.MaxRounds; round++) {
            var request = new CompletionRequest(
                ModelId: config.ModelId,
                SystemPrompt: systemPrompt,
                Context: history,
                Tools: visibleToolDefinitions
            );
            var result = await client.StreamCompletionAsync(request, null, cancellationToken).ConfigureAwait(false);
            if (result.Errors is { Count: > 0 }) { throw new InvalidOperationException(BuildProviderErrorMessage(result.Errors)); }

            lastAction = result.Message;
            history.Add(lastAction);

            if (lastAction.ToolCalls.Count == 0) {
                return new GmStageRunResult(
                    Completed: true,
                    RawText: lastAction.GetFlattenedText(),
                    LastText: NormalizeSummary(lastAction.GetFlattenedText())
                );
            }

            var executionResults = new List<ToolCallExecutionResult>(lastAction.ToolCalls.Count);
            foreach (var toolCall in lastAction.ToolCalls) {
                var execution = await toolExecutor.ExecuteAsync(toolCall, cancellationToken).ConfigureAwait(false);
                executionResults.Add(execution);
            }

            var toolResults = executionResults
                .Select(
                static result => new ToolResult(
                    result.ToolName,
                    result.ToolCallId,
                    result.ExecuteResult.Status,
                    result.ExecuteResult.Content
                )
            )
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

        return new GmStageRunResult(
            Completed: false,
            RawText: lastAction?.GetFlattenedText() ?? string.Empty,
            LastText: NormalizeSummary(lastAction?.GetFlattenedText() ?? string.Empty)
        );
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

            s_httpClient = CompletionHttpTransportFactory.CreateLiveClient(ResolveBaseAddress(config.BaseAddress));
            s_client = new DeepSeekV4ChatClient(
                apiKey: config.ApiKey,
                httpClient: s_httpClient,
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

    private static Uri ResolveBaseAddress(string? configuredBaseAddress) {
        return string.IsNullOrWhiteSpace(configuredBaseAddress)
            ? new Uri(DefaultBaseAddress)
            : new Uri(configuredBaseAddress, UriKind.Absolute);
    }

    private static GmConfig GetConfig() {
        lock (s_gate) {
            if (s_config is not null) { return s_config; }

            var removedMode = TextAdvRuntimeEnvironment.GetOptionalEnvironment("ATELIA_TEXTADV_GM_MODE");
            if (!string.IsNullOrWhiteSpace(removedMode)) {
                throw new InvalidOperationException(
                    "ATELIA_TEXTADV_GM_MODE 已移除。运行时现在只支持真实 LLM GM；测试请显式注入 GameMasterStub。"
                );
            }

            s_config = new GmConfig(
                BaseAddress: TextAdvRuntimeEnvironment.GetOptionalEnvironment(BaseAddressEnv),
                ModelId: TextAdvRuntimeEnvironment.GetEnvironmentOrDefault(
                    ModelIdEnv,
                    TextAdvRuntimeEnvironment.GetEnvironmentOrDefault(FallbackModelIdEnv, DefaultModelId)
                ),
                ApiKey: TextAdvRuntimeEnvironment.GetOptionalEnvironment(ApiKeyEnv),
                MaxRounds: TextAdvRuntimeEnvironment.GetPositiveIntEnvironment(MaxRoundsEnv, DefaultMaxRounds)
            );
            if (string.IsNullOrWhiteSpace(s_config.ApiKey)) {
                throw new InvalidOperationException(
                    $"{ApiKeyEnv} 未配置。TextAdv 运行时现在要求真实 GM Agent 可用；测试请显式注入 GameMasterStub。"
                );
            }

            return s_config;
        }
    }

    internal static void SetStubForTests(GameMasterStub? stub) {
        lock (s_gate) {
            s_stub = stub;
            s_httpClient?.Dispose();
            s_httpClient = null;
            s_client = null;
            s_config = null;
        }
    }

    internal static void ResetForTests() {
        lock (s_gate) {
            s_stub = null;
            s_httpClient?.Dispose();
            s_httpClient = null;
            s_client = null;
            s_config = null;
        }
    }

    private static GameMasterStub? GetStub() {
        lock (s_gate) {
            return s_stub;
        }
    }

    private static (string ActorFacingSummary, string? TerminalVisibleSummary) ParseInteractionEffectSummary(
        string text,
        bool terminalCanObserveActor
    ) {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal)) {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0) {
                trimmed = trimmed[(firstNewline + 1)..];
            }

            var fenceIndex = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceIndex >= 0) {
                trimmed = trimmed[..fenceIndex].Trim();
            }
        }

        try {
            if (JsonNode.Parse(trimmed) is JsonObject json) {
                var actorFacingSummary = json["actor_facing_summary"]?.GetValue<string>() ?? string.Empty;
                var terminalVisibleSummary = json["terminal_visible_summary"]?.GetValue<string>() ?? string.Empty;
                actorFacingSummary = NormalizeSummary(actorFacingSummary);
                terminalVisibleSummary = NormalizeSummary(terminalVisibleSummary);
                if (string.IsNullOrWhiteSpace(actorFacingSummary)) {
                    actorFacingSummary = "这一步效果已经产生，只是细节暂时不足。";
                }

                if (!terminalCanObserveActor) {
                    terminalVisibleSummary = string.Empty;
                }

                return (
                    actorFacingSummary,
                    string.IsNullOrWhiteSpace(terminalVisibleSummary) ? null : terminalVisibleSummary
                );
            }
        }
        catch (Exception) {
            // Fall through to plain-text fallback.
        }

        var fallback = NormalizeSummary(text);
        if (string.IsNullOrWhiteSpace(fallback)) {
            fallback = "这一步效果已经产生，只是细节暂时不足。";
        }

        return (
            fallback,
            terminalCanObserveActor ? fallback : null
        );
    }

    private static string NormalizeSummary(string text) {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (normalized.Length <= 800) { return normalized; }

        return string.Concat(normalized.AsSpan(0, 800), "...");
    }

    private static string BuildProviderErrorMessage(IReadOnlyList<string> errors) {
        return TextAdvRuntimeEnvironment.BuildProviderErrorMessage(
            errors,
            prefix: string.Empty,
            defaultMessage: "Unknown provider error."
        );
    }
}
