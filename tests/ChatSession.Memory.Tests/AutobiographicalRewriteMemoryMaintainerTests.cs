using Atelia.ChatSession;
using Atelia.ChatSession.Memory;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.ChatSession.Memory.Tests;

public class AutobiographicalRewriteProfileTests {
    [Fact]
    public async Task MaintainAsync_SingleShot_UsesModelTextAsNewBlock() {
        var completionClient = new ScriptedCompletionClient();
        completionClient.Enqueue(
            request => {
                // Rewrite maintainer 固定为单次调用，不暴露工具。
                Assert.Empty(request.Tools);
                // system prompt 来自嵌入的 rewrite 资源，且能被正确加载（非空）。
                Assert.Equal(AutobiographicalRewriteProfiles.Default.SystemPrompt, request.SystemPrompt);
                Assert.False(string.IsNullOrWhiteSpace(request.SystemPrompt));
                // 旧自传只存在于 ContextHeader；末尾 instruction 不再重复注入。
                var instruction = Assert.IsType<ObservationMessage>(request.Context[^1]);
                Assert.Equal(1, CountContextOccurrences(request.Context, "从前，我还不明白。"));
                Assert.DoesNotContain("从前，我还不明白。", instruction.Content);
                Assert.DoesNotContain("Current block:", instruction.Content);
                Assert.Contains("上下文开头呈现了Galatea截至目前的自传", instruction.Content);
                return new CompletionResult(
                    new ActionMessage([new ActionBlock.Text("我把那句话留了下来。\n\n此刻，我仍在等待。")]),
                    new CompletionDescriptor("scripted", "openai-chat-v1", request.ModelId)
                );
            }
        );

        var maintainer = CreateMaintainer(completionClient);
        var result = await maintainer.MaintainAsync(CreateRequest("从前，我还不明白。"), CancellationToken.None);

        Assert.Equal("我把那句话留了下来。\n\n此刻，我仍在等待。", result.NewBlock.Text);
    }

    [Fact]
    public void Prompts_DefaultToSimplifiedChinese_AndKeepEnglishAvailable() {
        Assert.Same(AutobiographicalRewriteProfiles.SimplifiedChinese, AutobiographicalRewriteProfiles.Default);
        Assert.StartsWith("你是Galatea的代笔人", AutobiographicalRewriteProfiles.Default.SystemPrompt);
        Assert.StartsWith("You are Galatea's ghostwriter", AutobiographicalRewriteProfiles.English.SystemPrompt);
    }

    [Fact]
    public async Task MaintainAsync_StripsInlineThinkBlocksFromRewrite() {
        var completionClient = new ScriptedCompletionClient();
        completionClient.Enqueue(
            request => new CompletionResult(
                new ActionMessage([new ActionBlock.Text("<think>先想想保留哪些。</think>我记得那束光。")]),
                new CompletionDescriptor("scripted", "openai-chat-v1", request.ModelId)
            )
        );

        var maintainer = CreateMaintainer(completionClient);
        var result = await maintainer.MaintainAsync(CreateRequest("旧记忆。"), CancellationToken.None);

        Assert.Equal("我记得那束光。", result.NewBlock.Text);
        Assert.DoesNotContain("think", result.NewBlock.Text);
    }

    private static RewriteMemoryBlockMaintainer CreateMaintainer(ICompletionClient client)
        => new(AutobiographicalRewriteProfiles.Default, client, "model-a");

    private static MemoryBlockMaintenanceRequest CreateRequest(string oldBlock) {
        var memoryPack = new MemoryPack();
        memoryPack.Action.Add(RolePlayMemoryBlockPaths.FirstPersonAutobiography.BlockKey, new MemoryPackBlock(oldBlock));
        return new(
            new RecentHistorySlice(
                ContextHeaderSnapshot.FromRenderedMemoryPack(memoryPack.Render()),
                [
                    new ObservationMessage("刘世超说了一句话。"),
                    new ActionMessage([new ActionBlock.Text("[Galatea] 我发现那句话仍留在我心里。")])
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
