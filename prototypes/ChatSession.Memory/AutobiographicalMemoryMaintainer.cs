using Atelia.ChatSession;
using Atelia.Completion.Abstractions;

namespace Atelia.ChatSession.Memory;

public sealed class AutobiographicalMemoryMaintainer : IMemoryBlockMaintainer {
    public const string DefaultId = "roleplay.first-person-autobiography.two-stage";

    private readonly MemoryDocumentCompressionPolicy _compressionPolicy;
    private readonly IMemoryBlockMaintainer _recordingMaintainer;
    private readonly IMemoryBlockMaintainer _compressionMaintainer;

    public AutobiographicalMemoryMaintainer(
        ICompletionClient completionClient,
        string modelId,
        MemoryDocumentCompressionPolicy compressionPolicy,
        string? recordingSystemPrompt = null,
        string? recordingUserPrompt = null,
        string? compressionSystemPrompt = null,
        string? compressionUserPrompt = null
    ) : this(
        compressionPolicy,
        new AutobiographicalRecordingMemoryMaintainer(
            completionClient,
            modelId,
            recordingSystemPrompt,
            recordingUserPrompt
        ),
        new AutobiographicalCompressionMemoryMaintainer(
            completionClient,
            modelId,
            compressionPolicy?.TargetTokens
                ?? throw new ArgumentNullException(nameof(compressionPolicy)),
            compressionSystemPrompt,
            compressionUserPrompt
        )
    ) { }

    public AutobiographicalMemoryMaintainer(
        MemoryDocumentCompressionPolicy compressionPolicy,
        IMemoryBlockMaintainer recordingMaintainer,
        IMemoryBlockMaintainer compressionMaintainer
    ) {
        _compressionPolicy = compressionPolicy ?? throw new ArgumentNullException(nameof(compressionPolicy));
        _recordingMaintainer = ValidateStageMaintainer(recordingMaintainer, nameof(recordingMaintainer));
        _compressionMaintainer = ValidateStageMaintainer(compressionMaintainer, nameof(compressionMaintainer));
    }

    public string Id => DefaultId;

    public MemoryPackBlockPath Target => RolePlayMemoryBlockPaths.FirstPersonAutobiography;

    public async ValueTask<MemoryBlockMaintenanceResult> MaintainAsync(
        MemoryBlockMaintenanceRequest request,
        CancellationToken ct
    ) {
        ArgumentNullException.ThrowIfNull(request);
        if (!Equals(Target, request.Target)) { throw new ArgumentException("Maintenance request target does not match autobiographical memory target.", nameof(request)); }

        var recordingResult = await _recordingMaintainer.MaintainAsync(request, ct).ConfigureAwait(false);
        ValidateStageResult(recordingResult, "recording");

        string recordedText = recordingResult.NewBlock.Text;
        int recordedTokens = MemoryDocumentTokenEstimator.Estimate(recordedText);
        var recordingStage = GetOrCreateSucceededStage(
            recordingResult,
            "recording",
            MemoryDocumentTokenEstimator.Estimate(request.OldBlock.Text),
            recordedTokens,
            targetTokens: null,
            targetReached: null
        );

        if (!_compressionPolicy.ShouldCompress(recordedText)) {
            var skippedStage = new MemoryBlockMaintenanceStageResult(
                Stage: "compression",
                Status: MemoryBlockMaintenanceStageStatus.Skipped,
                BeforeTokens: recordedTokens,
                AfterTokens: recordedTokens,
                TargetTokens: _compressionPolicy.TargetTokens,
                TargetReached: null,
                Invocation: null,
                Errors: null,
                ToolCallsExecuted: 0
            );
            return BuildResult(
                recordingResult.NewBlock,
                recordingResult,
                compressionResult: null,
                stages: [recordingStage, skippedStage],
                pipelineNotice: new MemoryMaintenanceNotice(
                    "compression-skipped-below-high-watermark",
                    $"Autobiography remained at {recordedTokens} estimated tokens, below high watermark {_compressionPolicy.HighWatermarkTokens}."
                ),
                pipelineDiagnostics: [
                    "pipeline=autobiographical-two-stage",
                    "compressionTriggered=false",
                    $"recordedTokens={recordedTokens}",
                    $"compressionHighWatermarkTokens={_compressionPolicy.HighWatermarkTokens}",
                    $"compressionTargetTokens={_compressionPolicy.TargetTokens}"
                ]
            );
        }

        var compressionRequest = new MemoryBlockMaintenanceRequest(
            new RecentHistorySlice(
                ContextHeaderSnapshot.Empty,
                Array.Empty<IHistoryMessage>(),
                SourceId: BuildCompressionSourceId(request.RecentHistory.SourceId),
                EstimatedTokens: 0
            ),
            Target,
            recordingResult.NewBlock
        );

        try {
            var compressionResult = await _compressionMaintainer.MaintainAsync(compressionRequest, ct)
                .ConfigureAwait(false);
            ValidateStageResult(compressionResult, "compression");
            int compressedTokens = MemoryDocumentTokenEstimator.Estimate(compressionResult.NewBlock.Text);
            var compressionStage = GetOrCreateSucceededStage(
                compressionResult,
                "compression",
                recordedTokens,
                compressedTokens,
                _compressionPolicy.TargetTokens,
                compressedTokens <= _compressionPolicy.TargetTokens
            );
            return BuildResult(
                compressionResult.NewBlock,
                recordingResult,
                compressionResult,
                [recordingStage, compressionStage],
                new MemoryMaintenanceNotice(
                    "compression-completed",
                    $"Autobiography compression completed at {compressedTokens} estimated tokens."
                ),
                [
                    "pipeline=autobiographical-two-stage",
                    "compressionTriggered=true",
                    "compressionSucceeded=true",
                    $"recordedTokens={recordedTokens}",
                    $"finalTokens={compressedTokens}",
                    $"compressionHighWatermarkTokens={_compressionPolicy.HighWatermarkTokens}",
                    $"compressionTargetTokens={_compressionPolicy.TargetTokens}"
                ]
            );
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        }
        catch (Exception ex) when (IsRecoverableCompressionFailure(ex)) {
            var loopFailure = ex as MemoryDocumentAgentLoopException;
            var failedStage = new MemoryBlockMaintenanceStageResult(
                Stage: loopFailure?.Stage ?? "compression",
                Status: MemoryBlockMaintenanceStageStatus.Failed,
                BeforeTokens: loopFailure?.BeforeTokens ?? recordedTokens,
                AfterTokens: loopFailure?.WorkingTokens,
                TargetTokens: loopFailure?.TargetTokens ?? _compressionPolicy.TargetTokens,
                TargetReached: null,
                Invocation: loopFailure?.Invocation,
                Errors: loopFailure?.Errors,
                ToolCallsExecuted: loopFailure?.ToolCallsExecuted ?? 0,
                FailureType: ex.GetType().FullName,
                FailureMessage: ex.Message
            );
            return BuildResult(
                recordingResult.NewBlock,
                recordingResult,
                compressionResult: null,
                stages: [recordingStage, failedStage],
                pipelineNotice: new MemoryMaintenanceNotice(
                    "compression-failed-recording-preserved",
                    $"Compression failed after recording; the recorded autobiography was preserved. {ex.GetType().Name}: {ex.Message}"
                ),
                pipelineDiagnostics: [
                    "pipeline=autobiographical-two-stage",
                    "compressionTriggered=true",
                    "compressionSucceeded=false",
                    "fallback=recorded-block",
                    $"recordedTokens={recordedTokens}",
                    $"compressionHighWatermarkTokens={_compressionPolicy.HighWatermarkTokens}",
                    $"compressionTargetTokens={_compressionPolicy.TargetTokens}"
                ],
                failedCompressionErrors: loopFailure?.Errors,
                failedCompressionToolCalls: loopFailure?.ToolCallsExecuted ?? 0
            );
        }
    }

    private MemoryBlockMaintenanceResult BuildResult(
        MemoryPackBlock newBlock,
        MemoryBlockMaintenanceResult recordingResult,
        MemoryBlockMaintenanceResult? compressionResult,
        IReadOnlyList<MemoryBlockMaintenanceStageResult> stages,
        MemoryMaintenanceNotice pipelineNotice,
        IReadOnlyList<string> pipelineDiagnostics,
        IReadOnlyList<string>? failedCompressionErrors = null,
        int failedCompressionToolCalls = 0
    ) {
        var notices = recordingResult.Notices
            .Concat(compressionResult?.Notices ?? Array.Empty<MemoryMaintenanceNotice>())
            .Append(pipelineNotice)
            .ToArray();
        var diagnostics = pipelineDiagnostics
            .Concat(recordingResult.Diagnostics.Select(static item => "recording." + item))
            .Concat((compressionResult?.Diagnostics ?? Array.Empty<string>()).Select(static item => "compression." + item))
            .ToArray();
        var errors = CombineErrors(recordingResult.Errors, compressionResult?.Errors, failedCompressionErrors);
        int toolCalls = recordingResult.ToolCallsExecuted
            + (compressionResult?.ToolCallsExecuted ?? failedCompressionToolCalls);

        return new MemoryBlockMaintenanceResult(
            MaintainerId: Id,
            Target: Target,
            NewBlock: newBlock,
            Notices: notices,
            Diagnostics: diagnostics,
            Invocation: compressionResult?.Invocation ?? recordingResult.Invocation,
            Errors: errors,
            ToolCallsExecuted: toolCalls,
            Stages: stages
        );
    }

    private static IMemoryBlockMaintainer ValidateStageMaintainer(
        IMemoryBlockMaintainer maintainer,
        string parameterName
    ) {
        ArgumentNullException.ThrowIfNull(maintainer, parameterName);
        if (!Equals(maintainer.Target, RolePlayMemoryBlockPaths.FirstPersonAutobiography)) { throw new ArgumentException("Autobiographical stage maintainer target is invalid.", parameterName); }

        return maintainer;
    }

    private void ValidateStageResult(MemoryBlockMaintenanceResult result, string stage) {
        ArgumentNullException.ThrowIfNull(result);
        if (!Equals(result.Target, Target)) { throw new InvalidOperationException($"{stage} maintainer returned the wrong target."); }
    }

    private static MemoryBlockMaintenanceStageResult GetOrCreateSucceededStage(
        MemoryBlockMaintenanceResult result,
        string stage,
        int beforeTokens,
        int afterTokens,
        int? targetTokens,
        bool? targetReached
    ) {
        var existing = result.Stages?.SingleOrDefault(item => string.Equals(item.Stage, stage, StringComparison.Ordinal));
        return existing ?? new MemoryBlockMaintenanceStageResult(
            Stage: stage,
            Status: MemoryBlockMaintenanceStageStatus.Succeeded,
            BeforeTokens: beforeTokens,
            AfterTokens: afterTokens,
            TargetTokens: targetTokens,
            TargetReached: targetReached,
            Invocation: result.Invocation,
            Errors: result.Errors,
            ToolCallsExecuted: result.ToolCallsExecuted
        );
    }

    private static IReadOnlyList<string>? CombineErrors(params IReadOnlyList<string>?[] sources) {
        var combined = sources
            .Where(static source => source is not null)
            .SelectMany(static source => source!)
            .ToArray();
        return combined.Length == 0 ? null : combined;
    }

    private static bool IsRecoverableCompressionFailure(Exception exception)
        => exception is InvalidOperationException
            or ChatSessionTurnAbortedException
            or HttpRequestException
            or TaskCanceledException;

    private static string BuildCompressionSourceId(string? sourceId)
        => string.IsNullOrWhiteSpace(sourceId) ? "compression" : sourceId + ":compression";
}
