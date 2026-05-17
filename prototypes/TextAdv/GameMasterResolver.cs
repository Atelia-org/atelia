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

    private static async Task<GmExploreResolution> ResolveExploreWithLlmAsync(
        DurableDict<string> root,
        GmExploreContext context,
        GmConfig config,
        CancellationToken cancellationToken
    ) {
        var toolExecutor = CreateToolExecutor(root);
        var history = new List<IHistoryMessage> {
            new ObservationMessage(BuildExploreObservation(context))
        };
        var client = GetClient(config);
        ActionMessage? lastAction = null;

        for (var round = 1; round <= config.MaxRounds; round++) {
            var request = new CompletionRequest(
                ModelId: config.ModelId,
                SystemPrompt: BuildExploreSystemPrompt(),
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
                var finalSummary = NormalizeSummary(lastAction.GetFlattenedText());
                if (string.IsNullOrWhiteSpace(finalSummary)) {
                    finalSummary = "GM Agent 完成了本回合探索结算。";
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
                    BuildToolResultsObservation(executionResults),
                    toolResults,
                    failure?.ExecuteResult.Content
                )
            );
        }

        var text = NormalizeSummary(lastAction?.GetFlattenedText() ?? string.Empty);
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(text)
                ? $"GM Agent 在 {config.MaxRounds} 轮内没有完成结算。"
                : $"GM Agent 在 {config.MaxRounds} 轮内仍未停止调用工具。最后文本：{text}"
        );
    }

    private static async Task<GmExploreResolution> ResolveInteractionWithLlmAsync(
        DurableDict<string> root,
        GmInteractionContext context,
        GmConfig config,
        CancellationToken cancellationToken
    ) {
        var toolExecutor = CreateToolExecutor(root);
        var history = new List<IHistoryMessage> {
            new ObservationMessage(BuildInteractionObservation(context))
        };
        var client = GetClient(config);
        ActionMessage? lastAction = null;

        for (var round = 1; round <= config.MaxRounds; round++) {
            var request = new CompletionRequest(
                ModelId: config.ModelId,
                SystemPrompt: BuildInteractionSystemPrompt(),
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
                var finalSummary = NormalizeSummary(lastAction.GetFlattenedText());
                if (string.IsNullOrWhiteSpace(finalSummary)) {
                    finalSummary = "GM Agent 完成了本回合交互结算。";
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
                    BuildToolResultsObservation(executionResults),
                    toolResults,
                    failure?.ExecuteResult.Content
                )
            );
        }

        var text = NormalizeSummary(lastAction?.GetFlattenedText() ?? string.Empty);
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(text)
                ? $"GM Agent 在 {config.MaxRounds} 轮内没有完成交互结算。"
                : $"GM Agent 在 {config.MaxRounds} 轮内仍未停止调用工具。最后文本：{text}"
        );
    }

    private static ToolExecutor CreateToolExecutor(DurableDict<string> root) {
        var toolService = new GmWorldEditService(root);
        return new ToolExecutor(
        [
            MethodToolWrapper.FromDelegate<string, string, string>(toolService.CreateLocationAsync),
            MethodToolWrapper.FromDelegate<string, string, string, string?>(toolService.LinkLocationsAsync),
            MethodToolWrapper.FromDelegate<string>(toolService.MovePlayerAsync),
            MethodToolWrapper.FromDelegate<string, string, string, string>(toolService.CreateItemAsync),
            MethodToolWrapper.FromDelegate<string, string, string, string>(toolService.CreateNpcAsync),
            MethodToolWrapper.FromDelegate<string, string, string, string, string>(toolService.AddInteractionAsync),
            MethodToolWrapper.FromDelegate<string, string>(toolService.SetVisibilityAsync),
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
- 可选：若新地点需要一个可见可操作细节，可以创建 0 到 1 个 Item 或 0 到 1 个 NPC，再调用 gm_add_interaction 给新地点、该 Item 或该 Actor 添加 0 到 2 个交互 affordance。
- 如果最终摘要提到玩家能看见的具体物品或人物，该实体必须已经通过 gm_create_item 或 gm_create_npc 落账；若可以对话、检查、拿取或操作，必须通过 gm_add_interaction 落账。
- 若有建议反向方向，应在 gm_link_locations 中填写 reverse_direction；否则传 null。
- 不要用普通文本声称世界已改变；只有工具调用成功才算落账。
- 必要工具调用完成后，停止调用工具，并输出 1 到 3 句玩家可见的中文结算摘要。
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
- 如果交互让某个对象变得可操作，必须调用 gm_add_interaction。
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
        sb.AppendLine("如果你创建 Interaction，target_ref 使用 location:<id>、item:<id> 或 actor:<id>；visible_label 是玩家看到的动作提示。");

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
        sb.AppendLine($"- LocationId: {context.CurrentLocationId}");
        sb.AppendLine($"- Name: {perception.Location.Name}");
        sb.AppendLine($"- Description: {perception.Location.Description}");
        sb.AppendLine();
        sb.AppendLine("[玩家选择的交互]");
        sb.AppendLine($"- InteractionId: {interaction.InteractionId}");
        sb.AppendLine($"- Target: {interaction.TargetKind}:{interaction.TargetId}");
        sb.AppendLine($"- ActionKind: {interaction.ActionKind}");
        sb.AppendLine($"- VisibleLabel: {interaction.VisibleLabel}");
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
        sb.AppendLine("优先根据所选 Interaction 的 target、actionKind、visibleLabel 和 effectNote 裁决，不要把玩家事前推理当成事实来源。");
        sb.AppendLine("若创建 Item/NPC/Interaction，ID 必须稳定、唯一，并且 target_ref 必须引用已经存在的对象。");
        sb.AppendLine("若只是检查、倾听、交谈第一句等低风险反馈，可以不改账本，只输出玩家可见摘要。");
        sb.AppendLine("若摘要提到新的可见物品、人物或可执行动作，必须先调用相应工具落账。");

        return sb.ToString();
    }

    private static void AppendInteractions(StringBuilder sb, IReadOnlyList<InteractionPerception> interactions) {
        if (interactions.Count == 0) { return; }

        foreach (var interaction in interactions) {
            sb.AppendLine($"- {interaction.InteractionId}: {interaction.TargetKind}:{interaction.TargetId} | {interaction.ActionKind} | {interaction.VisibleLabel}");
        }
    }

    private static string BuildToolResultsObservation(IReadOnlyList<ToolCallExecutionResult> results) {
        var sb = new StringBuilder();
        sb.AppendLine("[工具执行结果]");
        foreach (var result in results) {
            sb.AppendLine($"- {result.ToolName}#{result.ToolCallId}: {result.ExecuteResult.Status}");
            sb.AppendLine(result.ExecuteResult.Content);
        }

        sb.AppendLine();
        sb.AppendLine("如果必要工具都成功，请停止调用工具，并输出玩家可见结算摘要。若工具失败，请修正参数后继续。");
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
