using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Atelia.Agent.Core;
using Atelia.Agent.Core.History;
using Atelia.Agent.Core.Tool;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class AgentEngineAutoCompactionTests {
    private const string Model = "model";
    private const string ProfileName = "profile";

    // ──── 反射辅助 ────

    private static AgentRunState DetermineEngineState(AgentEngine engine) {
        var method = typeof(AgentEngine).GetMethod("DetermineState",
            BindingFlags.NonPublic | BindingFlags.Instance
        )!;
        return (AgentRunState)method.Invoke(engine, null)!;
    }

    // ──── 1. 低于 cap 不触发 ────

    [Fact]
    public async Task ContextBelowCap_DoesNotTriggerCompaction() {
        var client = new CapturingFakeClient(
            CreateDeltaSequence(agg => agg.AppendContent("ok"))
        );
        var profile = new LlmProfile(client, Model, ProfileName, 100_000u);
        var engine = CreateEngineWithAutoCompaction();

        engine.AppendNotification("hello");
        await engine.StepAsync(profile); // WaitingInput → PendingInput
        var step = await engine.StepAsync(profile); // → WaitingInput

        Assert.NotNull(step.Output);
        Assert.Single(client.CapturedRequests);
    }

    // ──── 2. 达到 cap 触发压缩（核心测试） ────

    [Fact]
    public async Task ContextAtCap_TriggersCompaction() {
        // Phase A：用高 cap 构建 2 个完整 Turn，确保有合法切分点
        var buildClient = new CapturingFakeClient(
            CreateDeltaSequence(agg => agg.AppendContent("turn-1")),
            CreateDeltaSequence(agg => agg.AppendContent("turn-2"))
        );
        var highCapProfile = new LlmProfile(buildClient, Model, ProfileName, 100_000u);
        var engine = CreateEngineWithAutoCompaction();

        engine.AppendNotification("t1");
        await engine.StepAsync(highCapProfile);
        await engine.StepAsync(highCapProfile);
        engine.AppendNotification("t2");
        await engine.StepAsync(highCapProfile);
        await engine.StepAsync(highCapProfile);

        Assert.Equal(4, engine.State.RecentHistory.Count);
        Assert.Equal(AgentRunState.WaitingInput, DetermineEngineState(engine));

        // Phase B：切换到低 cap，启动 Turn 3，cap 检查应触发压缩
        var compactClient = new CapturingFakeClient(
            CreateDeltaSequence(agg => agg.AppendContent("summary")),
            CreateDeltaSequence(agg => agg.AppendContent("after-compact"))
        );
        var lowCapProfile = highCapProfile with { SoftContextTokenCap = 1u, Client = compactClient };

        engine.AppendNotification("t3");
        await engine.StepAsync(lowCapProfile); // → PendingInput
        Assert.Equal(5, engine.State.RecentHistory.Count);

        // 1st: cap 命中 → FromStateMutation（尚未调 LLM）
        var s1 = await engine.StepAsync(lowCapProfile);
        Assert.True(s1.ProgressMade);
        Assert.Null(s1.Output);
        Assert.Empty(compactClient.CapturedRequests);

        // 2nd: 执行压缩
        var s2 = await engine.StepAsync(lowCapProfile);
        Assert.Equal(AgentRunState.Compacting, s2.StateBefore);
        Assert.Single(compactClient.CapturedRequests);
        Assert.Equal("summarize-system", compactClient.CapturedRequests[0].SystemPrompt);

        // 3rd: 压缩后正常调用（history<4 不再触发）
        var s3 = await engine.StepAsync(lowCapProfile);
        Assert.NotNull(s3.Output);
        Assert.Equal(2, compactClient.CapturedRequests.Count);
    }

    // ──── 3. 保守 cap 下正常调用不会误触发压缩 ────

    [Fact]
    public async Task ConservativeCap_DoesNotTriggerCompaction() {
        var client = new CapturingFakeClient(
            CreateDeltaSequence(agg => agg.AppendContent("ok"))
        );
        var profile = new LlmProfile(client, Model, ProfileName, 64_000u);
        var engine = CreateEngineWithAutoCompaction();

        engine.AppendNotification("hello");
        await engine.StepAsync(profile);
        var step = await engine.StepAsync(profile);

        Assert.NotNull(step.Output);
        Assert.Single(client.CapturedRequests);
    }

    // ──── 4. 无 AutoCompactionOptions → 回退到正常调用 ────

    [Fact]
    public async Task CapHitButNoAutoCompactionOptions_FallsBackToNormalCall() {
        var client = new CapturingFakeClient(
            CreateDeltaSequence(agg => agg.AppendContent("fallback-ok"))
        );
        var profile = new LlmProfile(client, Model, ProfileName, 1u);
        var engine = new AgentEngine(); // 无 AutoCompactionOptions

        engine.AppendNotification("trigger");
        await engine.StepAsync(profile); // → PendingInput
        var step = await engine.StepAsync(profile); // cap 命中但回退 → 照常调用

        Assert.NotNull(step.Output);
        Assert.Single(client.CapturedRequests);
    }

    // ──── 5. 手动 RequestCompaction 优先于自动触发 ────

    [Fact]
    public async Task ManualCompactionRequest_TakesPriorityOverAuto() {
        // Phase A：构建历史（高 cap）
        var buildClient = new CapturingFakeClient(
            CreateDeltaSequence(agg => agg.AppendContent("turn-1")),
            CreateDeltaSequence(agg => agg.AppendContent("turn-2"))
        );
        var highCapProfile = new LlmProfile(buildClient, Model, ProfileName, 100_000u);
        var engine = CreateEngineWithAutoCompaction();

        engine.AppendNotification("t1");
        await engine.StepAsync(highCapProfile);
        await engine.StepAsync(highCapProfile);
        engine.AppendNotification("t2");
        await engine.StepAsync(highCapProfile);
        await engine.StepAsync(highCapProfile);

        // 手动设置 _compactionRequest（自定义 prompt）
        bool requested = engine.RequestCompaction("custom-system", "custom-summarize");
        Assert.True(requested);
        Assert.Equal(AgentRunState.Compacting, DetermineEngineState(engine));

        // Phase B：立即调 StepAsync — 应进入 Compacting（不新增通知，否则 WaitingInput 会先消费通知）
        var summaryClient = new CapturingFakeClient(
            CreateDeltaSequence(agg => agg.AppendContent("manual-summary"))
        );
        var lowCapProfile = highCapProfile with { SoftContextTokenCap = 1u, Client = summaryClient };

        var step = await engine.StepAsync(lowCapProfile);
        Assert.Equal(AgentRunState.Compacting, step.StateBefore);
        Assert.Single(summaryClient.CapturedRequests);
        Assert.Equal("custom-system", summaryClient.CapturedRequests[0].SystemPrompt);
    }

    // ──── 6. ResolveProfile 修改 profile → 以最终 profile 的 cap 为准 ────

    [Fact]
    public async Task ResolveProfile_ProfileSwap_UsesFinalCap() {
        // Phase A：构建历史（高 cap）
        var buildClient = new CapturingFakeClient(
            CreateDeltaSequence(agg => agg.AppendContent("turn-1")),
            CreateDeltaSequence(agg => agg.AppendContent("turn-2"))
        );
        var highCapProfile = new LlmProfile(buildClient, Model, ProfileName, 100_000u);
        var engine = CreateEngineWithAutoCompaction();

        engine.AppendNotification("t1");
        await engine.StepAsync(highCapProfile);
        await engine.StepAsync(highCapProfile);
        engine.AppendNotification("t2");
        await engine.StepAsync(highCapProfile);
        await engine.StepAsync(highCapProfile);

        // Phase B：低 cap profile，但 handler 替换为高 cap
        var normalClient = new CapturingFakeClient(
            CreateDeltaSequence(agg => agg.AppendContent("ok"))
        );
        var lowCapProfile = highCapProfile with { SoftContextTokenCap = 1u, Client = normalClient };
        var relaxedCapProfile = lowCapProfile with { SoftContextTokenCap = 100_000u };

        engine.ResolveProfile += (_, args) => { args.Profile = relaxedCapProfile; };

        engine.AppendNotification("t3");
        await engine.StepAsync(lowCapProfile); // → PendingInput
        var step = await engine.StepAsync(lowCapProfile); // ResolveProfile 提高 cap → 不触发

        Assert.NotNull(step.Output);
        Assert.Single(normalClient.CapturedRequests);
    }

    [Fact]
    public async Task ResolveProfile_ProfileSwap_RendersWindowAgainstResolvedProfile() {
        var client = new CapturingFakeClient(
            CreateDeltaSequence(agg => agg.AppendContent("ok"))
        );
        var initialProfile = new LlmProfile(client, Model, "initial-profile", 100_000u);
        var resolvedProfile = initialProfile with { Name = "resolved-profile" };
        var engine = new AgentEngine();
        engine.RegisterApp(new ProfileEchoApp());

        engine.ResolveProfile += (_, args) => { args.Profile = resolvedProfile; };

        engine.AppendNotification("trigger");
        await engine.StepAsync(initialProfile);
        var step = await engine.StepAsync(initialProfile);

        Assert.NotNull(step.Output);

        var request = Assert.Single(client.CapturedRequests);
        var windowMessage = Assert.Single(
            request.Context.OfType<ObservationMessage>(),
            message => message.Content is not null && message.Content.Contains("ProfileWindow:", StringComparison.Ordinal)
        );

        Assert.Contains("resolved-profile", windowMessage.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("initial-profile", windowMessage.Content, StringComparison.Ordinal);
    }

    // ──── 7. 工具往返后触发（PendingToolResults 路径） ────

    [Fact]
    public async Task AfterToolRoundtrip_CapCheckTriggersBeforeNextModelCall() {
        var tool = new EchoTool("echo");

        // Phase A：高 cap 完成一个工具往返
        var buildClient = new CapturingFakeClient(
            CreateDeltaSequence(
                agg => agg.AppendContent("calling"),
                agg => agg.AppendToolCall(MakeToolCall("echo", "call-1"))
            ),
            CreateDeltaSequence(agg => agg.AppendContent("done"))
        );
        var highCapProfile = new LlmProfile(buildClient, Model, ProfileName, 100_000u);
        var engine = CreateEngineWithAutoCompaction(tool);

        engine.AppendNotification("trigger");
        await engine.StepAsync(highCapProfile); // → PendingInput
        await engine.StepAsync(highCapProfile); // → WaitingToolResults
        await engine.StepAsync(highCapProfile); // → ToolResultsReady
        await engine.StepAsync(highCapProfile); // → PendingToolResults
        await engine.StepAsync(highCapProfile); // → WaitingInput

        Assert.Equal(4, engine.State.RecentHistory.Count); // O→A→TR→A

        // Phase B：低 cap 新 Turn → cap 检查触发压缩
        var compactClient = new CapturingFakeClient(
            CreateDeltaSequence(agg => agg.AppendContent("summary")),
            CreateDeltaSequence(agg => agg.AppendContent("final"))
        );
        var lowCapProfile = highCapProfile with { SoftContextTokenCap = 1u, Client = compactClient };

        engine.AppendNotification("t2");
        await engine.StepAsync(lowCapProfile); // → PendingInput (5 entries)
        Assert.Equal(5, engine.State.RecentHistory.Count);

        // 1st: cap 命中 → FromStateMutation
        var s1 = await engine.StepAsync(lowCapProfile);
        Assert.True(s1.ProgressMade);
        Assert.Null(s1.Output);
        Assert.Empty(compactClient.CapturedRequests);

        // 2nd: 执行压缩
        var s2 = await engine.StepAsync(lowCapProfile);
        Assert.Equal(AgentRunState.Compacting, s2.StateBefore);
        Assert.Single(compactClient.CapturedRequests);

        // 3rd: 压缩后正常调用
        var s3 = await engine.StepAsync(lowCapProfile);
        Assert.NotNull(s3.Output);
        Assert.Equal(2, compactClient.CapturedRequests.Count);
    }

    // ──── 8. 压缩后 token 仍超 cap → history<4 防抖保护 → 不无限循环 ────

    [Fact]
    public async Task CompactionDoesNotReduceTokens_NoInfiniteLoop() {
        // Phase A：构建历史
        var buildClient = new CapturingFakeClient(
            CreateDeltaSequence(agg => agg.AppendContent("turn-1")),
            CreateDeltaSequence(agg => agg.AppendContent("turn-2"))
        );
        var highCapProfile = new LlmProfile(buildClient, Model, ProfileName, 100_000u);
        var engine = CreateEngineWithAutoCompaction();

        engine.AppendNotification("t1");
        await engine.StepAsync(highCapProfile);
        await engine.StepAsync(highCapProfile);
        engine.AppendNotification("t2");
        await engine.StepAsync(highCapProfile);
        await engine.StepAsync(highCapProfile);

        // Phase B：低 cap → 压缩一次后 history < 4 → 防抖 → 正常调用
        var compactClient = new CapturingFakeClient(
            CreateDeltaSequence(agg => agg.AppendContent("summary")),
            CreateDeltaSequence(agg => agg.AppendContent("fallback-final"))
        );
        var lowCapProfile = highCapProfile with { SoftContextTokenCap = 1u, Client = compactClient };

        engine.AppendNotification("t3");
        await engine.StepAsync(lowCapProfile); // → PendingInput

        var s1 = await engine.StepAsync(lowCapProfile); // cap → FromStateMutation
        Assert.True(s1.ProgressMade);
        Assert.Null(s1.Output);

        var s2 = await engine.StepAsync(lowCapProfile); // 压缩
        Assert.Equal(AgentRunState.Compacting, s2.StateBefore);
        Assert.Single(compactClient.CapturedRequests);

        var s3 = await engine.StepAsync(lowCapProfile); // 压缩后：history<4 防抖 → 正常调用
        Assert.NotNull(s3.Output);
        Assert.Equal(2, compactClient.CapturedRequests.Count); // 只调了 2 次，没有死循环
    }

    // ──── 辅助方法 ────

    private static AgentEngine CreateEngineWithAutoCompaction(params ITool[] tools) {
        var options = new AutoCompactionOptions(
            SystemPrompt: "summarize-system",
            SummarizePrompt: "please summarize"
        );
        var engine = new AgentEngine(autoCompaction: options);
        foreach (var tool in tools) { if (tool is not null) { engine.RegisterTool(tool); } }
        return engine;
    }

    private static Action<CompletionAggregator>[] CreateDeltaSequence(params Action<CompletionAggregator>[] feeds) => feeds;

    private static ParsedToolCall MakeToolCall(string toolName, string callId) =>
        new(toolName, callId, ImmutableDictionary<string, string>.Empty, ImmutableDictionary<string, object?>.Empty, null, null);

    // ──── Fake ────

    private sealed class CapturingFakeClient : ICompletionClient {
        private readonly Queue<Action<CompletionAggregator>[]> _responses;
        public string Name => "test-provider";
        public string ApiSpecId => "test-spec";
        public List<CompletionRequest> CapturedRequests { get; } = new();

        public CapturingFakeClient(params Action<CompletionAggregator>[][] responses) {
            _responses = new Queue<Action<CompletionAggregator>[]>(responses);
        }

        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            if (_responses.Count == 0) { throw new InvalidOperationException("No provider responses configured."); }
            CapturedRequests.Add(request);
            var feeds = _responses.Dequeue();
            var aggregator = new CompletionAggregator(CompletionDescriptor.From(this, request), observer);
            foreach (var feed in feeds) { feed(aggregator); }
            return Task.FromResult(aggregator.Build());
        }
    }

    private sealed class EchoTool : ITool {
        public EchoTool(string name) { Name = name; }
        public string Name { get; }
        public string Description => "echo";
        public bool Visible { get; set; } = true;
        public IReadOnlyList<ToolParamSpec> Parameters => Array.Empty<ToolParamSpec>();
        public ValueTask<LodToolExecuteResult> ExecuteAsync(IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken) {
            return ValueTask.FromResult(new LodToolExecuteResult(ToolExecutionStatus.Success, new LevelOfDetailContent("ok")));
        }
    }

    private sealed class ProfileEchoApp : IApp {
        public string Name => "ProfileEcho";
        public string Description => "Echoes the resolved profile name into a window for testing.";
        public IReadOnlyList<ITool> Tools => Array.Empty<ITool>();

        public string? RenderWindow(AppRenderContext context)
            => $"ProfileWindow: {context.CurrentProfile?.Name}";
    }
}
