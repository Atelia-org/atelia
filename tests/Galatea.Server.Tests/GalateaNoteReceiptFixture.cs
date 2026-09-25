using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.MemoPod;
using Atelia.SessionJournal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

/// <summary>
/// One synthetic Note, real extraction/reconciliation and durable receipts.
/// This fixture deliberately does not configure or prove Memo recall.
/// </summary>
internal static class GalateaNoteReceiptFixture {
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    internal const string NoteText = "The blue door opens toward the quiet garden.";
    internal const string NoteAction = "I submitted a long-term Note save request with exact text:\n"
        + NoteText + "\nI completed the submission.";
    internal const string DerivedTitle = "Blue garden door";
    internal const string ContinueAction = "I continue exploring the garden.";
    internal static CompletionConnectionConfig MainConnection { get; } = Connection("test", "model-a");
    internal static CompletionConnectionConfig HelperConnection { get; } = Connection("note-helper", "helper-model");
    private static CompletionConnectionConfig Connection(string id, string model) => new(
        id, "openai-chat", model, "openai-chat/strict", "http://127.0.0.1:1/", ApiKey: "synthetic-key");

    internal sealed record Epoch(CharacterSessionHost Session, GalateaAutomaticTurnCoordinator Coordinator,
        GalateaServerAgentHostedService Loop, GalateaLabClock Clock, Factory Completion);

    internal sealed record DurableState(Memo Note, string PodIdentity,
        CharacterNoteReceiptDeliverySnapshot Receipt, IReadOnlyList<SessionCompletedTurnProjection> Turns);

    internal static async Task<Epoch> StartEpochAsync(GalateaScenarioLab lab, GalateaLabClock clock, Factory completion) {
        IServiceProvider services = lab.Host.Factory.Services; // No HTTP client or Player request.
        var host = services.GetRequiredService<GalateaHostService>();
        var coordinator = services.GetRequiredService<GalateaAutomaticTurnCoordinator>();
        var loop = Assert.Single(services.GetServices<IHostedService>().OfType<GalateaServerAgentHostedService>());
        GalateaAgentStatusDto waiting = await ReadWaitingStatusAsync(coordinator);
        CharacterSessionHost session = Assert.IsType<CharacterSessionHost>(host.ReadAttachedSession("alice"));
        Assert.Equal(clock.GetUtcNow().AddMinutes(10).ToUnixTimeMilliseconds(),
            waiting.NextActivationAtUnixTimeMilliseconds);
        Assert.Equal(0, completion.TotalCalls);
        return new(session, coordinator, loop, clock, completion);
    }

    internal static async Task AdvanceHeartbeatAsync(Epoch epoch) {
        var pulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        epoch.Loop.PulseCompletedForTest = _ => pulse.TrySetResult();
        epoch.Clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(50));
        await pulse.Task.WaitAsync(Deadline);
        Assert.Equal(0, epoch.Completion.TotalCalls);
        epoch.Clock.Advance(TimeSpan.FromSeconds(10));
        await epoch.Completion.MainEntered.Task.WaitAsync(Deadline);
        GalateaLiveTurn turn = Assert.IsType<GalateaLiveTurn>(epoch.Session.GetCurrentTurn());
        Assert.IsType<GalateaFreshInput.HeartbeatActivation>(turn.FreshInput);
        epoch.Completion.ReleaseMain.TrySetResult();
        await Assert.IsAssignableFrom<Task>(turn.RunTask).WaitAsync(Deadline);
        Assert.Equal("completed", turn.Status);
        GalateaAgentStatusDto waiting = await ReadWaitingStatusAsync(epoch.Coordinator);
        Assert.Equal(epoch.Clock.GetUtcNow().AddMinutes(10).ToUnixTimeMilliseconds(),
            waiting.NextActivationAtUnixTimeMilliseconds);
        // Finish background enrichment before taking the first cold snapshot.
        // This prevents the next epoch from inheriting pending helper work.
        await UntilAsync(async cancellationToken => {
            await epoch.Session.TurnLock.WaitAsync(cancellationToken);
            try {
                var pod = global::Atelia.MemoPod.MemoPod.Open(
                    epoch.Session.Character.CharacterMemoryStateDir, CharacterNoteDefaultPodV1.PodId);
                return Assert.Single(pod.List()).Title == DerivedTitle
                    && epoch.Session.CharacterMemoryReconciler!.ReadStatusSnapshot().ActiveDerivedInfoSourceAction is null;
            }
            finally { epoch.Session.TurnLock.Release(); }
        });
    }

    internal static async Task<DurableState> ReadStateAsync(Epoch epoch) {
        Assert.True(await epoch.Session.TurnLock.WaitAsync(Deadline));
        try {
            IReadOnlyList<SessionCompletedTurnProjection> turns = epoch.Session.Engine
                .ReadRecentCompletedTurns().RequireSnapshot().Turns;
            string source = EventAddressTextCodec.Format(turns[^1].RequireTerminalAction().Address);
            var memory = epoch.Session.CharacterMemoryReconciler!;
            return new(Assert.Single(global::Atelia.MemoPod.MemoPod.Open(
                    epoch.Session.Character.CharacterMemoryStateDir, CharacterNoteDefaultPodV1.PodId).List()),
                Assert.IsType<string>(memory.ReadStatusSnapshot().SettledDefaultPodStateIdentity),
                Assert.IsType<CharacterNoteReceiptDeliverySnapshot>(memory.ReadReceiptDeliveryExact(source)),
                turns);
        }
        finally { epoch.Session.TurnLock.Release(); }
    }

    private static async Task<GalateaAgentStatusDto> ReadWaitingStatusAsync(GalateaAutomaticTurnCoordinator coordinator) {
        GalateaAgentStatusDto? observed = null;
        await UntilAsync(_ => {
            observed = coordinator.ReadStatus("alice");
            return Task.FromResult(observed.State == "waiting");
        });
        // Admission may change the live status immediately after this read.
        // Validate cadence on this exact waiting snapshot, never a second read.
        return Assert.IsType<GalateaAgentStatusDto>(observed);
    }

    private static async Task UntilAsync(Func<CancellationToken, Task<bool>> condition) {
        using var timeout = new CancellationTokenSource(Deadline);
        while (!await condition(timeout.Token)) { await Task.Delay(5, timeout.Token); }
    }

    internal sealed class Factory(int epoch) : ICompletionClientFactory {
        private int Epoch { get; } = epoch;
        private int _mainCalls;
        private int _extractorCalls;
        private int _extractorRounds;
        private int _derivedCalls;
        private int _saveIntents;
        internal int MainCalls => Volatile.Read(ref _mainCalls);
        // One extraction batch may span an artifact round and a zero-tool round.
        internal int ExtractorCalls => Volatile.Read(ref _extractorCalls);
        internal int ExtractorRounds => Volatile.Read(ref _extractorRounds);
        internal int DerivedCalls => Volatile.Read(ref _derivedCalls);
        internal int SaveIntents => Volatile.Read(ref _saveIntents);
        internal int TotalCalls => MainCalls + ExtractorRounds + DerivedCalls;
        internal string? CurrentObservation { get; private set; }
        internal TaskCompletionSource MainEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseMain { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ICompletionClient Create(CompletionConnectionConfig connection) => connection.Id switch {
            "test" => new Client(this, main: true),
            "note-helper" => new Client(this, main: false),
            _ => throw new InvalidOperationException("Unplanned synthetic Completion connection.")
        };
        private sealed class Client(Factory factory, bool main) : ICompletionClient {
            public string Name => main ? "lab-note-main" : "lab-note-helper";
            public string ApiSpecId => "lab-note-v1";
            public async Task<CompletionResult> StreamCompletionAsync(CompletionRequest request,
                CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
                cancellationToken.ThrowIfCancellationRequested();
                ActionMessage result;
                if (main) {
                    Assert.Equal(1, Interlocked.Increment(ref factory._mainCalls));
                    factory.CurrentObservation = request.PromptPrefix.SharedContextMessages
                        .OfType<ObservationMessage>().Last().Content;
                    factory.MainEntered.TrySetResult();
                    await factory.ReleaseMain.Task.WaitAsync(cancellationToken);
                    string text = factory.Epoch == 1 ? NoteAction : ContinueAction;
                    observer?.OnTextDelta(text);
                    result = new([new ActionBlock.Text(text)]);
                }
                else if (HasTool(request, CharacterNoteExtractor.ToolName)) {
                    Interlocked.Increment(ref factory._extractorRounds);
                    bool continuation = request.TailMessages.OfType<ActionMessage>().Any();
                    if (!continuation) {
                        Assert.Equal(1, Interlocked.Increment(ref factory._extractorCalls));
                    }
                    string target = Assert.IsType<string>(Assert.IsType<ObservationMessage>(request.TailMessages[0]).Content);
                    if (continuation) {
                        Assert.Equal(1, factory.Epoch);
                        Assert.Equal(2, factory.ExtractorRounds);
                        result = new([]);
                    }
                    else if (target.Contains(NoteText, StringComparison.Ordinal)) {
                        Assert.Equal(1, factory.Epoch);
                        Interlocked.Increment(ref factory._saveIntents);
                        result = Tool(CharacterNoteExtractor.ToolName, new { textStartLine = 2, textEndLine = 2 });
                    }
                    else {
                        Assert.True(factory.Epoch > 1);
                        Assert.Contains(ContinueAction, target, StringComparison.Ordinal);
                        result = new([]);
                    }
                }
                else if (HasTool(request, CharacterNoteDerivedInfoEnricher.ToolName)) {
                    Assert.Equal(1, factory.Epoch);
                    Assert.Equal(1, Interlocked.Increment(ref factory._derivedCalls));
                    result = Tool(CharacterNoteDerivedInfoEnricher.ToolName, new {
                        items = new[] { new { artifactOrdinal = 0, title = DerivedTitle,
                            gist = "The door leads to a quiet garden.", summary = "The blue door opens toward the quiet garden." } }
                    });
                }
                else { throw new InvalidOperationException("Unplanned synthetic helper contract."); }
                return new(result, CompletionDescriptor.From(this, request));
            }
            private static bool HasTool(CompletionRequest request, string tool) =>
                request.PromptPrefix.OutputContract.Tools.Any(definition => definition.Name == tool);
            private static ActionMessage Tool(string name, object arguments) => new([
                new ActionBlock.ToolCall(new RawToolCall(name, "synthetic-call", JsonSerializer.Serialize(arguments)))
            ]);
        }
    }
}
