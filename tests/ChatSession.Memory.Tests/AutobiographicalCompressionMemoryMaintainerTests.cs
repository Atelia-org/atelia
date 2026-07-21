using Atelia.ChatSession;
using Atelia.ChatSession.Memory;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.ChatSession.Memory.Tests;

public class AutobiographicalCompressionMemoryMaintainerTests {
    [Fact]
    public async Task MaintainAsync_UsesEditedDocumentAndPreservesFinalPassage() {
        const string finalPassage = "我仍在这里，等他回来。";
        var completionClient = new ScriptedCompletionClient();
        completionClient.Enqueue(
            request => {
                Assert.Contains(MemoryDocumentTools.CompressionFinishToolName, request.Tools.Select(static tool => tool.Name));
                Assert.Contains("approximately 8 tokens", Assert.IsType<ObservationMessage>(request.Context[^1]).Content);
                return ToolCallResult(
                    request,
                    new RawToolCall(
                        MemoryDocumentTools.ReplaceToolName,
                        "replace-1",
                        """{"anchor":"1","content":"最初，我在海边认出了自己的选择。"}"""
                    ),
                    ignoredText: "THIS ASSISTANT TEXT MUST NOT BECOME MEMORY"
                );
            }
        );
        completionClient.Enqueue(
            request => ToolCallResult(
                request,
                new RawToolCall(
                    MemoryDocumentTools.CompressionFinishToolName,
                    "finish-1",
                    """{"status":"changed"}"""
                ),
                ignoredText: "ANOTHER IGNORED TEXT"
            )
        );

        var maintainer = new AutobiographicalCompressionMemoryMaintainer(completionClient, "model-a", targetTokenCount: 8);
        var result = await maintainer.MaintainAsync(
            CreateRequest($"最初，我在海边经历了很多很多难以忘记的细节。\n\n{finalPassage}"),
            CancellationToken.None
        );

        Assert.Equal($"最初，我在海边认出了自己的选择。\n\n{finalPassage}", result.NewBlock.Text);
        Assert.DoesNotContain("IGNORED", result.NewBlock.Text);
        Assert.EndsWith(finalPassage, result.NewBlock.Text);
        Assert.Contains("stage=compression", result.Diagnostics);
        Assert.Contains("targetTokens=8", result.Diagnostics);
        Assert.Equal(2, result.ToolCallsExecuted);
    }

    [Fact]
    public async Task MaintainAsync_AutoFinishStillValidatesCompressionResult() {
        var completionClient = new ScriptedCompletionClient();
        completionClient.Enqueue(
            request => ToolCallResult(
                request,
                new RawToolCall(
                    MemoryDocumentTools.ReplaceToolName,
                    "replace-with-expansion",
                    """{"anchor":"head","content":"这一段被扩写成了明显比原文更长更长的内容。"}"""
                )
            )
        );
        completionClient.Enqueue(
            request => new CompletionResult(
                new ActionMessage([new ActionBlock.Text("Compression is complete.")]),
                new CompletionDescriptor("scripted", "openai-chat-v1", request.ModelId)
            )
        );
        completionClient.Enqueue(
            request => {
                var validation = Assert.IsType<ObservationMessage>(request.Context[^1]);
                Assert.Contains("cannot finish yet", validation.Content);
                Assert.Contains("expanded", validation.Content);
                return ToolCallResult(
                    request,
                    new RawToolCall(
                        MemoryDocumentTools.ReplaceToolName,
                        "replace-after-auto-validation",
                        """{"anchor":"head","content":"旧事。"}"""
                    )
                );
            }
        );
        completionClient.Enqueue(
            request => new CompletionResult(
                new ActionMessage([new ActionBlock.Text("Compression is now complete.")]),
                new CompletionDescriptor("scripted", "openai-chat-v1", request.ModelId)
            )
        );

        const string oldText = "一段旧事。\n\n现在仍鲜活。";
        var maintainer = new AutobiographicalCompressionMemoryMaintainer(completionClient, "model-a", targetTokenCount: 3);
        var result = await maintainer.MaintainAsync(CreateRequest(oldText), CancellationToken.None);

        Assert.Equal("旧事。\n\n现在仍鲜活。", result.NewBlock.Text);
        Assert.Equal(2, result.ToolCallsExecuted);
        Assert.Contains("completionStatus=changed", result.Diagnostics);
    }

    [Fact]
    public async Task MaintainAsync_RejectsEditingFinalPassageAndAllowsRecovery() {
        const string finalPassage = "此刻，我仍在等待。";
        var completionClient = new ScriptedCompletionClient();
        completionClient.Enqueue(
            request => ToolCallResult(
                request,
                new RawToolCall(
                    MemoryDocumentTools.ReplaceToolName,
                    "replace-tail",
                    """{"anchor":"tail","content":"篡改后的现在。"}"""
                )
            )
        );
        completionClient.Enqueue(
            request => {
                var failedResult = Assert.Single(Assert.IsType<ToolResultsMessage>(request.Context[^1]).Results);
                Assert.Equal(ToolExecutionStatus.Failed, failedResult.Status);
                Assert.Contains("protected final block", failedResult.GetFlattenedText());
                Assert.Contains("Current estimated document tokens:", failedResult.GetFlattenedText());
                return ToolCallResult(
                    request,
                    new RawToolCall(
                        MemoryDocumentTools.ReplaceToolName,
                        "replace-head",
                        """{"anchor":"head","content":"那次相遇改变了我。"}"""
                    )
                );
            }
        );
        completionClient.Enqueue(
            request => ToolCallResult(
                request,
                new RawToolCall(
                    MemoryDocumentTools.CompressionFinishToolName,
                    "finish-protected",
                    """{"status":"changed"}"""
                )
            )
        );

        var maintainer = new AutobiographicalCompressionMemoryMaintainer(completionClient, "model-a", targetTokenCount: 5);
        var result = await maintainer.MaintainAsync(
            CreateRequest($"那次相遇有许多许多细节，它们逐渐沉淀。\n\n{finalPassage}"),
            CancellationToken.None
        );

        Assert.EndsWith(finalPassage, result.NewBlock.Text);
        Assert.DoesNotContain("篡改后的现在", result.NewBlock.Text);
        Assert.Equal(3, result.ToolCallsExecuted);
    }

    [Fact]
    public void EditingSession_ReplaceRangeIsAtomicAndProtectsFinalPassage() {
        var session = new MemoryDocumentEditingSession(
            "第一段很长。\n\n第二段也很长。\n\n第三段继续重复。\n\n此刻仍鲜活。",
            new MemoryDocumentEditingOptions(ProtectFinalBlock: true)
        );

        var replaced = session.ReplaceRange("1", "3", "旧事沉淀为一个意象。");
        var rejected = session.ReplaceRange("head", "tail", "不应覆盖现在。");

        Assert.True(replaced.IsSuccess);
        Assert.False(rejected.IsSuccess);
        Assert.Equal("旧事沉淀为一个意象。\n\n此刻仍鲜活。", session.RenderDocumentText());
        Assert.Equal(1, session.EditCount);
    }

    [Fact]
    public async Task MaintainAsync_AllowsSuccessWhenTargetIsNotReached() {
        var completionClient = new ScriptedCompletionClient();
        completionClient.Enqueue(
            request => ToolCallResult(
                request,
                new RawToolCall(
                    MemoryDocumentTools.ReplaceToolName,
                    "replace-small-reduction",
                    """{"anchor":"head","content":"一段仍然较长但已经稍微压缩的旧记忆。"}"""
                )
            )
        );
        completionClient.Enqueue(
            request => ToolCallResult(
                request,
                new RawToolCall(
                    MemoryDocumentTools.CompressionFinishToolName,
                    "finish-above-target",
                    """{"status":"changed"}"""
                )
            )
        );

        var maintainer = new AutobiographicalCompressionMemoryMaintainer(completionClient, "model-a", targetTokenCount: 1);
        var result = await maintainer.MaintainAsync(
            CreateRequest("一段非常非常长而且包含许多已经稳定下来的旧记忆。\n\n此刻，我仍有未完成的问题。"),
            CancellationToken.None
        );

        Assert.Contains(result.Notices, static notice => notice.Code == "compression-target-not-reached");
        Assert.Contains("targetReached=false", result.Diagnostics);
    }

    [Fact]
    public async Task MaintainAsync_RejectsExpansionAtFinishAndAllowsRecovery() {
        var completionClient = new ScriptedCompletionClient();
        completionClient.Enqueue(
            request => ToolCallResult(
                request,
                new RawToolCall(
                    MemoryDocumentTools.ReplaceToolName,
                    "replace-longer",
                    """{"anchor":"head","content":"这一段被错误地扩写成了比原文长很多很多很多很多很多的内容。"}"""
                )
            )
        );
        completionClient.Enqueue(
            request => ToolCallResult(
                request,
                new RawToolCall(
                    MemoryDocumentTools.CompressionFinishToolName,
                    "finish-expanded",
                    """{"status":"changed"}"""
                )
            )
        );
        completionClient.Enqueue(
            request => {
                var failedFinish = Assert.Single(Assert.IsType<ToolResultsMessage>(request.Context[^1]).Results);
                Assert.Equal(ToolExecutionStatus.Failed, failedFinish.Status);
                Assert.Contains("expanded", failedFinish.GetFlattenedText());
                return ToolCallResult(
                    request,
                    new RawToolCall(
                        MemoryDocumentTools.ReplaceToolName,
                        "replace-shorter",
                        """{"anchor":"head","content":"旧事沉淀了。"}"""
                    )
                );
            }
        );
        completionClient.Enqueue(
            request => ToolCallResult(
                request,
                new RawToolCall(
                    MemoryDocumentTools.CompressionFinishToolName,
                    "finish-recovered",
                    """{"status":"changed"}"""
                )
            )
        );

        const string oldText = "一段需要压缩的旧事。\n\n现在仍然鲜活。";
        var maintainer = new AutobiographicalCompressionMemoryMaintainer(completionClient, "model-a", targetTokenCount: 3);
        var result = await maintainer.MaintainAsync(CreateRequest(oldText), CancellationToken.None);

        Assert.True(MemoryDocumentTokenEstimator.Estimate(result.NewBlock.Text) <= MemoryDocumentTokenEstimator.Estimate(oldText));
        Assert.Equal(4, result.ToolCallsExecuted);
    }

    [Fact]
    public async Task MaintainAsync_RejectsCharacterExpansionWithinSameTokenEstimate() {
        var completionClient = new ScriptedCompletionClient();
        completionClient.Enqueue(
            request => ToolCallResult(
                request,
                new RawToolCall(
                    MemoryDocumentTools.ReplaceToolName,
                    "replace-same-token-bucket",
                    """{"anchor":"head","content":"甲乙丙丁戊"}"""
                )
            )
        );
        completionClient.Enqueue(
            request => ToolCallResult(
                request,
                new RawToolCall(
                    MemoryDocumentTools.CompressionFinishToolName,
                    "finish-same-token-bucket",
                    """{"status":"changed"}"""
                )
            )
        );
        completionClient.Enqueue(
            request => {
                var failedFinish = Assert.Single(Assert.IsType<ToolResultsMessage>(request.Context[^1]).Results);
                Assert.Equal(ToolExecutionStatus.Failed, failedFinish.Status);
                Assert.Contains("characters", failedFinish.GetFlattenedText());
                return ToolCallResult(
                    request,
                    new RawToolCall(
                        MemoryDocumentTools.ReplaceToolName,
                        "replace-short-after-bucket",
                        """{"anchor":"head","content":"甲"}"""
                    )
                );
            }
        );
        completionClient.Enqueue(
            request => ToolCallResult(
                request,
                new RawToolCall(
                    MemoryDocumentTools.CompressionFinishToolName,
                    "finish-short-after-bucket",
                    """{"status":"changed"}"""
                )
            )
        );

        var maintainer = new AutobiographicalCompressionMemoryMaintainer(completionClient, "model-a", targetTokenCount: 1);
        var result = await maintainer.MaintainAsync(CreateRequest("甲乙\n\n现在。"), CancellationToken.None);

        Assert.Equal("甲\n\n现在。", result.NewBlock.Text);
    }

    [Fact]
    public async Task MaintainAsync_RejectsAssistantTextWithoutFinishTool() {
        var completionClient = new ScriptedCompletionClient();
        completionClient.Enqueue(
            request => new CompletionResult(
                new ActionMessage([new ActionBlock.Text("压缩后的替换稿。")]),
                new CompletionDescriptor("scripted", "openai-chat-v1", request.ModelId)
            )
        );
        completionClient.Enqueue(
            request => {
                Assert.Contains(MemoryDocumentTools.CompressionFinishToolName, Assert.IsType<ObservationMessage>(request.Context[^1]).Content);
                return new CompletionResult(
                    new ActionMessage([]),
                    new CompletionDescriptor("scripted", "openai-chat-v1", request.ModelId)
                );
            }
        );

        var maintainer = new AutobiographicalCompressionMemoryMaintainer(completionClient, "model-a", targetTokenCount: 2);

        var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => maintainer.MaintainAsync(CreateRequest("旧记忆很长。\n\n现在仍鲜活。"), CancellationToken.None).AsTask()
        );
        Assert.Contains(MemoryDocumentTools.CompressionFinishToolName, exception.Message);
    }

    private static MemoryBlockMaintenanceRequest CreateRequest(string oldBlock)
        => new(
            new RecentHistorySlice(ContextHeaderSnapshot.Empty, Array.Empty<IHistoryMessage>()),
            RolePlayMemoryBlockPaths.FirstPersonAutobiography,
            new MemoryPackBlock(oldBlock)
        );

    private static CompletionResult ToolCallResult(
        CompletionRequest request,
        RawToolCall toolCall,
        string? ignoredText = null
    ) {
        var blocks = new List<ActionBlock>();
        if (ignoredText is not null) { blocks.Add(new ActionBlock.Text(ignoredText)); }
        blocks.Add(new ActionBlock.ToolCall(toolCall));
        return new CompletionResult(
            new ActionMessage(blocks),
            new CompletionDescriptor("scripted", "openai-chat-v1", request.ModelId)
        );
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
