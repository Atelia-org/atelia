using Atelia.ChatSession;
using Atelia.ChatSession.Memory;
using Atelia.Completion.Abstractions;
using Atelia.TextEditScript;
using Xunit;

namespace Atelia.ChatSession.Memory.Tests;

public class AutobiographicalRecordingMemoryMaintainerTests {
    [Fact]
    public void EditingSession_FailedEditDoesNotChangeWorkingDocument() {
        var session = new MemoryDocumentEditingSession("第一段。\n\n第二段。");

        var failed = session.Replace("999", "不应写入。");
        var inserted = session.Insert(TextInsertSide.AfterAnchor, "1", "插入段。");

        Assert.False(failed.IsSuccess);
        Assert.True(inserted.IsSuccess);
        Assert.Equal((uint)3, inserted.NewBlockId);
        Assert.Equal("第一段。\n\n插入段。\n\n第二段。", session.RenderDocumentText());
        Assert.Equal(1, session.EditCount);
    }

    [Fact]
    public async Task MaintainAsync_UsesEditedDocumentAndRequiresChangedFinish() {
        var completionClient = new ScriptedCompletionClient();
        completionClient.Enqueue(
            request => {
                Assert.Contains(MemoryDocumentTools.InsertToolName, request.Tools.Select(static tool => tool.Name));
                Assert.Contains(MemoryDocumentTools.RecordingFinishToolName, request.Tools.Select(static tool => tool.Name));
                Assert.Contains(MemoryDocumentTools.RecordingFinishToolName, request.SystemPrompt);
                Assert.DoesNotContain("memory_document.finish_recording", request.SystemPrompt);
                var instruction = Assert.IsType<ObservationMessage>(request.Context[^1]);
                Assert.Contains("[block:1]", instruction.Content);
                Assert.Contains("The messages above are a segment", instruction.Content);

                return ToolCallResult(
                    request,
                    new RawToolCall(
                        MemoryDocumentTools.InsertToolName,
                        "insert-1",
                        """{"side":"after","anchor":"tail","content":"后来，我选择把这句话留下。"}"""
                    ),
                    ignoredText: "THIS ASSISTANT TEXT MUST NOT BECOME MEMORY"
                );
            }
        );
        completionClient.Enqueue(
            request => {
                var toolResults = Assert.IsType<ToolResultsMessage>(request.Context[^1]);
                Assert.Equal(ToolExecutionStatus.Success, Assert.Single(toolResults.Results).Status);
                return ToolCallResult(
                    request,
                    new RawToolCall(
                        MemoryDocumentTools.RecordingFinishToolName,
                        "finish-1",
                        """{"status":"changed"}"""
                    ),
                    ignoredText: "ANOTHER IGNORED TEXT"
                );
            }
        );

        var maintainer = new AutobiographicalRecordingMemoryMaintainer(completionClient, "model-a");
        var result = await maintainer.MaintainAsync(CreateRequest("从前，我还不明白。"), CancellationToken.None);

        Assert.Equal("从前，我还不明白。\n\n后来，我选择把这句话留下。", result.NewBlock.Text);
        Assert.DoesNotContain("IGNORED", result.NewBlock.Text);
        Assert.Equal(2, result.ToolCallsExecuted);
        Assert.Contains("completionStatus=changed", result.Diagnostics);
        Assert.Contains("editCount=1", result.Diagnostics);
    }

    [Fact]
    public async Task MaintainAsync_AcceptsExplicitNoChangeWithoutEdits() {
        var completionClient = new ScriptedCompletionClient();
        completionClient.Enqueue(
            request => ToolCallResult(
                request,
                new RawToolCall(
                    MemoryDocumentTools.RecordingFinishToolName,
                    "finish-no-change",
                    """{"status":"no-change"}"""
                )
            )
        );

        var maintainer = new AutobiographicalRecordingMemoryMaintainer(completionClient, "model-a");
        var result = await maintainer.MaintainAsync(CreateRequest("原有记忆。"), CancellationToken.None);

        Assert.Equal("原有记忆。", result.NewBlock.Text);
        Assert.Equal(1, result.ToolCallsExecuted);
        Assert.Contains("completionStatus=no-change", result.Diagnostics);
        Assert.Contains("editCount=0", result.Diagnostics);
    }

    [Fact]
    public async Task MaintainAsync_RejectsAssistantTextWithoutFinishTool() {
        var completionClient = new ScriptedCompletionClient();
        completionClient.Enqueue(
            request => new CompletionResult(
                new ActionMessage([new ActionBlock.Text("我直接输出一篇替换稿。")]),
                new CompletionDescriptor("scripted", "openai-chat-v1", request.ModelId)
            )
        );
        completionClient.Enqueue(
            request => {
                Assert.Contains(MemoryDocumentTools.RecordingFinishToolName, Assert.IsType<ObservationMessage>(request.Context[^1]).Content);
                return new CompletionResult(
                    new ActionMessage([]),
                    new CompletionDescriptor("scripted", "openai-chat-v1", request.ModelId)
                );
            }
        );

        var maintainer = new AutobiographicalRecordingMemoryMaintainer(completionClient, "model-a");

        var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => maintainer.MaintainAsync(CreateRequest("原有记忆。"), CancellationToken.None).AsTask()
        );
        Assert.Contains(MemoryDocumentTools.RecordingFinishToolName, exception.Message);
    }

    [Fact]
    public async Task MaintainAsync_RejectsFinishAlongsideAnotherToolCall() {
        var completionClient = new ScriptedCompletionClient();
        completionClient.Enqueue(
            request => new CompletionResult(
                new ActionMessage(
                    [
                    new ActionBlock.ToolCall(
                        new RawToolCall(
                            MemoryDocumentTools.InsertToolName,
                            "insert-mixed",
                            """{"side":"after","anchor":"tail","content":"新记忆。"}"""
                        )
                    ),
                    new ActionBlock.ToolCall(
                        new RawToolCall(
                            MemoryDocumentTools.RecordingFinishToolName,
                            "finish-mixed",
                            """{"status":"changed"}"""
                        )
                    )
                ]
                ),
                new CompletionDescriptor("scripted", "openai-chat-v1", request.ModelId)
            )
        );

        var maintainer = new AutobiographicalRecordingMemoryMaintainer(completionClient, "model-a");

        var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => maintainer.MaintainAsync(CreateRequest("原有记忆。"), CancellationToken.None).AsTask()
        );
        Assert.Contains("only tool call", exception.Message);
    }

    private static MemoryBlockMaintenanceRequest CreateRequest(string oldBlock)
        => new(
            new RecentHistorySlice(
                ContextHeaderSnapshot.Empty,
                [
                    new ObservationMessage("刘世超说了一句话。"),
                    new ActionMessage([new ActionBlock.Text("[Galatea] 我发现那句话仍留在我心里。")])
                ]
            ),
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
