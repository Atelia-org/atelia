using Atelia.ChatSession;
using Atelia.ChatSession.Memory;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Xunit;

namespace Atelia.ChatSession.Memory.Tests;

public class WorldUnderstandingRewriteMemoryMaintainerTests {
    [Fact]
    public async Task MaintainAsync_SingleShot_UsesModelTextAsNewBlock() {
        var completionClient = new ScriptedCompletionClient();
        completionClient.Enqueue(
            request => {
                // 空 tool session ⇒ 单轮完整重写，无可见工具。
                Assert.Empty(request.Tools);
                // system prompt 来自嵌入的 world-understanding rewrite 资源，且能被正确加载（非空）。
                Assert.Equal(WorldUnderstandingRewritePrompts.SystemPrompt, request.SystemPrompt);
                Assert.False(string.IsNullOrWhiteSpace(request.SystemPrompt));
                // 最后一条 observation 同时含当前 block 与 rewrite user prompt。
                var instruction = Assert.IsType<ObservationMessage>(request.Context[^1]);
                Assert.Contains("### 刘世超", instruction.Content);
                Assert.Contains("The messages above are a segment", instruction.Content);
                return new CompletionResult(
                    new ActionMessage([new ActionBlock.Text("### 刘世超\n\n他偏好简体中文。")]),
                    new CompletionDescriptor("scripted", "openai-chat-v1", request.ModelId)
                );
            }
        );

        var maintainer = CreateMaintainer(completionClient);
        var result = await maintainer.MaintainAsync(CreateRequest("### 刘世超\n\n开发者。"), CancellationToken.None);

        Assert.Equal("### 刘世超\n\n他偏好简体中文。", result.NewBlock.Text);
        Assert.Equal(0, result.ToolCallsExecuted);
    }

    [Fact]
    public void Target_IsObservationCarrier() {
        var maintainer = CreateMaintainer(new ScriptedCompletionClient());

        Assert.Equal(MemoryPackCarrier.Observation, maintainer.Target.Carrier);
        Assert.Equal(RolePlayMemoryBlockPaths.WorldUnderstanding, maintainer.Target);
    }

    private static WorldUnderstandingRewriteMemoryMaintainer CreateMaintainer(ICompletionClient client)
        => new(client, "model-a", new ToolRegistry(Array.Empty<ITool>()).CreateSession());

    private static MemoryBlockMaintenanceRequest CreateRequest(string oldBlock)
        => new(
            new RecentHistorySlice(
                ContextHeaderSnapshot.Empty,
                [
                    new ObservationMessage("刘世超说他只用简体中文。"),
                    new ActionMessage([new ActionBlock.Text("[Galatea] 我记下了这个偏好。")])
                ]
            ),
            RolePlayMemoryBlockPaths.WorldUnderstanding,
            new MemoryPackBlock(oldBlock)
        );

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
