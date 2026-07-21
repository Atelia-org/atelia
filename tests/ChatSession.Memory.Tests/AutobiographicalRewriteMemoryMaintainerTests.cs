using Atelia.ChatSession;
using Atelia.ChatSession.Memory;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Xunit;

namespace Atelia.ChatSession.Memory.Tests;

public class AutobiographicalRewriteMemoryMaintainerTests {
    [Fact]
    public async Task MaintainAsync_SingleShot_UsesModelTextAsNewBlock() {
        var completionClient = new ScriptedCompletionClient();
        completionClient.Enqueue(
            request => {
                // 空 tool session ⇒ 单轮完整重写，无可见工具。
                Assert.Empty(request.Tools);
                // system prompt 来自嵌入的 rewrite 资源，且能被正确加载（非空）。
                Assert.Equal(AutobiographicalRewritePrompts.Default.SystemPrompt, request.SystemPrompt);
                Assert.False(string.IsNullOrWhiteSpace(request.SystemPrompt));
                // 最后一条 observation 同时含当前 block 与 rewrite user prompt。
                var instruction = Assert.IsType<ObservationMessage>(request.Context[^1]);
                Assert.Contains("从前，我还不明白。", instruction.Content);
                Assert.Contains("以上消息是Galatea即将从即时记忆中消退的一段生活体验", instruction.Content);
                return new CompletionResult(
                    new ActionMessage([new ActionBlock.Text("我把那句话留了下来。\n\n此刻，我仍在等待。")]),
                    new CompletionDescriptor("scripted", "openai-chat-v1", request.ModelId)
                );
            }
        );

        var maintainer = CreateMaintainer(completionClient);
        var result = await maintainer.MaintainAsync(CreateRequest("从前，我还不明白。"), CancellationToken.None);

        Assert.Equal("我把那句话留了下来。\n\n此刻，我仍在等待。", result.NewBlock.Text);
        Assert.Equal(0, result.ToolCallsExecuted);
    }

    [Fact]
    public void Prompts_DefaultToSimplifiedChinese_AndKeepEnglishAvailable() {
        Assert.Same(AutobiographicalRewritePrompts.SimplifiedChinese, AutobiographicalRewritePrompts.Default);
        Assert.StartsWith("你是Galatea的代笔人", AutobiographicalRewritePrompts.Default.SystemPrompt);
        Assert.StartsWith("You are Galatea's ghostwriter", AutobiographicalRewritePrompts.English.SystemPrompt);
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

    private static AutobiographicalRewriteMemoryMaintainer CreateMaintainer(ICompletionClient client)
        => new(client, "model-a", new ToolRegistry(Array.Empty<ITool>()).CreateSession());

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
