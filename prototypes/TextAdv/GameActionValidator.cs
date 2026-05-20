using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;

namespace Atelia.TextAdv;

internal static class GameActionValidator {
    private const string BaseAddressEnv = "DEEPSEEK_BASE_URL";
    private const string ModelIdEnv = "ATELIA_TEXTADV_VALIDATOR_MODEL_ID";
    private const string FallbackModelIdEnv = "DEEPSEEK_MODEL";
    private const string ApiKeyEnv = "DEEPSEEK_API_KEY";
    private const string DefaultModelId = "deepseek-v4-flash";
    private const string DefaultBaseAddress = "https://api.deepseek.com/";
    private const string PointOutIssuesToolName = "point_out_issues";

    private static readonly Lock s_gate = new();
    private static readonly ImmutableArray<ToolDefinition> s_tools = [
        new ToolDefinition(
            Name: PointOutIssuesToolName,
            Description: BuildPointOutIssuesToolDescription(),
            Parameters: [
                new ToolParamSpec(
                    name: "problem_summary",
                    description: "一句话概括最主要的不合理之处。",
                    valueKind: ToolParamType.String
                ),
                new ToolParamSpec(
                    name: "evidence_boundary",
                    description: "说明它越过了哪条证据边界，或引用了哪些输入中不存在的事实。",
                    valueKind: ToolParamType.String,
                    isNullable: true,
                    defaultValue: new ParamDefault(null)
                ),
                new ToolParamSpec(
                    name: "rewrite_suggestion",
                    description: "给 Player 的简短修正建议。",
                    valueKind: ToolParamType.String,
                    isNullable: true,
                    defaultValue: new ParamDefault(null)
                )
            ]
        )
    ];

    private static DeepSeekV4ChatClient? s_client;
    private static HttpClient? s_httpClient;
    private static ValidatorConfig? s_config;

    private sealed record ValidatorConfig(string? BaseAddress, string ModelId, string ApiKey);

    internal sealed record ValidationResult(bool Accepted, string Feedback);

    internal static async Task<ValidationResult> ValidateActionAsync(
        PerceptionBundle perception,
        string actionKind,
        string actionSummary,
        string preActionReason,
        string? actionPayload,
        CancellationToken cancellationToken
    ) {
        var client = GetClient();
        var config = GetConfig();
        var request = new CompletionRequest(
            ModelId: config.ModelId,
            SystemPrompt: BuildSystemPrompt(),
            Context: new IHistoryMessage[]
            {
                new ObservationMessage(BuildObservation(perception, actionKind, actionSummary, preActionReason, actionPayload))
            },
            Tools: s_tools
        );

        CompletionResult result;
        try {
            result = await client.StreamCompletionAsync(request, null, cancellationToken);
        }
        catch (Exception ex) {
            throw new InvalidOperationException(
                $"请确认 DeepSeek validator 可访问，并检查环境变量 {ApiKeyEnv} / {BaseAddressEnv} / {ModelIdEnv}。{ex.Message}",
                ex
            );
        }

        if (result.Errors is { Count: > 0 }) { throw new InvalidOperationException(BuildProviderErrorMessage(result.Errors)); }

        if (result.Message.ToolCalls.Count == 0) {
            return new ValidationResult(
                Accepted: true,
                Feedback: BuildAcceptedFeedback()
            );
        }

        return new ValidationResult(
            Accepted: false,
            Feedback: BuildRejectedFeedback(result.Message.ToolCalls)
        );
    }

    private static DeepSeekV4ChatClient GetClient() {
        lock (s_gate) {
            if (s_client is not null) { return s_client; }

            var config = GetConfig();
            s_httpClient = new HttpClient {
                BaseAddress = ResolveBaseAddress(config.BaseAddress)
            };
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
            : new Uri(EnsureTrailingSlash(configuredBaseAddress));
    }

    private static string EnsureTrailingSlash(string value) {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";
    }

    private static ValidatorConfig GetConfig() {
        lock (s_gate) {
            if (s_config is not null) { return s_config; }

            s_config = new ValidatorConfig(
                BaseAddress: TextAdvRuntimeEnvironment.GetOptionalEnvironment(BaseAddressEnv),
                ModelId: TextAdvRuntimeEnvironment.GetEnvironmentOrDefault(
                    ModelIdEnv,
                    TextAdvRuntimeEnvironment.GetEnvironmentOrDefault(FallbackModelIdEnv, DefaultModelId)
                ),
                ApiKey: TextAdvRuntimeEnvironment.RequireEnvironment(ApiKeyEnv, "DeepSeek validator")
            );
            return s_config;
        }
    }

    private static string BuildSystemPrompt() {
        var sb = new StringBuilder();
        sb.AppendLine("你是 TextAdv 的专用动作验证器。");
        sb.AppendLine();
        sb.AppendLine("你的任务不是判断玩家是否猜中世界真相，而是判断这一步动作前提交的“事前推理（pre-action reason）”是否 grounded 于给定证据，并且是否足以支持候选动作。");
        sb.AppendLine();
        sb.AppendLine("[共享事前推理要求]");
        sb.AppendLine(PlayerActionGuideCatalog.BuildValidatorSharedReasoningSection());
        sb.AppendLine();
        sb.AppendLine("[validator 额外裁决规则]");
        sb.AppendLine("1. 只能依据输入里给出的当前可见信息、上回合结算结果、当前 Memory-Notebook 快照、以及当前回合已接受步骤。");
        sb.AppendLine("2. 上回合结算结果（[上回合结算结果]）中描述的世界事实，视为已确立的世界状态，玩家可以在事前推理中直接引用。但不能把结算结果中未出现的内容说成已发生。");
        sb.AppendLine("3. 标准是“防君子不防小人”的 groundedness 检查，不追求吹毛求疵。");
        sb.AppendLine("4. 对于 notebook 编辑，输入会额外提供当前 notebook 的块视图、TextEditScript，以及宿主侧预测出的编辑后块视图；你应重点检查 after-view 中新增、修改、删除的内容是否 grounded。");
        sb.AppendLine("5. 对于 large/explore，玩家可以探索当前尚未列为出口的方向；这表示“试探未知”，不要求该方向已经出现在 Exits 中。你只需要检查玩家是否把未知方向说成已确认事实，或是否声称已有输入中不存在的地点/线索。");
        sb.AppendLine("6. 对于 small/interact 或 large/interact，玩家必须选择当前 VisibleInteractions 中存在的 interaction，并且事前推理应依据该交互的 visibleLabel、target、turnCost、effectScope、effectSlots 和当前可见信息；不能凭空假定 interaction 执行后的隐藏结果。");
        sb.AppendLine("7. 如果某一步的先前已接受步骤仍处于 pending-turn-end / working 状态，玩家不能把那些尚未完成的结果当成已经到手或已经发生的事实继续推理。");
        sb.AppendLine();
        sb.AppendLine("请把工具调用当作填写裁决表：");
        sb.AppendLine("- 若你认为这一步动作合理，请不要调用任何工具。");
        sb.AppendLine("- 若你认为这一步动作不合理，请调用一次或多次 point_out_issues 工具，逐条指出问题。");
        sb.AppendLine("- 当你调用工具时，普通文本可以留空；不要再输出“好的我来仅用 JSON 回答”之类的多余前后缀。");
        sb.AppendLine();
        sb.Append("重要：没有工具调用，就表示验证通过。");
        return sb.ToString();
    }

    private static string BuildObservation(
        PerceptionBundle perception,
        string actionKind,
        string actionSummary,
        string preActionReason,
        string? actionPayload
    ) {
        var sb = new StringBuilder();
        sb.AppendLine("请验证下面这一步动作是否 grounded。\n");
        sb.AppendLine("[当前与玩家共享的证据视图]");
        sb.AppendLine(PerceptionEvidenceRenderer.RenderForPrompt(perception));

        sb.AppendLine();
        sb.AppendLine("[事前推理]");
        sb.AppendLine(PlayerActionGuideCatalog.BuildSharedReasoningReminder());
        sb.AppendLine(preActionReason);

        sb.AppendLine();
        sb.AppendLine("[候选动作]");
        sb.AppendLine($"- ActionKind: {actionKind}");
        sb.AppendLine($"- ActionSummary: {actionSummary}");
        if (!string.IsNullOrWhiteSpace(actionPayload)) {
            sb.AppendLine("- ActionPayload:");
            sb.AppendLine(actionPayload!);
        }

        sb.AppendLine();
        sb.AppendLine("若动作合理，请不要调用任何工具。若动作不合理，请调用 point_out_issues 工具。\n不要输出与裁决无关的寒暄。 ");
        return sb.ToString();
    }

    internal static string BuildSharedReasoningSectionForTests() => PlayerActionGuideCatalog.BuildValidatorSharedReasoningSection();

    private static string BuildPointOutIssuesToolDescription() {
        return "指出不合理之处。仅当你发现这一步动作或其事前推理违反共享事前推理要求、有明显跳步、偷用输入外记忆，或把猜测写成确定事实时调用。若动作合理，请不要调用任何工具。";
    }

    private static string BuildAcceptedFeedback() {
        return "通过：validator 未指出 groundedness 问题。";
    }

    private static string BuildRejectedFeedback(IReadOnlyList<RawToolCall> toolCalls) {
        var issues = toolCalls.Select(BuildIssueFeedback).ToArray();
        if (issues.Length == 0) { return "未通过：模型调用了问题指出工具，但未给出可读内容。"; }

        return string.Join("\n\n", issues);
    }

    private static string BuildIssueFeedback(RawToolCall toolCall) {
        string? summary = null;
        string? boundary = null;
        string? suggestion = null;

        try {
            using var document = JsonDocument.Parse(toolCall.RawArgumentsJson);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object) {
                summary = TryGetString(root, "problem_summary");
                boundary = TryGetString(root, "evidence_boundary");
                suggestion = TryGetString(root, "rewrite_suggestion");
            }
        }
        catch (JsonException) {
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(summary)) {
            parts.Add($"问题：{summary}");
        }

        if (!string.IsNullOrWhiteSpace(boundary)) {
            parts.Add($"证据边界：{boundary}");
        }

        if (!string.IsNullOrWhiteSpace(suggestion)) {
            parts.Add($"建议：{suggestion}");
        }

        if (parts.Count == 0) {
            parts.Add($"模型调用了 {toolCall.ToolName}，但参数不可读：{toolCall.RawArgumentsJson}");
        }

        return string.Join("\n", parts);
    }

    private static string BuildProviderErrorMessage(IReadOnlyList<string> errors) {
        return TextAdvRuntimeEnvironment.BuildProviderErrorMessage(
            errors,
            prefix: "DeepSeek validator 返回 provider error：",
            defaultMessage: "Unknown provider error."
        );
    }

    private static string? TryGetString(JsonElement root, string propertyName) {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String) { return null; }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
