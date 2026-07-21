using System.Globalization;
using Atelia.ChatSession;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

namespace Atelia.ChatSession.Memory;

public sealed class AutobiographicalCompressionMemoryMaintainer : IMemoryBlockMaintainer {
    public const string DefaultId = "roleplay.first-person-autobiography.compression";

    private const int MaxCompressionAgentIterations = 128;
    private const string AgentToolInstructions = """

        ## Editing Protocol

        The current autobiography is shown in the final instruction as blocks marked `[block:N]` and `[/block]`.
        These markers are editing metadata and are not part of the memoir.

        Modify the document only through the `memory_document_*` tools. Do not output replacement memoir text in an assistant message.
        Block IDs are stable for this compression invocation. Tool results report recoverable edit failures and the current estimated document token count.
        The final block is mechanically protected: do not replace it, delete it, or insert anything after it.
        Prefer `memory_document_replace_range` when several adjacent blocks can be condensed into one passage.
        You may issue multiple independent tool calls in one turn. Use the token count in tool results to decide when to finish.

        When compression is complete, call `memory_document_finish_compression` as the only tool call in that turn:
        - status `changed` after one or more successful edits;
        - status `no-change` only when preserving protected content prevents any safe reduction.

        After one or more successful edits, ending the turn without another tool call is also accepted as `changed`.
        A `no-change` result still requires the explicit finish tool. The host rejects a result larger than the original document.
        """;

    private readonly ICompletionClient _completionClient;
    private readonly string _modelId;
    private readonly int _targetTokenCount;
    private readonly string _systemPrompt;
    private readonly string? _userPromptOverride;

    public AutobiographicalCompressionMemoryMaintainer(
        ICompletionClient completionClient,
        string modelId,
        int targetTokenCount,
        string? systemPrompt = null,
        string? userPrompt = null
    ) {
        _completionClient = completionClient ?? throw new ArgumentNullException(nameof(completionClient));
        _modelId = string.IsNullOrWhiteSpace(modelId)
            ? throw new ArgumentException("Model id cannot be empty.", nameof(modelId))
            : modelId;
        _targetTokenCount = targetTokenCount > 0
            ? targetTokenCount
            : throw new ArgumentOutOfRangeException(nameof(targetTokenCount), "Target token count must be positive.");
        _systemPrompt = string.Concat(
            systemPrompt ?? AutobiographicalCompressionPrompts.SystemPrompt,
            AgentToolInstructions
        );
        _userPromptOverride = userPrompt;
    }

    public string Id => DefaultId;

    public MemoryPackBlockPath Target => RolePlayMemoryBlockPaths.FirstPersonAutobiography;

    public async ValueTask<MemoryBlockMaintenanceResult> MaintainAsync(
        MemoryBlockMaintenanceRequest request,
        CancellationToken ct
    ) {
        ArgumentNullException.ThrowIfNull(request);
        if (!Equals(Target, request.Target)) { throw new ArgumentException("Maintenance request target does not match autobiographical compression target.", nameof(request)); }
        if (string.IsNullOrWhiteSpace(request.OldBlock.Text)) { throw new ArgumentException("Autobiography cannot be empty for compression.", nameof(request)); }

        int beforeTokens = MemoryDocumentTokenEstimator.Estimate(request.OldBlock.Text);
        var editingSession = new MemoryDocumentEditingSession(
            request.OldBlock.Text,
            new MemoryDocumentEditingOptions(ProtectFinalBlock: true)
        );
        var finishProfile = new MemoryDocumentFinishToolProfile(
            MemoryDocumentTools.CompressionFinishToolName,
            ValidateNonExpandingResult
        );
        string userPrompt = _userPromptOverride
            ?? AutobiographicalCompressionPrompts.FormatUserPrompt(beforeTokens, _targetTokenCount);

        var loopResult = await MemoryDocumentAgentLoop.RunAsync(
            new MemoryDocumentAgentLoopRequest(
                Id,
                "compression",
                _completionClient,
                _modelId,
                _systemPrompt,
                "Final Compression Instruction",
                userPrompt,
                "Current Autobiography Compression View",
                request,
                editingSession,
                finishProfile,
                TargetTokens: _targetTokenCount,
                MissingFinishRetryCount: 1,
                MaxIterations: MaxCompressionAgentIterations
            ),
            ct
        ).ConfigureAwait(false);

        string newText = editingSession.RenderDocumentText();
        int afterTokens = MemoryDocumentTokenEstimator.Estimate(newText);
        double actualCompressionPercent = beforeTokens == 0
            ? 0
            : (beforeTokens - afterTokens) * 100d / beforeTokens;
        bool targetReached = afterTokens <= _targetTokenCount;

        return new MemoryBlockMaintenanceResult(
            MaintainerId: Id,
            Target: Target,
            NewBlock: new MemoryPackBlock(newText),
            Notices: [
                new MemoryMaintenanceNotice(
                    targetReached ? "compression-target-reached" : "compression-target-not-reached",
                    targetReached
                        ? $"Autobiography compressed to {afterTokens} estimated tokens."
                        : $"Autobiography remains at {afterTokens} estimated tokens; protected content took priority over target {_targetTokenCount}."
                )
            ],
            Diagnostics: [
                "stage=compression",
                $"completionStatus={FormatCompletionStatus(editingSession.CompletionStatus)}",
                $"beforeTokens={beforeTokens}",
                $"afterTokens={afterTokens}",
                $"targetTokens={_targetTokenCount}",
                $"actualCompressionPercent={actualCompressionPercent.ToString("F2", CultureInfo.InvariantCulture)}",
                $"targetReached={targetReached.ToString().ToLowerInvariant()}",
                $"editCount={editingSession.EditCount}",
                $"blockCount={editingSession.WorkingDocument.Blocks.Count}"
            ],
            Invocation: loopResult.Invocation,
            Errors: loopResult.Errors,
            ToolCallsExecuted: loopResult.ToolCallsExecuted,
            Stages: [
                new MemoryBlockMaintenanceStageResult(
                    Stage: "compression",
                    Status: MemoryBlockMaintenanceStageStatus.Succeeded,
                    BeforeTokens: beforeTokens,
                    AfterTokens: afterTokens,
                    TargetTokens: _targetTokenCount,
                    TargetReached: targetReached,
                    Invocation: loopResult.Invocation,
                    Errors: loopResult.Errors,
                    ToolCallsExecuted: loopResult.ToolCallsExecuted
                )
            ]
        );

        ValidateResult ValidateNonExpandingResult(
            MemoryDocumentEditingSession session,
            FinishMemoryDocumentArtifact _
        ) {
            string candidateText = session.RenderDocumentText();
            int candidateTokens = MemoryDocumentTokenEstimator.Estimate(candidateText);
            return candidateText.Length <= session.BaseText.Length && candidateTokens <= beforeTokens
                ? new ValidateResult(true, null)
                : new ValidateResult(
                    false,
                    $"Compression result expanded from {session.BaseText.Length} to {candidateText.Length} characters "
                    + $"and from {beforeTokens} to {candidateTokens} estimated tokens. Reduce the document before finishing."
                );
        }
    }

    private static string FormatCompletionStatus(MemoryDocumentCompletionStatus? status)
        => status switch {
            MemoryDocumentCompletionStatus.Changed => "changed",
            MemoryDocumentCompletionStatus.NoChange => "no-change",
            _ => "unfinished"
        };
}
