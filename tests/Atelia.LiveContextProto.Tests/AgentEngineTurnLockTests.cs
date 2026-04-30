using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Atelia.Agent.Core;
using Atelia.Agent.Core.History;
using Atelia.Agent.Core.Tool;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

/// <summary>
/// 验证 AgentEngine 的 "Turn 内 LlmProfile 锁定" 不变量。
///
/// Turn = _state.RecentHistory 中从最近一条 ObservationEntry（含）起到末尾的连续段。
/// 同一 Turn 内的所有模型调用必须使用相同 profile（按 Provider/ApiSpec/Model 三元组比对）。
/// </summary>
public sealed class AgentEngineTurnLockTests {
    private const string Model = "model";
    private const string ProfileName = "profile";

    [Fact]
    public async Task FirstTurn_AnyProfile_IsAllowed() {
        var provider = new FakeProviderClient(
            CreateDeltaSequence(agg => agg.AppendContent("ok"))
        );
        var profile = new LlmProfile(provider, Model, ProfileName);
        var engine = new AgentEngine();

        engine.AppendNotification("hello");

        await engine.StepAsync(profile); // WaitingInput -> PendingInput
        var step = await engine.StepAsync(profile); // PendingInput -> WaitingInput (no tools)

        Assert.NotNull(step.Output);
        Assert.Equal(AgentRunState.WaitingInput, step.StateAfter);
    }

    [Fact]
    public async Task SameProfile_AcrossToolRoundtrip_IsAllowed() {
        var tool = new EchoTool("echo");
        var provider = new FakeProviderClient(
            CreateDeltaSequence(
                agg => agg.AppendToolCall(MakeToolCall("echo", "call-1"))
            ),
            CreateDeltaSequence(agg => agg.AppendContent("done"))
        );
        var profile = new LlmProfile(provider, Model, ProfileName);
        var engine = new AgentEngine();
        engine.RegisterTool(tool);

        engine.AppendNotification("trigger");

        // WaitingInput -> PendingInput -> WaitingToolResults -> ToolResultsReady -> PendingToolResults -> WaitingInput
        await engine.StepAsync(profile);
        await engine.StepAsync(profile);
        await engine.StepAsync(profile);
        await engine.StepAsync(profile);
        var final = await engine.StepAsync(profile);

        Assert.Equal(AgentRunState.WaitingInput, final.StateAfter);
        Assert.NotNull(final.Output);
    }

    [Fact]
    public async Task SwitchModelId_WithinTurn_Throws() {
        var tool = new EchoTool("echo");
        var providerA = new FakeProviderClient(
            CreateDeltaSequence(
                agg => agg.AppendToolCall(MakeToolCall("echo", "call-1"))
            )
        );
        var providerB = new FakeProviderClient(
            CreateDeltaSequence(agg => agg.AppendContent("from-B"))
        );
        var profileA = new LlmProfile(providerA, "model-A", "profile-A");
        var profileB = new LlmProfile(providerB, "model-B", "profile-B");
        var engine = new AgentEngine();
        engine.RegisterTool(tool);

        engine.AppendNotification("trigger");

        await engine.StepAsync(profileA); // WaitingInput -> PendingInput
        await engine.StepAsync(profileA); // PendingInput -> WaitingToolResults (locks turn to providerA/model-A)
        await engine.StepAsync(profileA); // WaitingToolResults -> ToolResultsReady
        await engine.StepAsync(profileA); // ToolResultsReady -> PendingToolResults

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await engine.StepAsync(profileB)
        );
        Assert.Contains("Turn is locked", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Model=model-A", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Model=model-B", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwitchProvider_WithinTurn_Throws() {
        var tool = new EchoTool("echo");
        var providerA = new FakeProviderClient(
            name: "provider-A",
            CreateDeltaSequence(
                agg => agg.AppendToolCall(MakeToolCall("echo", "call-1"))
            )
        );
        var providerB = new FakeProviderClient(
            name: "provider-B",
            CreateDeltaSequence(agg => agg.AppendContent("from-B"))
        );
        var profileA = new LlmProfile(providerA, Model, "profile-A");
        var profileB = new LlmProfile(providerB, Model, "profile-B");
        var engine = new AgentEngine();
        engine.RegisterTool(tool);

        engine.AppendNotification("trigger");

        await engine.StepAsync(profileA);
        await engine.StepAsync(profileA);
        await engine.StepAsync(profileA);
        await engine.StepAsync(profileA);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await engine.StepAsync(profileB)
        );
        Assert.Contains("Provider=provider-A", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Provider=provider-B", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwitchApiSpec_WithinTurn_Throws() {
        var tool = new EchoTool("echo");
        var providerA = new FakeProviderClient(
            name: "test-provider",
            apiSpecId: "spec-A",
            CreateDeltaSequence(
                agg => agg.AppendToolCall(MakeToolCall("echo", "call-1"))
            )
        );
        var providerB = new FakeProviderClient(
            name: "test-provider",
            apiSpecId: "spec-B",
            CreateDeltaSequence(agg => agg.AppendContent("from-B"))
        );
        var profileA = new LlmProfile(providerA, Model, "profile-A");
        var profileB = new LlmProfile(providerB, Model, "profile-B");
        var engine = new AgentEngine();
        engine.RegisterTool(tool);

        engine.AppendNotification("trigger");

        await engine.StepAsync(profileA);
        await engine.StepAsync(profileA);
        await engine.StepAsync(profileA);
        await engine.StepAsync(profileA);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await engine.StepAsync(profileB)
        );
        Assert.Contains("ApiSpec=spec-A", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ApiSpec=spec-B", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DifferentProfileNames_WithSameDescriptor_AreAllowed() {
        var tool = new EchoTool("echo");
        var provider = new FakeProviderClient(
            CreateDeltaSequence(
                agg => agg.AppendToolCall(MakeToolCall("echo", "call-1"))
            ),
            CreateDeltaSequence(agg => agg.AppendContent("done"))
        );
        var profileA = new LlmProfile(provider, Model, "profile-A");
        var profileB = new LlmProfile(provider, Model, "profile-B");
        var engine = new AgentEngine();
        engine.RegisterTool(tool);

        engine.AppendNotification("trigger");

        await engine.StepAsync(profileA);
        await engine.StepAsync(profileA);
        await engine.StepAsync(profileA);
        await engine.StepAsync(profileA);
        var final = await engine.StepAsync(profileB);

        Assert.Equal(AgentRunState.WaitingInput, final.StateAfter);
        Assert.NotNull(final.Output);
    }

    [Fact]
    public async Task ProviderReturningMismatchedInvocation_Throws() {
        var provider = new FakeProviderClient(
            name: "provider-A",
            apiSpecId: "spec-A",
            invocationFactory: static _ => new CompletionDescriptor("provider-B", "spec-B", "model-B"),
            CreateDeltaSequence(agg => agg.AppendContent("done"))
        );
        var profile = new LlmProfile(provider, "model-A", "profile-A");
        var engine = new AgentEngine();

        engine.AppendNotification("turn 1");
        await engine.StepAsync(profile);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await engine.StepAsync(profile)
        );

        Assert.Contains("Expected {Provider=provider-A, ApiSpec=spec-A, Model=model-A}", ex.Message, StringComparison.Ordinal);
        Assert.Contains("received {Provider=provider-B, ApiSpec=spec-B, Model=model-B}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwitchProfile_AcrossTurnBoundary_IsAllowed() {
        var providerA = new FakeProviderClient(
            CreateDeltaSequence(agg => agg.AppendContent("turn-1 done"))
        );
        var providerB = new FakeProviderClient(
            name: "provider-B",
            CreateDeltaSequence(agg => agg.AppendContent("turn-2 done"))
        );
        var profileA = new LlmProfile(providerA, "model-A", "profile-A");
        var profileB = new LlmProfile(providerB, "model-B", "profile-B");
        var engine = new AgentEngine();

        // Turn 1 with profile A
        engine.AppendNotification("turn 1");
        await engine.StepAsync(profileA);
        var t1Output = await engine.StepAsync(profileA);
        Assert.NotNull(t1Output.Output);
        Assert.Equal(AgentRunState.WaitingInput, t1Output.StateAfter);

        // Turn 2 with profile B (after a fresh ObservationEntry)
        engine.AppendNotification("turn 2");
        await engine.StepAsync(profileB);
        var t2Output = await engine.StepAsync(profileB);
        Assert.NotNull(t2Output.Output);
        Assert.Equal(AgentRunState.WaitingInput, t2Output.StateAfter);
    }

    [Fact]
    public async Task BeforeModelCallHandler_ReplacingProfileMidTurn_Throws() {
        var tool = new EchoTool("echo");
        var providerA = new FakeProviderClient(
            CreateDeltaSequence(
                agg => agg.AppendToolCall(MakeToolCall("echo", "call-1"))
            ),
            // 第二次 call（工具往返后）会被 handler 替换 profile，期望抛错而不会用到本响应。
            CreateDeltaSequence(agg => agg.AppendContent("should not reach"))
        );
        var providerB = new FakeProviderClient(
            name: "provider-B",
            CreateDeltaSequence(agg => agg.AppendContent("usurper"))
        );
        var profileA = new LlmProfile(providerA, "model-A", "profile-A");
        var profileB = new LlmProfile(providerB, "model-B", "profile-B");
        var engine = new AgentEngine();
        engine.RegisterTool(tool);

        engine.AppendNotification("trigger");

        await engine.StepAsync(profileA); // WaitingInput -> PendingInput
        await engine.StepAsync(profileA); // PendingInput -> WaitingToolResults (locks turn)
        await engine.StepAsync(profileA); // WaitingToolResults -> ToolResultsReady
        await engine.StepAsync(profileA); // ToolResultsReady -> PendingToolResults

        // Handler 在事件中将 Profile 偷换为 profileB，应在二次校验中被拒。
        engine.BeforeModelCall += (_, e) => {
            e.Profile = profileB;
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await engine.StepAsync(profileA)
        );
        Assert.Contains("Turn is locked", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TurnAnalyzer_ToolResultsDoesNotStartNewTurn() {
        var oldDescriptor = new CompletionDescriptor("provider-old", "spec-old", "model-old");
        var newDescriptor = new CompletionDescriptor("provider-new", "spec-new", "model-new");
        var history = new HistoryEntry[] {
            new ObservationEntry(),
            new ActionEntry(new ActionMessage(new ActionBlock[] { new ActionBlock.Text("first") }), oldDescriptor),
            new ToolResultsEntry(Array.Empty<LodToolCallResult>(), "runner_failed"),
            new ActionEntry(new ActionMessage(new ActionBlock[] { new ActionBlock.Text("second") }), newDescriptor)
        };

        var turn = TurnAnalyzer.Analyze(history);

        Assert.Equal(0, turn.StartIndex);
        Assert.Equal(3, turn.EndIndex);
        Assert.True(turn.IsLocked);
        Assert.Equal(oldDescriptor, turn.LockedInvocation);
        Assert.False(TurnAnalyzer.IsTurnStart((HistoryEntry)history[2]));
    }

    [Fact]
    public void TurnAnalyzer_RecappedBoundaryStillPreservesLockInference() {
        var descriptor = new CompletionDescriptor("provider", "spec", "model");
        var history = new HistoryEntry[] {
            new RecapEntry("recap", 42),
            new ActionEntry(new ActionMessage(new ActionBlock[] { new ActionBlock.Text("tool call") }), descriptor),
            new ToolResultsEntry(Array.Empty<LodToolCallResult>(), "runner_failed")
        };

        var turn = TurnAnalyzer.Analyze(history);

        Assert.False(turn.HasExplicitStartBoundary);
        Assert.Equal(-1, turn.StartIndex);
        Assert.Equal(2, turn.EndIndex);
        Assert.Equal(descriptor, turn.LockedInvocation);
    }

    private static ParsedToolCall MakeToolCall(string toolName, string callId) {
        return new ParsedToolCall(
            toolName,
            callId,
            ImmutableDictionary<string, string>.Empty,
            ImmutableDictionary<string, object?>.Empty,
            null,
            null
        );
    }

    private static Action<CompletionAggregator>[] CreateDeltaSequence(params Action<CompletionAggregator>[] feeds) => feeds;

    private sealed class FakeProviderClient : ICompletionClient {
        private readonly Queue<Action<CompletionAggregator>[]> _responses;
        private readonly Func<CompletionRequest, CompletionDescriptor>? _invocationFactory;

        public string Name { get; }
        public string ApiSpecId { get; }

        public FakeProviderClient(params Action<CompletionAggregator>[][] responses)
            : this("test-provider", "test-spec", null, responses) {
        }

        public FakeProviderClient(string name, params Action<CompletionAggregator>[][] responses)
            : this(name, "test-spec", null, responses) {
        }

        public FakeProviderClient(string name, string apiSpecId, params Action<CompletionAggregator>[][] responses) {
            Name = name;
            ApiSpecId = apiSpecId;
            _responses = new Queue<Action<CompletionAggregator>[]>(responses);
        }

        public FakeProviderClient(
            string name,
            string apiSpecId,
            Func<CompletionRequest, CompletionDescriptor>? invocationFactory,
            params Action<CompletionAggregator>[][] responses
        ) {
            Name = name;
            ApiSpecId = apiSpecId;
            _invocationFactory = invocationFactory;
            _responses = new Queue<Action<CompletionAggregator>[]>(responses);
        }

        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            if (_responses.Count == 0) { throw new InvalidOperationException("No provider responses configured."); }

            var feeds = _responses.Dequeue();
            var invocation = _invocationFactory?.Invoke(request) ?? CompletionDescriptor.From(this, request);
            var aggregator = new CompletionAggregator(invocation, observer);
            foreach (var feed in feeds) {
                feed(aggregator);
            }
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
            var result = new LodToolExecuteResult(
                ToolExecutionStatus.Success,
                new LevelOfDetailContent("ok")
            );
            return ValueTask.FromResult(result);
        }
    }
}
