using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Atelia.Agent.Core;
using Atelia.Agent.Core.History;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class AgentEngineIdleObservationTests {
    [Fact]
    public async Task DefaultProvider_FillsHeartbeatNotificationWhenHandlerHasNoInput() {
        var fixedNow = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var engine = new AgentEngine(utcNowProvider: () => fixedNow);

        engine.WaitingInput += (_, args) => args.ShouldContinue = true;

        var result = await engine.StepAsync(CreateNoOpProfile());

        Assert.True(result.ProgressMade);
        var observation = Assert.IsType<ObservationEntry>(result.Input);
        Assert.NotNull(observation.Notifications);
        var text = observation.Notifications!.Basic;
        Assert.Contains("[Heartbeat]", text, StringComparison.Ordinal);
        Assert.Contains("2030-01-02", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdleProviderReturningNull_ProducesNoProgress() {
        var engine = new AgentEngine(idleProvider: new SilentIdleProvider());

        engine.WaitingInput += (_, args) => args.ShouldContinue = true;

        var result = await engine.StepAsync(CreateNoOpProfile());

        Assert.False(result.ProgressMade);
        Assert.Equal(AgentRunState.WaitingInput, result.StateAfter);
        Assert.Null(result.Input);
        Assert.Empty(engine.State.RecentHistory);
    }

    [Fact]
    public async Task IdleProviderIsSkippedWhenAdditionalNotificationProvided() {
        var idle = new RecordingIdleProvider();
        var engine = new AgentEngine(idleProvider: idle);

        engine.WaitingInput += (_, args) => {
            args.ShouldContinue = true;
            args.AdditionalNotification = new LevelOfDetailContent("real-notification");
        };

        var result = await engine.StepAsync(CreateNoOpProfile());

        Assert.True(result.ProgressMade);
        Assert.Equal(0, idle.CallCount);

        var observation = Assert.IsType<ObservationEntry>(result.Input);
        Assert.Equal("real-notification", observation.Notifications!.Basic);
    }

    [Fact]
    public async Task IdleProviderIsSkippedWhenInputEntryProvided() {
        var idle = new RecordingIdleProvider();
        var engine = new AgentEngine(idleProvider: idle);

        var explicitInput = new ObservationEntry();
        explicitInput.AssignNotifications(new LevelOfDetailContent("explicit-input"));

        engine.WaitingInput += (_, args) => {
            args.ShouldContinue = true;
            args.InputEntry = explicitInput;
        };

        var result = await engine.StepAsync(CreateNoOpProfile());

        Assert.True(result.ProgressMade);
        Assert.Equal(0, idle.CallCount);
        Assert.Same(explicitInput, result.Input);
    }

    [Fact]
    public async Task IdleProviderIsSkippedWhenStateAlreadyHasPendingNotification() {
        var idle = new RecordingIdleProvider();
        var engine = new AgentEngine(idleProvider: idle);
        engine.AppendNotification("pre-queued");

        engine.WaitingInput += (_, args) => args.ShouldContinue = true;

        var result = await engine.StepAsync(CreateNoOpProfile());

        Assert.True(result.ProgressMade);
        Assert.Equal(0, idle.CallCount);
        var observation = Assert.IsType<ObservationEntry>(result.Input);
        Assert.Equal("pre-queued", observation.Notifications!.Basic);
    }

    private static LlmProfile CreateNoOpProfile() {
        // ProcessWaitingInput 永远不会真的调用 client，因为状态机会停在 PendingInput。
        var client = new ThrowingCompletionClient();
        return new LlmProfile(Client: client, ModelId: "model", Name: "test");
    }

    private sealed class SilentIdleProvider : IIdleObservationProvider {
        public LevelOfDetailContent? CreateIdleNotification(in IdleObservationContext context) => null;
    }

    private sealed class RecordingIdleProvider : IIdleObservationProvider {
        public int CallCount { get; private set; }

        public LevelOfDetailContent? CreateIdleNotification(in IdleObservationContext context) {
            CallCount++;
            return new LevelOfDetailContent("recorded-idle");
        }
    }

    private sealed class ThrowingCompletionClient : Atelia.Completion.Abstractions.ICompletionClient {
        public string Name => "throwing";
        public string ApiSpecId => "throwing";

        public System.Threading.Tasks.Task<Atelia.Completion.Abstractions.AggregatedAction> StreamCompletionAsync(
            Atelia.Completion.Abstractions.CompletionRequest request,
            Atelia.Completion.Abstractions.CompletionStreamObserver? observer,
            System.Threading.CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Idle observation tests must not reach the completion client.");
    }
}
