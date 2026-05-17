using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using Atelia.Completion.Tools;
using Atelia.StateJournal;

namespace Atelia.TextAdv;

internal static class LlmPlayerAgentDriver {
    private const string BaseAddressEnv = "DEEPSEEK_BASE_URL";
    private const string ModelIdEnv = "ATELIA_TEXTADV_LLM_PLAYER_MODEL_ID";
    private const string FallbackModelIdEnv = "DEEPSEEK_MODEL";
    private const string ApiKeyEnv = "DEEPSEEK_API_KEY";
    private const string ModeEnv = "ATELIA_TEXTADV_LLM_PLAYER_MODE";
    private const string PipelineEnv = "ATELIA_TEXTADV_LLM_PLAYER_PIPELINE";
    private const string MaxAttemptsEnv = "ATELIA_TEXTADV_LLM_PLAYER_MAX_ATTEMPTS";
    private const string DefaultModelId = "deepseek-v4-flash";
    private const string DirectorExecutorPipeline = "director-executor";
    private const string SinglePipeline = "single";
    private const int DefaultMaxAttempts = 3;

    private static readonly Lock s_gate = new();
    private static DeepSeekV4ChatClient? s_client;
    private static LlmPlayerConfig? s_config;

    private sealed record LlmPlayerConfig(
        string? BaseAddress,
        string ModelId,
        string? ApiKey,
        string Mode,
        string Pipeline,
        int MaxAttempts
    );

    private sealed record PlayerActionProposal(
        string ActionKind,
        string ActionSummary,
        string? ActionPayload,
        string PreActionReason
    );

    internal static async Task<AsyncAteliaResult<TurnCollectionStatus>> TrySubmitLargeActionAsync(
        DurableDict<string> root,
        string actorId,
        CancellationToken cancellationToken
    ) {
        var config = GetConfig();
        if (string.Equals(config.Mode, "deterministic", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(config.ApiKey)) {
            return SubmitFallback(root, actorId, "LLM Player Agent 未启用或未配置 API key。");
        }

        try {
            var perception = GameSimulation.DescribePerceptionForActor(root, actorId);
            var initialObservation = BuildInitialObservation(perception);
            var history = new List<IHistoryMessage>
            {
                new ObservationMessage(initialObservation)
            };

            if (UsesDirectorExecutorPipeline(config)) {
                var directorNotes = await BuildDirectorNotesAsync(config, initialObservation, cancellationToken)
                    .ConfigureAwait(false);
                if (directorNotes.IsFailure) {
                    return SubmitFallback(root, actorId, directorNotes.Error!.Message);
                }

                history.Add(new ObservationMessage(BuildDirectorNotesObservation(directorNotes.Value!)));
            }

            var toolService = new PlayerActionToolService(root, actorId, perception);

            for (var attempt = 1; attempt <= config.MaxAttempts; attempt++) {
                var toolExecutor = CreateToolExecutor(toolService);
                var request = new CompletionRequest(
                    ModelId: config.ModelId,
                    SystemPrompt: BuildSystemPrompt(),
                    Context: history,
                    Tools: toolExecutor.GetVisibleToolDefinitions()
                );
                var result = await GetClient(config).StreamCompletionAsync(request, null, cancellationToken).ConfigureAwait(false);
                if (result.Errors is { Count: > 0 }) {
                    return SubmitFallback(root, actorId, BuildProviderErrorMessage(result.Errors));
                }

                var action = result.Message;
                history.Add(action);
                if (action.ToolCalls.Count == 0) {
                    history.Add(new ObservationMessage("你必须调用工具行动：可以先调用 Small-Action 工具编辑 notebook，最终必须调用一个 Large-Action 工具提交回合。不要只返回自然语言。"));
                    continue;
                }

                var executionResults = new List<ToolCallExecutionResult>(action.ToolCalls.Count);
                foreach (var toolCall in action.ToolCalls) {
                    executionResults.Add(await toolExecutor.ExecuteAsync(toolCall, cancellationToken).ConfigureAwait(false));
                }

                var toolResults = executionResults
                    .Select(static item => new ToolResult(
                        item.ToolName,
                        item.ToolCallId,
                        item.ExecuteResult.Status,
                        item.ExecuteResult.Content
                    ))
                    .ToArray();
                var failure = executionResults.FirstOrDefault(static item => item.ExecuteResult.Status == ToolExecutionStatus.Failed);
                history.Add(
                    new ToolResultsMessage(
                        BuildToolResultsObservation(executionResults),
                        toolResults,
                        failure?.ExecuteResult.Content
                    )
                );

                if (failure is not null) {
                    toolService.ClearProposal();
                    history.Add(new ObservationMessage("工具调用失败。请修正参数；可以先编辑 notebook，最终必须调用一个 Large-Action 工具。"));
                    continue;
                }

                if (toolService.Proposal is null) {
                    history.Add(new ObservationMessage("Small-Action 已处理。现在请根据更新后的 Memory-Notebook 和感知，调用一个 Large-Action 工具提交本回合。"));
                    continue;
                }

                var proposal = toolService.Proposal;
                GameActionValidator.ValidationResult validation;
                try {
                    validation = await GameActionValidator.ValidateActionAsync(
                        toolService.CurrentPerception,
                        proposal.ActionKind,
                        proposal.ActionSummary,
                        proposal.PreActionReason,
                        proposal.ActionPayload,
                        cancellationToken
                    ).ConfigureAwait(false);
                }
                catch (Exception ex) {
                    return SubmitFallback(root, actorId, $"LLM Player validator 调用失败：{ex.Message}");
                }

                if (!validation.Accepted) {
                    toolService.ClearProposal();
                    history.Add(new ObservationMessage(
                        $"validator 拒绝了你的行动，请根据反馈重试。\n[validator feedback]\n{validation.Feedback}"
                    ));
                    continue;
                }

                var submitResult = GameSimulation.SubmitLargeActionForActor(
                    root,
                    actorId,
                    proposal.ActionKind,
                    proposal.ActionSummary,
                    proposal.ActionPayload,
                    proposal.PreActionReason,
                    validation.Feedback
                );
                return submitResult.IsSuccess
                    ? AsyncAteliaResult<TurnCollectionStatus>.Success(submitResult.Value!)
                    : AsyncAteliaResult<TurnCollectionStatus>.Failure(submitResult.Error!);
            }

            return SubmitFallback(root, actorId, $"LLM Player Agent 在 {config.MaxAttempts} 次尝试内没有提交 validator 通过的 Large-Action。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return SubmitFallback(root, actorId, $"LLM Player Agent 失败：{ex.Message}");
        }
    }

    private static AsyncAteliaResult<TurnCollectionStatus> SubmitFallback(
        DurableDict<string> root,
        string actorId,
        string reason
    ) {
        var perception = GameSimulation.DescribePerceptionForActor(root, actorId);
        var submitResult = GameSimulation.SubmitLargeActionForActor(
            root,
            actorId,
            actionKind: "large/rest-a-while",
            actionSummary: "谨慎观察并暂不移动",
            actionPayload: null,
            preActionReason: $"MVP fallback：{reason} 当前先在「{perception.Location.Name}」保持观察，不主动改变世界状态。",
            validatorFeedback: "llm-player fallback bypassed validator"
        );
        return submitResult.IsSuccess
            ? AsyncAteliaResult<TurnCollectionStatus>.Success(submitResult.Value!)
            : AsyncAteliaResult<TurnCollectionStatus>.Failure(submitResult.Error!);
    }

    private static ToolExecutor CreateToolExecutor(PlayerActionToolService toolService)
        => new([
            MethodToolWrapper.FromDelegate<string, string>(toolService.EditMemoryNotebookAsync),
            MethodToolWrapper.FromDelegate<string>(toolService.RestAWhileAsync),
            MethodToolWrapper.FromDelegate<string, string, string?>(toolService.ExploreAsync),
            MethodToolWrapper.FromDelegate<string, string>(toolService.InteractAsync),
        ]);

    private static string BuildSystemPrompt() {
        return """
你是 TextAdv 的 LLM Player Agent，负责扮演一个 active player actor。

你的任务是根据自己的 Perception-Bundle、Memory-Notebook，以及可能出现的导演札记，为当前回合提交一个 Large-Action。

硬规则：
1. 你只能依据输入给你的 actor 私有视角行动，不能假装知道完整世界真相。
2. 你可以先调用零到多个 player_edit_memory_notebook Small-Action 工具更新自己的 Memory-Notebook。
3. 最终必须调用 exactly one Large-Action 工具提交回合，不要只返回自然语言。
4. 你可以选择休息、探索、或执行当前可见 interaction。
5. 每个工具的事前推理必须先说明当前证据如何支持这个动作，不要写成事后解释。
6. 如果没有明确目标，优先选择 player_rest_a_while，语义是谨慎观察并暂不移动。
7. 不要试图直接改世界账本；你只是更新自己的记忆或声明玩家意图，GM 会统一结算。
8. 导演札记是行动参考，不是世界真相；若札记与 Perception-Bundle 冲突，以 Perception-Bundle 和工具结果为准。

Notebook 规则：
- 记录猜测时请写“可能 / 怀疑 / 尚未确认”，不要把未证实内容写成确定事实。
- edit_script 可直接传 <insert side="after" anchor="tail">...</insert> 这类片段，系统会补根节点。
- 每次工具调用后你会收到结果；如果 notebook 已经足够，继续提交 Large-Action。
""";
    }

    private static string BuildDirectorSystemPrompt() {
        return """
你是 TextAdv 的 LLM Player 导演/心理建模器。你不调用工具，不直接扮演角色说台词。

你的任务是把当前 actor 的私有感知整理成一份短小、可执行的导演札记，帮助后续执行阶段更像“有脑子、有欲望、有顾虑的角色”，同时仍保持工具调用可靠。

请只依据输入的 Perception-Bundle 和 Memory-Notebook，不要引入完整世界真相。输出必须是简洁可见文本，包含：

1. perceived_facts: 角色此刻确实能确认的事实。
2. beliefs_and_uncertainties: 角色合理怀疑但尚未确认的事。
3. motive_pressure: 此刻驱动角色行动的欲望、恐惧、利益、责任或惯性。
4. risk_posture: 倾向谨慎、冒险、社交、控制、逃避等哪种姿态，以及原因。
5. notebook_update: 是否建议先更新 Memory-Notebook，若建议，写出应记录的内容。
6. intended_large_action: 推荐的 Large-Action 类型和目标；若证据不足，推荐 rest-a-while。

不要写世界结算，不要假装行动已经成功，不要编造工具返回结果。
""";
    }

    private static async Task<AsyncAteliaResult<string>> BuildDirectorNotesAsync(
        LlmPlayerConfig config,
        string initialObservation,
        CancellationToken cancellationToken
    ) {
        var request = new CompletionRequest(
            ModelId: config.ModelId,
            SystemPrompt: BuildDirectorSystemPrompt(),
            Context: [new ObservationMessage(initialObservation)],
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var result = await GetClient(config).StreamCompletionAsync(request, null, cancellationToken).ConfigureAwait(false);
        if (result.Errors is { Count: > 0 }) {
            return AsyncAteliaResult<string>.Failure(
                new TextAdvError(
                    "TextAdv.LlmPlayerDirectorProviderError",
                    BuildProviderErrorMessage(result.Errors)
                )
            );
        }

        var text = result.Message.GetFlattenedText().Trim();
        if (string.IsNullOrWhiteSpace(text)) {
            text = "导演阶段没有产出可见文本。执行阶段请只依据 Perception-Bundle、Memory-Notebook 和可用工具，保守提交 Large-Action。";
        }

        return AsyncAteliaResult<string>.Success(text);
    }

    private static string BuildDirectorNotesObservation(string directorNotes) {
        return $"""
[导演札记：供本回合工具执行阶段参考]
{directorNotes}

[执行要求]
- 你现在必须通过工具行动。
- 如果导演札记建议更新 notebook，可以先调用 player_edit_memory_notebook。
- 最终必须调用 exactly one Large-Action 工具。
- 不要把导演札记中的猜测当成已确认世界事实。
""";
    }

    private static string BuildInitialObservation(PerceptionBundle perception) {
        var sb = new StringBuilder();
        sb.AppendLine("[你的当前 Perception-Bundle]");
        sb.AppendLine($"- ActorId: {perception.ActorId}");
        sb.AppendLine($"- ActorKind: {perception.ActorKind}");
        sb.AppendLine($"- ActorName: {perception.ActorName}");
        sb.AppendLine($"- ActorProfileNote: {perception.ActorProfileNote}");
        sb.AppendLine($"- Time: {GameClock.FormatClock(perception.Day, perception.Slot, perception.SlotsPerDay)}");
        sb.AppendLine($"- LocationId: {perception.Location.LocationId}");
        sb.AppendLine($"- LocationName: {perception.Location.Name}");
        sb.AppendLine($"- LocationDescription: {perception.Location.Description}");
        sb.AppendLine();

        sb.AppendLine("[Exits]");
        if (perception.Location.Exits.Count == 0) {
            sb.AppendLine("(none)");
        }
        else {
            foreach (var exit in perception.Location.Exits) {
                sb.AppendLine($"- {exit.Direction} -> {exit.TargetLocationId} ({exit.TargetName})");
            }
        }

        sb.AppendLine();
        sb.AppendLine("[VisibleItems]");
        if (perception.Location.Items.Count == 0) {
            sb.AppendLine("(none)");
        }
        else {
            foreach (var item in perception.Location.Items) {
                sb.AppendLine($"- {item.ItemId}: {item.Name} | {item.Description}");
                AppendInteractions(sb, item.Interactions);
            }
        }

        sb.AppendLine();
        sb.AppendLine("[InventoryItems]");
        if (perception.InventoryItems.Count == 0) {
            sb.AppendLine("(none)");
        }
        else {
            foreach (var item in perception.InventoryItems) {
                sb.AppendLine($"- {item.ItemId}: {item.Name} | {item.Description}");
                AppendInteractions(sb, item.Interactions);
            }
        }

        sb.AppendLine();
        sb.AppendLine("[VisibleActors]");
        if (perception.Location.Actors.Count == 0) {
            sb.AppendLine("(none)");
        }
        else {
            foreach (var actor in perception.Location.Actors) {
                sb.AppendLine($"- {actor.ActorId}: {actor.Name} ({actor.Kind}) | {actor.ProfileNote}");
                AppendInteractions(sb, actor.Interactions);
            }
        }

        sb.AppendLine();
        sb.AppendLine("[LocationInteractions]");
        AppendInteractions(sb, perception.Location.Interactions);
        if (perception.Location.Interactions.Count == 0) {
            sb.AppendLine("(none)");
        }

        sb.AppendLine();
        sb.AppendLine("[Memory-Notebook]");
        sb.AppendLine(NotebookBlockViewRenderer.RenderBlockView(perception.NotebookBlocks));

        sb.AppendLine();
        sb.AppendLine("[Your Accepted Steps This Turn]");
        if (perception.AcceptedSteps.Count == 0) {
            sb.AppendLine("(none)");
        }
        else {
            foreach (var step in perception.AcceptedSteps) {
                sb.AppendLine($"- {step.StepNumber}. {step.ActionKind}: {step.ActionSummary}");
                sb.AppendLine($"  reason: {step.PreActionReason}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("[可用 Small-Action 工具]");
        sb.AppendLine("- player_edit_memory_notebook(reason, edit_script)");
        sb.AppendLine();
        sb.AppendLine("[可用 Large-Action 工具]");
        sb.AppendLine("- player_rest_a_while(reason)");
        sb.AppendLine("- player_explore(reason, direction, focus)");
        sb.AppendLine("- player_interact(reason, interaction_id)");
        return sb.ToString();
    }

    private static void AppendInteractions(StringBuilder sb, IReadOnlyList<InteractionPerception> interactions) {
        foreach (var interaction in interactions) {
            var precondition = string.IsNullOrWhiteSpace(interaction.PreconditionNote)
                ? "none"
                : interaction.PreconditionNote;
            sb.AppendLine($"  - interaction {interaction.InteractionId}: {interaction.TargetKind}:{interaction.TargetId} | {interaction.ActionKind} | {interaction.VisibleLabel} | precondition: {precondition}");
        }
    }

    private static string BuildToolResultsObservation(IReadOnlyList<ToolCallExecutionResult> results) {
        var sb = new StringBuilder();
        sb.AppendLine("[工具执行结果]");
        foreach (var result in results) {
            sb.AppendLine($"- {result.ToolName}#{result.ToolCallId}: {result.ExecuteResult.Status}");
            sb.AppendLine(result.ExecuteResult.Content);
        }

        return sb.ToString();
    }

    private static DeepSeekV4ChatClient GetClient(LlmPlayerConfig config) {
        lock (s_gate) {
            if (s_client is not null) { return s_client; }

            s_client = new DeepSeekV4ChatClient(
                apiKey: config.ApiKey!,
                baseAddress: string.IsNullOrWhiteSpace(config.BaseAddress) ? null : new Uri(config.BaseAddress),
                options: new OpenAIChatClientOptions {
                    ExtraBody = new JsonObject {
                        ["thinking"] = new JsonObject {
                            ["type"] = "enabled"
                        },
                        ["reasoning_effort"] = "medium"
                    }
                }
            );
            return s_client;
        }
    }

    private static LlmPlayerConfig GetConfig() {
        lock (s_gate) {
            if (s_config is not null) { return s_config; }

            s_config = new LlmPlayerConfig(
                BaseAddress: GetOptionalEnvironment(BaseAddressEnv),
                ModelId: GetEnvironmentOrDefault(ModelIdEnv, GetEnvironmentOrDefault(FallbackModelIdEnv, DefaultModelId)),
                ApiKey: GetOptionalEnvironment(ApiKeyEnv),
                Mode: GetEnvironmentOrDefault(ModeEnv, "auto"),
                Pipeline: GetEnvironmentOrDefault(PipelineEnv, DirectorExecutorPipeline),
                MaxAttempts: GetPositiveIntEnvironment(MaxAttemptsEnv, DefaultMaxAttempts)
            );
            return s_config;
        }
    }

    private static bool UsesDirectorExecutorPipeline(LlmPlayerConfig config)
        => !string.Equals(config.Pipeline, SinglePipeline, StringComparison.OrdinalIgnoreCase);

    private static string BuildProviderErrorMessage(IReadOnlyList<string> errors) {
        var message = string.Join(
            "; ",
            errors
                .Select(static error => error?.Trim())
                .Where(static error => !string.IsNullOrWhiteSpace(error))
        );

        return string.IsNullOrWhiteSpace(message)
            ? "LLM Player provider returned unknown error."
            : $"LLM Player provider error: {message}";
    }

    private static string GetEnvironmentOrDefault(string key, string defaultValue) {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
    }

    private static string? GetOptionalEnvironment(string key) {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int GetPositiveIntEnvironment(string key, int defaultValue) {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value)) { return defaultValue; }
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : defaultValue;
    }

    private sealed class PlayerActionToolService {
        private readonly DurableDict<string> _root;
        private readonly string _actorId;

        public PlayerActionToolService(DurableDict<string> root, string actorId, PerceptionBundle perception) {
            _root = root;
            _actorId = actorId;
            CurrentPerception = perception;
        }

        public PlayerActionProposal? Proposal { get; private set; }

        public PerceptionBundle CurrentPerception { get; private set; }

        public void ClearProposal() {
            Proposal = null;
        }

        [Tool("player_edit_memory_notebook", "Small-Action：编辑自己的私人 Memory-Notebook，不结束当前回合。")]
        public async ValueTask<ToolExecuteResult> EditMemoryNotebookAsync(
            [ToolParam("事前推理：依据当前可见信息说明为什么要这样更新记忆。")] string reason,
            [ToolParam("TextEditScript 片段或完整 XML，例如 <insert side=\"after\" anchor=\"tail\">...</insert>。")] string edit_script,
            CancellationToken cancellationToken
        ) {
            if (Proposal is not null) {
                return new ToolExecuteResult(
                    ToolExecutionStatus.Failed,
                    "已经提交了 Large-Action，不能再执行 Small-Action。"
                );
            }

            var prepareResult = GameNotebookEditService.Prepare(CurrentPerception.NotebookBlocks, edit_script);
            if (!prepareResult.TryGetValue(out var proposal) || proposal is null) {
                return new ToolExecuteResult(
                    ToolExecutionStatus.Failed,
                    prepareResult.Error?.Message ?? "notebook edit script 无效。"
                );
            }

            GameActionValidator.ValidationResult validation;
            try {
                validation = await GameActionValidator.ValidateActionAsync(
                    CurrentPerception,
                    actionKind: "small/edit-memory-notebook",
                    proposal.ActionSummary,
                    reason,
                    actionPayload: proposal.ValidatorPayload,
                    cancellationToken
                ).ConfigureAwait(false);
            }
            catch (Exception ex) {
                return new ToolExecuteResult(
                    ToolExecutionStatus.Failed,
                    $"notebook edit validator 调用失败：{ex.Message}"
                );
            }

            if (!validation.Accepted) {
                return new ToolExecuteResult(
                    ToolExecutionStatus.Failed,
                    $"validator 拒绝 notebook edit：{validation.Feedback}"
                );
            }

            CurrentPerception = GameSimulation.ApplyNotebookEditForActor(
                _root,
                _actorId,
                proposal,
                reason,
                validation.Feedback
            );

            return new ToolExecuteResult(
                ToolExecutionStatus.Success,
                "Memory-Notebook 已更新。\n[当前 notebook]\n" + NotebookBlockViewRenderer.RenderBlockView(CurrentPerception.NotebookBlocks)
            );
        }

        [Tool("player_rest_a_while", "提交 Large-Action：谨慎观察并暂不移动。没有明确目标或风险较高时优先选择。")]
        public ValueTask<ToolExecuteResult> RestAWhileAsync(
            [ToolParam("事前推理：依据当前可见信息说明为什么此刻选择暂不移动。")] string reason,
            CancellationToken cancellationToken
        ) {
            if (!TrySetProposal(
                new PlayerActionProposal(
                    "large/rest-a-while",
                    "谨慎观察并暂不移动",
                    null,
                    reason
                ),
                out var result
            )) {
                return ValueTask.FromResult(result);
            }

            return ValueTask.FromResult(result);
        }

        [Tool("player_explore", "提交 Large-Action：向指定方向探索。可以探索可见出口，也可以试探未知方向。")]
        public ValueTask<ToolExecuteResult> ExploreAsync(
            [ToolParam("事前推理：依据当前可见信息说明为什么探索这个方向。")] string reason,
            [ToolParam("探索方向，例如 north/south/east/west/inside。")] string direction,
            [ToolParam("可选：希望重点寻找或确认的对象；没有则传 null。")] string? focus,
            CancellationToken cancellationToken
        ) {
            direction = string.IsNullOrWhiteSpace(direction) ? string.Empty : direction.Trim();
            focus = NormalizeOptionalToolString(focus);
            if (string.IsNullOrWhiteSpace(direction)) {
                return ValueTask.FromResult(new ToolExecuteResult(
                    ToolExecutionStatus.Failed,
                    "direction 不能为空。"
                ));
            }

            var summary = focus is null
                ? $"向 {direction} 探索"
                : $"向 {direction} 探索：{focus}";
            var payload = focus is null
                ? $"direction={direction}"
                : $"direction={direction}\nfocus={focus}";

            if (!TrySetProposal(
                new PlayerActionProposal(
                    "large/explore",
                    summary,
                    payload,
                    reason
                ),
                out var result
            )) {
                return ValueTask.FromResult(result);
            }

            return ValueTask.FromResult(result);
        }

        [Tool("player_interact", "提交 Large-Action：执行当前 Perception-Bundle 中可见的 interaction。")]
        public ValueTask<ToolExecuteResult> InteractAsync(
            [ToolParam("事前推理：依据当前可见 interaction 和环境说明为什么执行它。")] string reason,
            [ToolParam("当前 Perception-Bundle 中可见的 interaction_id。")] string interaction_id,
            CancellationToken cancellationToken
        ) {
            var interactionResult = GameSimulation.TryGetVisibleInteraction(CurrentPerception, interaction_id);
            if (!interactionResult.TryGetValue(out var interaction) || interaction is null) {
                return ValueTask.FromResult(new ToolExecuteResult(
                    ToolExecutionStatus.Failed,
                    interactionResult.Error?.Message ?? $"当前看不到 interaction '{interaction_id}'。"
                ));
            }

            if (!TrySetProposal(
                new PlayerActionProposal(
                    "large/interact",
                    $"{interaction.VisibleLabel} ({interaction.ActionKind})",
                    GameSimulation.BuildInteractionPayload(interaction),
                    reason
                ),
                out var result
            )) {
                return ValueTask.FromResult(result);
            }

            return ValueTask.FromResult(result);
        }

        private bool TrySetProposal(PlayerActionProposal proposal, out ToolExecuteResult result) {
            if (Proposal is not null) {
                result = new ToolExecuteResult(
                    ToolExecutionStatus.Failed,
                    "本回合已经提交过一个 Large-Action；请 exactly one tool call。"
                );
                return false;
            }

            if (string.IsNullOrWhiteSpace(proposal.PreActionReason)) {
                result = new ToolExecuteResult(
                    ToolExecutionStatus.Failed,
                    "reason 不能为空；必须提供 grounded 的事前推理。"
                );
                return false;
            }

            Proposal = proposal;
            result = new ToolExecuteResult(
                ToolExecutionStatus.Success,
                $"已暂存 Large-Action: {proposal.ActionKind} — {proposal.ActionSummary}"
            );
            return true;
        }

        private static string? NormalizeOptionalToolString(string? value) {
            if (string.IsNullOrWhiteSpace(value)) { return null; }

            value = value.Trim();
            return string.Equals(value, "null", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "(none)", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : value;
        }
    }
}
