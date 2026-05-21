using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using Atelia.Completion.Transport;
using Atelia.Completion.Tools;
using Atelia.StateJournal;

namespace Atelia.TextAdv;

internal static class LlmPlayerAgentDriver {
    private const string BaseAddressEnv = "DEEPSEEK_BASE_URL";
    private const string ModelIdEnv = "ATELIA_TEXTADV_LLM_PLAYER_MODEL_ID";
    private const string FallbackModelIdEnv = "DEEPSEEK_MODEL";
    private const string ApiKeyEnv = "DEEPSEEK_API_KEY";
    private const string PipelineEnv = "ATELIA_TEXTADV_LLM_PLAYER_PIPELINE";
    private const string MaxAttemptsEnv = "ATELIA_TEXTADV_LLM_PLAYER_MAX_ATTEMPTS";
    private const string DefaultModelId = "deepseek-v4-flash";
    private const string DefaultBaseAddress = "https://api.deepseek.com/";
    private const string DirectorExecutorPipeline = "director-executor";
    private const string SinglePipeline = "single";
    private const int DefaultMaxAttempts = 3;

    private static readonly Lock s_gate = new();
    private static DeepSeekV4ChatClient? s_client;
    private static HttpClient? s_httpClient;
    private static LlmPlayerConfig? s_config;
    private static LlmPlayerStub? s_stub;

    private sealed record LlmPlayerConfig(
        string? BaseAddress,
        string ModelId,
        string? ApiKey,
        string Pipeline,
        int MaxAttempts
    );

    internal sealed record LlmPlayerStub(
        Func<DurableDict<string>, string, CancellationToken, Task<AsyncAteliaResult<TurnCollectionStatus>>> SubmitLargeActionAsync
    );

    internal static async Task<AsyncAteliaResult<TurnCollectionStatus>> TrySubmitLargeActionAsync(
        DurableDict<string> root,
        string actorId,
        CancellationToken cancellationToken
    ) {
        var stub = GetStub();
        if (stub is not null) { return await stub.SubmitLargeActionAsync(root, actorId, cancellationToken).ConfigureAwait(false); }

        var config = GetConfig();

        try {
            var perception = GameSimulation.DescribePerceptionForActor(root, actorId);
            var toolService = new PlayerActionToolService(root, actorId, perception);
            var toolExecutor = CreateToolExecutor(toolService);
            var initialObservation = BuildInitialObservation(perception, toolExecutor.GetVisibleToolDefinitions());
            var history = new List<IHistoryMessage>
            {
                new ObservationMessage(initialObservation)
            };

            if (UsesDirectorExecutorPipeline(config)) {
                var directorNotes = await BuildDirectorNotesAsync(config, initialObservation, cancellationToken)
                    .ConfigureAwait(false);
                if (directorNotes.IsFailure) { return AsyncAteliaResult<TurnCollectionStatus>.Failure(directorNotes.Error!); }

                history.Add(new ObservationMessage(BuildDirectorNotesObservation(directorNotes.Value!)));
            }

            for (var attempt = 1; attempt <= config.MaxAttempts; attempt++) {
                var request = new CompletionRequest(
                    ModelId: config.ModelId,
                    SystemPrompt: BuildSystemPrompt(),
                    Context: history,
                    Tools: toolExecutor.GetVisibleToolDefinitions()
                );
                var result = await GetClient(config).StreamCompletionAsync(request, null, cancellationToken).ConfigureAwait(false);
                if (result.Errors is { Count: > 0 }) {
                    return AsyncAteliaResult<TurnCollectionStatus>.Failure(
                        new TextAdvError(
                            "TextAdv.LlmPlayerProviderError",
                            BuildProviderErrorMessage(result.Errors)
                        )
                    );
                }

                var action = result.Message;
                history.Add(action);
                if (action.ToolCalls.Count == 0) {
                    history.Add(new ObservationMessage(BuildMissingToolCallObservation()));
                    continue;
                }

                var executionResults = new List<ToolCallExecutionResult>(action.ToolCalls.Count);
                foreach (var toolCall in action.ToolCalls) {
                    executionResults.Add(await toolExecutor.ExecuteAsync(toolCall, cancellationToken).ConfigureAwait(false));
                }

                var toolResults = executionResults
                    .Select(
                    static item => new ToolResult(
                        item.ToolName,
                        item.ToolCallId,
                        item.ExecuteResult.Status,
                        item.ExecuteResult.Content
                    )
                )
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
                    history.Add(new ObservationMessage(BuildToolFailureObservation()));
                    continue;
                }

                if (toolService.Proposal is null) {
                    history.Add(new ObservationMessage(BuildAfterSmallActionObservation()));
                    continue;
                }

                var plan = toolService.Proposal;
                GameActionValidator.ValidationResult validation;
                try {
                    var descriptor = plan.Descriptor;
                    validation = await GameActionValidator.ValidateActionAsync(
                        toolService.CurrentPerception,
                        descriptor.ActionKind,
                        descriptor.ActionSummary,
                        descriptor.PreActionReason,
                        descriptor.ActionPayload,
                        cancellationToken
                    ).ConfigureAwait(false);
                }
                catch (Exception ex) {
                    return AsyncAteliaResult<TurnCollectionStatus>.Failure(
                        new TextAdvError(
                            "TextAdv.LlmPlayerValidatorFailed",
                            $"LLM Player validator 调用失败：{ex.Message}"
                        )
                    );
                }

                if (!validation.Accepted) {
                    toolService.ClearProposal();
                    history.Add(
                        new ObservationMessage(
                            $"validator 拒绝了你的行动，请根据反馈重试。\n[validator feedback]\n{validation.Feedback}"
                        )
                    );
                    continue;
                }

                var submitResult = GameSimulation.SubmitLargeActionForActor(
                    root,
                    actorId,
                    plan.Descriptor,
                    validation.Feedback
                );
                return submitResult.IsSuccess
                    ? AsyncAteliaResult<TurnCollectionStatus>.Success(submitResult.Value!)
                    : AsyncAteliaResult<TurnCollectionStatus>.Failure(submitResult.Error!);
            }

            return AsyncAteliaResult<TurnCollectionStatus>.Failure(
                new TextAdvError(
                    "TextAdv.LlmPlayerNoAcceptedLargeAction",
                    $"LLM Player Agent 在 {config.MaxAttempts} 次尝试内没有提交 validator 通过的 Large-Action。"
                )
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return AsyncAteliaResult<TurnCollectionStatus>.Failure(
                new TextAdvError(
                    "TextAdv.LlmPlayerFailed",
                    $"LLM Player Agent 失败：{ex.Message}"
                )
            );
        }
    }

    private static ToolExecutor CreateToolExecutor(PlayerActionToolService toolService)
        => new([
            OverrideToolMetadata(
                MethodToolWrapper.FromDelegate<string, string>(toolService.EditMemoryNotebookAsync),
                PlayerActionGuideCatalog.GetEditMemoryNotebookToolMetadata()
            ),
            OverrideToolMetadata(
                MethodToolWrapper.FromDelegate<string>(toolService.RestAWhileAsync),
                PlayerActionGuideCatalog.GetRestAWhileToolMetadata()
            ),
            OverrideToolMetadata(
                MethodToolWrapper.FromDelegate<string, string, string?>(toolService.ExploreAsync),
                PlayerActionGuideCatalog.GetExploreToolMetadata()
            ),
            OverrideToolMetadata(
                MethodToolWrapper.FromDelegate<string, string>(toolService.InteractAsync),
                PlayerActionGuideCatalog.GetInteractToolMetadata()
            ),
        ]);

    private static string BuildSystemPrompt() {
        return """
你是 TextAdv 的 LLM Player Agent，负责扮演一个 active player actor。

你的任务是根据自己的 Perception-Bundle、Memory-Notebook、原生工具 schema，以及可能出现的导演札记，为当前回合完成行动：你可以先做 small actions，也可以用 `player_interact` 处理当前可见 interaction；系统会判定 interaction 属于 small 还是 large，但本回合最终仍必须落成 exactly one Large-Action。

硬规则：
1. 你只能依据输入给你的 actor 私有视角行动，不能假装知道完整世界真相。
2. 工具参数里的 reason 必须先说明当前证据如何支持这个动作，不要写成事后解释。
3. 你必须通过工具行动；可以先做零到多个 Small-Action，也可以用 `player_interact` 处理当前可见 interaction。若该 interaction 属于 small，会立即执行；若属于 large，会成为本回合 proposal。不要只返回自然语言。
4. 不要试图直接改世界账本；你只是更新自己的记忆或声明玩家意图，GM 会统一结算。
5. 导演札记是行动参考，不是世界真相；若札记与 Perception-Bundle 冲突，以 Perception-Bundle 和工具结果为准。
6. 每次工具调用后你会收到结果；若当前只完成了 small actions，请继续行动，直到本回合最终落成 exactly one Large-Action。
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
            text = "导演阶段没有产出可见文本。执行阶段请只依据 Perception-Bundle、Memory-Notebook 和可用工具；你可以先做 small actions，也可以用 player_interact 处理当前可见 interaction。系统会判定 small / large，但本回合最终仍要落成 exactly one Large-Action。";
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
- 你也可以用 player_interact 处理当前可见 interaction；系统会判定它是 small 还是 large。
- 若当前只完成了 small actions，请继续行动，直到本回合最终落成 exactly one Large-Action。
- 不要把导演札记中的猜测当成已确认世界事实。
""";
    }

    private static string BuildMissingToolCallObservation() {
        return "你必须调用工具行动：可以先做 Small-Action（编辑 notebook，或用 player_interact 处理当前可见 interaction）。系统会判定 interaction 是 small 还是 large；若当前只完成了 small actions，请继续行动，直到本回合最终落成 exactly one Large-Action。不要只返回自然语言。";
    }

    private static string BuildToolFailureObservation() {
        return "工具调用失败。请修正参数；你可以先做 Small-Action（编辑 notebook，或用 player_interact 处理当前可见 interaction）。系统会判定 interaction 是 small 还是 large；本回合最终仍必须落成 exactly one Large-Action。";
    }

    private static string BuildAfterSmallActionObservation() {
        return "Small-Action 已处理。现在请根据更新后的 Perception-Bundle、Memory-Notebook 和工具结果继续行动；你也可以用 player_interact 处理当前可见 interaction。系统会判定它是 small 还是 large，本回合最终仍要落成 exactly one Large-Action。";
    }

    private static string BuildInitialObservation(
        PerceptionBundle perception,
        ImmutableArray<ToolDefinition> toolDefinitions
    ) {
        var sb = new StringBuilder();
        sb.AppendLine("[你的当前 Perception-Bundle]");
        sb.AppendLine(PerceptionEvidenceRenderer.RenderForPrompt(perception));

        sb.AppendLine();
        sb.AppendLine(PlayerActionGuideCatalog.BuildLlmPlayerManual());
        sb.AppendLine();
        sb.AppendLine("[当前可用原生工具 schema]");
        sb.Append(ToolSchemaTextRenderer.RenderDefinitions(toolDefinitions));

        return sb.ToString();
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

            s_httpClient = CompletionHttpTransportFactory.CreateLiveClient(ResolveBaseAddress(config.BaseAddress));
            s_client = new DeepSeekV4ChatClient(
                apiKey: config.ApiKey!,
                httpClient: s_httpClient,
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

    private static Uri ResolveBaseAddress(string? configuredBaseAddress) {
        return string.IsNullOrWhiteSpace(configuredBaseAddress)
            ? new Uri(DefaultBaseAddress)
            : new Uri(configuredBaseAddress, UriKind.Absolute);
    }

    private static LlmPlayerConfig GetConfig() {
        lock (s_gate) {
            if (s_config is not null) { return s_config; }

            var removedMode = TextAdvRuntimeEnvironment.GetOptionalEnvironment("ATELIA_TEXTADV_LLM_PLAYER_MODE");
            if (!string.IsNullOrWhiteSpace(removedMode)) {
                throw new InvalidOperationException(
                    "ATELIA_TEXTADV_LLM_PLAYER_MODE 已移除。运行时现在只支持真实 LLM Player；测试请显式注入 LlmPlayerStub。"
                );
            }

            s_config = new LlmPlayerConfig(
                BaseAddress: TextAdvRuntimeEnvironment.GetOptionalEnvironment(BaseAddressEnv),
                ModelId: TextAdvRuntimeEnvironment.GetEnvironmentOrDefault(
                    ModelIdEnv,
                    TextAdvRuntimeEnvironment.GetEnvironmentOrDefault(FallbackModelIdEnv, DefaultModelId)
                ),
                ApiKey: TextAdvRuntimeEnvironment.GetOptionalEnvironment(ApiKeyEnv),
                Pipeline: TextAdvRuntimeEnvironment.GetEnvironmentOrDefault(PipelineEnv, DirectorExecutorPipeline),
                MaxAttempts: TextAdvRuntimeEnvironment.GetPositiveIntEnvironment(MaxAttemptsEnv, DefaultMaxAttempts)
            );
            if (string.IsNullOrWhiteSpace(s_config.ApiKey)) {
                throw new InvalidOperationException(
                    $"{ApiKeyEnv} 未配置。TextAdv 运行时现在要求真实 LLM Player 可用；测试请显式注入 LlmPlayerStub。"
                );
            }

            return s_config;
        }
    }

    internal static void SetStubForTests(LlmPlayerStub? stub) {
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

    private static LlmPlayerStub? GetStub() {
        lock (s_gate) {
            return s_stub;
        }
    }

    private static bool UsesDirectorExecutorPipeline(LlmPlayerConfig config)
        => !string.Equals(config.Pipeline, SinglePipeline, StringComparison.OrdinalIgnoreCase);

    private static string BuildProviderErrorMessage(IReadOnlyList<string> errors) {
        return TextAdvRuntimeEnvironment.BuildProviderErrorMessage(
            errors,
            prefix: "LLM Player provider error: ",
            defaultMessage: "LLM Player provider returned unknown error."
        );
    }

    private static ITool OverrideToolMetadata(
        ITool inner,
        ToolDefinition metadata
    ) {
        return new ToolMetadataOverrideTool(inner, metadata);
    }

    private sealed class ToolMetadataOverrideTool : ITool {
        private readonly ITool _inner;

        public ToolMetadataOverrideTool(
            ITool inner,
            ToolDefinition metadata
        ) {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            Definition = ToolContracts.CreateCompatibleFlatOverride(inner.Definition, metadata);
        }

        public ToolDefinition Definition { get; }

        public bool Visible {
            get => _inner.Visible;
            set => _inner.Visible = value;
        }

        public ValueTask<ToolExecuteResult> ExecuteAsync(
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken
        ) {
            return _inner.ExecuteAsync(arguments, cancellationToken);
        }
    }

    private sealed class PlayerActionToolService {
        private readonly DurableDict<string> _root;
        private readonly string _actorId;
        private readonly Func<PerceptionBundle, string, string, string, string?, CancellationToken, Task<GameActionValidator.ValidationResult>> _validateActionAsync;

        public PlayerActionToolService(DurableDict<string> root, string actorId, PerceptionBundle perception) {
            _root = root;
            _actorId = actorId;
            CurrentPerception = perception;
            _validateActionAsync = GameActionValidator.ValidateActionAsync;
        }

        public PlayerActionToolService(
            DurableDict<string> root,
            string actorId,
            PerceptionBundle perception,
            Func<PerceptionBundle, string, string, string, string?, CancellationToken, Task<GameActionValidator.ValidationResult>> validateActionAsync
        ) {
            _root = root;
            _actorId = actorId;
            CurrentPerception = perception;
            _validateActionAsync = validateActionAsync ?? throw new ArgumentNullException(nameof(validateActionAsync));
        }

        public TerminalActionExecutionPlan? Proposal { get; private set; }

        public PerceptionBundle CurrentPerception { get; private set; }

        public void ClearProposal() {
            Proposal = null;
        }

        [Tool("player_edit_memory_notebook", PlayerActionGuideText.EditMemoryNotebookToolDescription)]
        public async ValueTask<ToolExecuteResult> EditMemoryNotebookAsync(
            [ToolParam(PlayerActionGuideText.EditMemoryNotebookReasonAttributeDescription)] string reason,
            [ToolParam(PlayerActionGuideText.EditScriptParameterDescription)] string edit_script,
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
                validation = await _validateActionAsync(
                    CurrentPerception,
                    TerminalActionKinds.SmallEditMemoryNotebook,
                    proposal.ActionSummary,
                    reason,
                    proposal.ValidatorPayload,
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

        [Tool("player_rest_a_while", PlayerActionGuideText.RestAWhileToolDescription)]
        public ValueTask<ToolExecuteResult> RestAWhileAsync(
            [ToolParam(PlayerActionGuideText.RestReasonAttributeDescription)] string reason,
            CancellationToken cancellationToken
        ) {
            return BuildLargeProposalResult(
                GameSimulation.BuildRestAWhileTerminalPlan(reason),
                "当前不能构造 rest-a-while 动作。"
            );
        }

        [Tool("player_explore", PlayerActionGuideText.ExploreToolDescription)]
        public ValueTask<ToolExecuteResult> ExploreAsync(
            [ToolParam(PlayerActionGuideText.ExploreReasonAttributeDescription)] string reason,
            [ToolParam(PlayerActionGuideText.DirectionParameterDescription)] string direction,
            [ToolParam(PlayerActionGuideText.FocusParameterDescription)] string? focus = null,
            CancellationToken cancellationToken = default
        ) {
            focus = NormalizeOptionalToolString(focus);
            return BuildLargeProposalResult(
                GameSimulation.BuildExploreTerminalPlan(direction, focus, reason),
                "当前不能构造 explore 动作。"
            );
        }

        [Tool("player_interact", PlayerActionGuideText.InteractToolDescription)]
        public async ValueTask<ToolExecuteResult> InteractAsync(
            [ToolParam(PlayerActionGuideText.InteractReasonAttributeDescription)] string reason,
            [ToolParam(PlayerActionGuideText.InteractionIdParameterDescription)] string interaction_id,
            CancellationToken cancellationToken
        ) {
            if (Proposal is not null) {
                return new ToolExecuteResult(
                    ToolExecutionStatus.Failed,
                    "已经提交了 Large-Action，不能再执行 Small-Action。"
                );
            }

            var planResult = GameSimulation.BuildTerminalInteractionPlan(CurrentPerception, interaction_id, reason);
            if (!planResult.TryGetValue(out var plan) || plan is null) {
                return new ToolExecuteResult(
                    ToolExecutionStatus.Failed,
                    planResult.Error?.Message ?? $"当前不能执行 interaction '{interaction_id}'。"
                );
            }

            if (plan.Tier == TerminalActionTier.Large) {
                if (!TrySetProposal(plan, out var result)) { return result; }
                return result;
            }

            GameActionValidator.ValidationResult validation;
            try {
                var descriptor = plan.Descriptor;
                validation = await _validateActionAsync(
                    CurrentPerception,
                    descriptor.ActionKind,
                    descriptor.ActionSummary,
                    descriptor.PreActionReason,
                    descriptor.ActionPayload,
                    cancellationToken
                ).ConfigureAwait(false);
            }
            catch (Exception ex) {
                return new ToolExecuteResult(
                    ToolExecutionStatus.Failed,
                    $"interaction validator 调用失败：{ex.Message}"
                );
            }

            if (!validation.Accepted) {
                return new ToolExecuteResult(
                    ToolExecutionStatus.Failed,
                    $"validator 拒绝 interaction：{validation.Feedback}"
                );
            }

            var interactionPlan = plan as TerminalActionExecutionPlan.Interaction;
            if (interactionPlan is null) {
                return new ToolExecuteResult(
                    ToolExecutionStatus.Failed,
                    $"interaction '{interaction_id}' 没有生成合法的 interaction plan。"
                );
            }

            var resolutionResult = await GameSimulation.ExecuteInteractionPlanForActorAsync(
                _root,
                _actorId,
                interactionPlan,
                validation.Feedback,
                cancellationToken
            ).ConfigureAwait(false);

            if (!resolutionResult.TryGetValue(out var resolution) || resolution is null) {
                return new ToolExecuteResult(
                    ToolExecutionStatus.Failed,
                    resolutionResult.Error?.Message ?? $"interaction '{interaction_id}' 结算失败。"
                );
            }

            CurrentPerception = resolution.NextPerception;
            return new ToolExecuteResult(
                ToolExecutionStatus.Success,
                "Small-Action 已执行。\n[结算摘要]\n"
                + resolution.Summary
                + "\n\n[当前 Perception-Bundle]\n"
                + PerceptionEvidenceRenderer.RenderForPrompt(CurrentPerception)
            );
        }

        private bool TrySetProposal(TerminalActionExecutionPlan plan, out ToolExecuteResult result) {
            if (Proposal is not null) {
                result = new ToolExecuteResult(
                    ToolExecutionStatus.Failed,
                    "本回合已经提交过一个 Large-Action；请 exactly one tool call。"
                );
                return false;
            }

            if (plan.Tier != TerminalActionTier.Large) {
                result = new ToolExecuteResult(
                    ToolExecutionStatus.Failed,
                    "当前工具只能暂存会结束本回合的 Large-Action。"
                );
                return false;
            }

            Proposal = plan;
            result = new ToolExecuteResult(
                ToolExecutionStatus.Success,
                $"已暂存 Large-Action: {plan.ActionKind} — {plan.ActionSummary}"
            );
            return true;
        }

        private ValueTask<ToolExecuteResult> BuildLargeProposalResult(
            AteliaResult<TerminalActionExecutionPlan> planResult,
            string fallbackFailureMessage
        ) {
            if (!planResult.TryGetValue(out var plan) || plan is null) {
                return ValueTask.FromResult(
                    new ToolExecuteResult(
                        ToolExecutionStatus.Failed,
                        planResult.Error?.Message ?? fallbackFailureMessage
                    )
                );
            }

            if (!TrySetProposal(plan, out var result)) { return ValueTask.FromResult(result); }
            return ValueTask.FromResult(result);
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
