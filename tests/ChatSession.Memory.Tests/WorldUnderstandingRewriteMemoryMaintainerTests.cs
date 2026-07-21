using Atelia.ChatSession;
using Atelia.ChatSession.Memory;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.ChatSession.Memory.Tests;

public class WorldUnderstandingRewriteProfileTests {
    [Fact]
    public async Task MaintainAsync_SingleShot_UsesModelTextAsNewBlock() {
        var completionClient = new ScriptedCompletionClient();
        completionClient.Enqueue(
            request => {
                // Rewrite maintainer 固定为单次调用，不暴露工具。
                Assert.Empty(request.Tools);
                // system prompt 来自嵌入的 world-understanding rewrite 资源，且能被正确加载（非空）。
                Assert.Equal(WorldUnderstandingRewriteProfiles.Default.SystemPrompt, request.SystemPrompt);
                Assert.False(string.IsNullOrWhiteSpace(request.SystemPrompt));
                // 旧世界理解只存在于 ContextHeader；末尾 instruction 不再重复注入。
                var instruction = Assert.IsType<ObservationMessage>(request.Context[^1]);
                Assert.Equal(1, CountContextOccurrences(request.Context, "### 刘世超\n\n开发者。"));
                Assert.DoesNotContain("### 刘世超\n\n开发者。", instruction.Content);
                Assert.DoesNotContain("Current block:", instruction.Content);
                Assert.Contains("上下文开头呈现了Galatea目前版本的世界理解", instruction.Content);
                return new CompletionResult(
                    new ActionMessage([new ActionBlock.Text("### 刘世超\n\n他偏好简体中文。")]),
                    new CompletionDescriptor("scripted", "openai-chat-v1", request.ModelId)
                );
            }
        );

        var maintainer = CreateMaintainer(completionClient);
        var result = await maintainer.MaintainAsync(CreateRequest("### 刘世超\n\n开发者。"), CancellationToken.None);

        Assert.Equal("### 刘世超\n\n他偏好简体中文。", result.NewBlock.Text);
    }

    [Fact]
    public void Prompts_DefaultToSimplifiedChinese_AndKeepEnglishAvailable() {
        Assert.Same(WorldUnderstandingRewriteProfiles.SimplifiedChinese, WorldUnderstandingRewriteProfiles.Default);
        Assert.StartsWith("你是Galatea的认知制图师", WorldUnderstandingRewriteProfiles.Default.SystemPrompt);
        Assert.StartsWith("You are Galatea's cognitive cartographer", WorldUnderstandingRewriteProfiles.English.SystemPrompt);
    }

    [Fact]
    public void Target_IsObservationCarrier() {
        var maintainer = CreateMaintainer(new ScriptedCompletionClient());

        Assert.Equal(MemoryPackCarrier.Observation, maintainer.Target.Carrier);
        Assert.Equal(RolePlayMemoryBlockPaths.WorldUnderstanding, maintainer.Target);
    }

    private static RewriteMemoryBlockMaintainer CreateMaintainer(ICompletionClient client)
        => new(WorldUnderstandingRewriteProfiles.Default, client, "model-a");

    private static MemoryBlockMaintenanceRequest CreateRequest(string oldBlock) {
        var memoryPack = new MemoryPack();
        memoryPack.Observation.Add(RolePlayMemoryBlockPaths.WorldUnderstanding.BlockKey, new MemoryPackBlock(oldBlock));
        return new(
            new RecentHistorySlice(
                ContextHeaderSnapshot.FromRenderedMemoryPack(memoryPack.Render()),
                [
                    new ObservationMessage("刘世超说他只用简体中文。"),
                    new ActionMessage([new ActionBlock.Text("[Galatea] 我记下了这个偏好。")])
                ]
            ),
            new MemoryPackBlock(oldBlock)
        );
    }

    private static int CountContextOccurrences(IReadOnlyList<IHistoryMessage> context, string value) {
        int count = 0;
        foreach (var message in context) {
            var text = message switch {
                ObservationMessage observation => observation.Content ?? string.Empty,
                ActionMessage action => action.GetFlattenedText(),
                _ => string.Empty
            };
            int index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0) {
                count++;
                index += value.Length;
            }
        }
        return count;
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
