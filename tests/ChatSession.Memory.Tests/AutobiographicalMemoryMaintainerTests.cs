using Atelia.ChatSession;
using Atelia.ChatSession.Memory;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.ChatSession.Memory.Tests;

public class AutobiographicalMemoryMaintainerTests {
    [Fact]
    public void CompressionPolicy_ValidatesWatermarksAndTriggersAtHighWatermark() {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryDocumentCompressionPolicy(10, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryDocumentCompressionPolicy(10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryDocumentCompressionPolicy(9, 10));

        var policy = new MemoryDocumentCompressionPolicy(10, 5);
        Assert.False(policy.ShouldCompress(new string('x', 29)));
        Assert.True(policy.ShouldCompress(new string('x', 30)));
    }

    [Fact]
    public async Task MaintainAsync_SkipsCompressionBelowHighWatermark() {
        int compressionCalls = 0;
        var recording = new DelegateMaintainer(
            request => SuccessResult("recorded", "recording", request.OldBlock.Text, toolCalls: 2)
        );
        var compression = new DelegateMaintainer(
            request => {
                compressionCalls++;
                return SuccessResult("should-not-run", "compression", request.OldBlock.Text, toolCalls: 3);
            }
        );
        var maintainer = new AutobiographicalMemoryMaintainer(
            new MemoryDocumentCompressionPolicy(highWatermarkTokens: 10, targetTokens: 5),
            recording,
            compression
        );

        var result = await maintainer.MaintainAsync(CreateRequest("old"), CancellationToken.None);

        Assert.Equal("recorded", result.NewBlock.Text);
        Assert.Equal(0, compressionCalls);
        Assert.Equal(2, result.ToolCallsExecuted);
        Assert.Collection(
            Assert.IsAssignableFrom<IReadOnlyList<MemoryBlockMaintenanceStageResult>>(result.Stages),
            stage => Assert.Equal(MemoryBlockMaintenanceStageStatus.Succeeded, stage.Status),
            stage => Assert.Equal(MemoryBlockMaintenanceStageStatus.Skipped, stage.Status)
        );
        Assert.Contains("compressionTriggered=false", result.Diagnostics);
    }

    [Fact]
    public async Task MaintainAsync_CompressesAtHighWatermarkAndAggregatesAudit() {
        var order = new List<string>();
        string recordedText = new('r', 30);
        var recording = new DelegateMaintainer(
            request => {
                order.Add("recording");
                return SuccessResult(
                    recordedText,
                    "recording",
                    request.OldBlock.Text,
                    toolCalls: 2,
                    errors: ["recording-warning"]
                );
            }
        );
        var compression = new DelegateMaintainer(
            request => {
                order.Add("compression");
                Assert.True(request.RecentHistory.PriorContext.IsEmpty);
                Assert.Empty(request.RecentHistory.Messages);
                Assert.Equal("epoch-1:compression", request.RecentHistory.SourceId);
                Assert.Equal(recordedText, request.OldBlock.Text);
                return SuccessResult(
                    "compressed",
                    "compression",
                    request.OldBlock.Text,
                    toolCalls: 3,
                    targetTokens: 5,
                    targetReached: true,
                    errors: ["compression-warning"]
                );
            }
        );
        var maintainer = new AutobiographicalMemoryMaintainer(
            new MemoryDocumentCompressionPolicy(highWatermarkTokens: 10, targetTokens: 5),
            recording,
            compression
        );

        var result = await maintainer.MaintainAsync(CreateRequest("old", "epoch-1"), CancellationToken.None);

        Assert.Equal(["recording", "compression"], order);
        Assert.Equal("compressed", result.NewBlock.Text);
        Assert.Equal(5, result.ToolCallsExecuted);
        Assert.Equal(["recording-warning", "compression-warning"], result.Errors);
        Assert.Collection(
            Assert.IsAssignableFrom<IReadOnlyList<MemoryBlockMaintenanceStageResult>>(result.Stages),
            stage => {
                Assert.Equal("recording", stage.Stage);
                Assert.Equal(MemoryBlockMaintenanceStageStatus.Succeeded, stage.Status);
                Assert.Equal(2, stage.ToolCallsExecuted);
            },
            stage => {
                Assert.Equal("compression", stage.Stage);
                Assert.Equal(MemoryBlockMaintenanceStageStatus.Succeeded, stage.Status);
                Assert.Equal(3, stage.ToolCallsExecuted);
                Assert.True(stage.TargetReached);
            }
        );
        Assert.Contains("compressionSucceeded=true", result.Diagnostics);
    }

    [Fact]
    public async Task MaintainAsync_PropagatesRecordingFailureWithoutCallingCompression() {
        int compressionCalls = 0;
        var recording = new DelegateMaintainer(
            _ => throw new InvalidOperationException("recording failed")
        );
        var compression = new DelegateMaintainer(
            request => {
                compressionCalls++;
                return SuccessResult("compressed", "compression", request.OldBlock.Text, toolCalls: 1);
            }
        );
        var maintainer = new AutobiographicalMemoryMaintainer(
            new MemoryDocumentCompressionPolicy(highWatermarkTokens: 10, targetTokens: 5),
            recording,
            compression
        );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => maintainer.MaintainAsync(CreateRequest("old"), CancellationToken.None).AsTask()
        );

        Assert.Equal("recording failed", exception.Message);
        Assert.Equal(0, compressionCalls);
    }

    [Fact]
    public async Task MaintainAsync_PreservesRecordedBlockWhenCompressionFailsAfterToolCall() {
        string recordedText = new('r', 60);
        var recording = new DelegateMaintainer(
            request => SuccessResult(recordedText, "recording", request.OldBlock.Text, toolCalls: 2)
        );
        var completionClient = new ScriptedCompletionClient();
        completionClient.Enqueue(
            request => ToolCallResult(
                request,
                new RawToolCall(
                    MemoryDocumentTools.ReplaceToolName,
                    "replace-before-failure",
                    """{"anchor":"head","content":"shorter"}"""
                )
            )
        );
        completionClient.Enqueue(request => TextResult(request, "forgot to finish"));
        completionClient.Enqueue(request => TextResult(request, "still no finish"));
        var compression = new AutobiographicalCompressionMemoryMaintainer(
            completionClient,
            "model-a",
            targetTokenCount: 5
        );
        var maintainer = new AutobiographicalMemoryMaintainer(
            new MemoryDocumentCompressionPolicy(highWatermarkTokens: 10, targetTokens: 5),
            recording,
            compression
        );

        var result = await maintainer.MaintainAsync(CreateRequest("old"), CancellationToken.None);

        Assert.Equal(recordedText, result.NewBlock.Text);
        Assert.Equal(3, result.ToolCallsExecuted);
        Assert.Contains(
            result.Notices,
            static notice => notice.Code == "compression-failed-recording-preserved"
        );
        Assert.Collection(
            Assert.IsAssignableFrom<IReadOnlyList<MemoryBlockMaintenanceStageResult>>(result.Stages),
            stage => Assert.Equal(MemoryBlockMaintenanceStageStatus.Succeeded, stage.Status),
            stage => {
                Assert.Equal(MemoryBlockMaintenanceStageStatus.Failed, stage.Status);
                Assert.Equal(1, stage.ToolCallsExecuted);
                Assert.NotNull(stage.AfterTokens);
                Assert.True(stage.AfterTokens < stage.BeforeTokens);
                Assert.Contains(MemoryDocumentTools.CompressionFinishToolName, stage.FailureMessage);
            }
        );
        Assert.Contains("compressionSucceeded=false", result.Diagnostics);
        Assert.Contains("fallback=recorded-block", result.Diagnostics);
    }

    private static MemoryBlockMaintenanceRequest CreateRequest(
        string oldBlock,
        string? sourceId = null
    ) => new(
        new RecentHistorySlice(
            ContextHeaderSnapshot.Empty,
            [new ObservationMessage("recent experience")],
            SourceId: sourceId
        ),
        RolePlayMemoryBlockPaths.FirstPersonAutobiography,
        new MemoryPackBlock(oldBlock)
    );

    private static MemoryBlockMaintenanceResult SuccessResult(
        string newText,
        string stage,
        string oldText,
        int toolCalls,
        int? targetTokens = null,
        bool? targetReached = null,
        IReadOnlyList<string>? errors = null
    ) {
        int beforeTokens = MemoryDocumentTokenEstimator.Estimate(oldText);
        int afterTokens = MemoryDocumentTokenEstimator.Estimate(newText);
        var invocation = new CompletionDescriptor(stage, "scripted", "model-a");
        return new MemoryBlockMaintenanceResult(
            MaintainerId: stage,
            Target: RolePlayMemoryBlockPaths.FirstPersonAutobiography,
            NewBlock: new MemoryPackBlock(newText),
            Notices: Array.Empty<MemoryMaintenanceNotice>(),
            Diagnostics: [$"stage={stage}"],
            Invocation: invocation,
            Errors: errors,
            ToolCallsExecuted: toolCalls,
            Stages: [
                new MemoryBlockMaintenanceStageResult(
                    Stage: stage,
                    Status: MemoryBlockMaintenanceStageStatus.Succeeded,
                    BeforeTokens: beforeTokens,
                    AfterTokens: afterTokens,
                    TargetTokens: targetTokens,
                    TargetReached: targetReached,
                    Invocation: invocation,
                    Errors: errors,
                    ToolCallsExecuted: toolCalls
                )
            ]
        );
    }

    private static CompletionResult ToolCallResult(
        CompletionRequest request,
        RawToolCall toolCall
    ) => new(
        new ActionMessage([new ActionBlock.ToolCall(toolCall)]),
        new CompletionDescriptor("scripted", "openai-chat-v1", request.ModelId)
    );

    private static CompletionResult TextResult(CompletionRequest request, string text)
        => new(
            new ActionMessage([new ActionBlock.Text(text)]),
            new CompletionDescriptor("scripted", "openai-chat-v1", request.ModelId)
        );

    private sealed class DelegateMaintainer(
        Func<MemoryBlockMaintenanceRequest, MemoryBlockMaintenanceResult> maintain
    ) : IMemoryBlockMaintainer {
        public string Id => "delegate";
        public MemoryPackBlockPath Target => RolePlayMemoryBlockPaths.FirstPersonAutobiography;

        public ValueTask<MemoryBlockMaintenanceResult> MaintainAsync(
            MemoryBlockMaintenanceRequest request,
            CancellationToken ct
        ) {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(maintain(request));
        }
    }

    private sealed class ScriptedCompletionClient : ICompletionClient {
        private readonly Queue<Func<CompletionRequest, CompletionResult>> _responses = new();

        public string Name => "scripted";
        public string ApiSpecId => "openai-chat-v1";

        public void Enqueue(Func<CompletionRequest, CompletionResult> response)
            => _responses.Enqueue(response);

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            if (_responses.Count == 0) { throw new InvalidOperationException("No scripted response remaining."); }
            return Task.FromResult(_responses.Dequeue()(request));
        }
    }
}
