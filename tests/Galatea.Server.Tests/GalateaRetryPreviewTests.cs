using System.Net;
using System.Net.Http.Json;
using System.Text;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.AgentControl;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaRetryPreviewTests {
    [Fact]
    public async Task ActualHostRetryResetsFailedFilterAndReplayButPreservesCommittedToolSegment() {
        var completion = new PreviewClient();
        var clock = new GalateaLabClock();
        var connection = new CompletionConnectionConfig("test", "openai-chat", "model-a",
            "openai-chat/strict", "http://localhost:8000/", ApiKey: "test-key");
        var profile = GalateaTestHost.CreateGalateaV7Profile();
        await using var host = GalateaTestHost.Create(completion,
            DisabledGalateaUserMessageNormalizer.Instance, timeProvider: clock,
            connections: [connection], agentControlProfile: profile);
        // Fresh calls intentionally expose no tools. Freeze a historical tool-enabled
        // Prepared while this isolated instance is stopped, then exercise real Host recovery.
        EventAddress prepared;
        using (var seed = SessionJournalEngine.OpenForTest(host.SessionDirectory, runtime: null,
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterRequestPreparedCommitted), new EventJournalOptions())) {
            using var agent = Assert.IsType<RecapGridAgentControlOpenResult.Opened>(
                RecapGridAgentControlFactory.Bind(seed.ReadView, profile,
                    new O200kBaseHistoryUnitLoadEstimator())).Handle;
            var identity = CompletionDispatchIdentityFactory.Create(connection, completion);
            seed.UseRuntime(new SessionRuntime(completion, agent.ToolSession,
                CompletionTarget: new(identity.ConnectionId, identity.Kind, identity.ConnectionFingerprint),
                ToolRuntimeIdentity: agent.RuntimeIdentity,
                ContextCandidateSource: new EmptyCandidateSource(), InputProjector: GalateaInputProjector.Instance));
            await Assert.ThrowsAsync<SessionJournalFailpointException>(() =>
                seed.SendAsync("inspect then reply", CancellationToken.None));
            prepared = seed.ReadCurrentHead()!.Value;
        }
        Assert.Equal(0, completion.Calls);
        using HttpClient http = host.CreateClient();
        using var login = await GalateaTestHost.LoginAsync(http);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        using var accepted = await http.PostAsJsonAsync("/api/v1/characters/alice/chat/turns/resume",
            new ResumeTurnRequest(EventAddressTextCodec.Format(prepared), ConnectionId: "test"));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var started = Assert.IsType<StartTurnResponseDto>(await accepted.Content.ReadFromJsonAsync<StartTurnResponseDto>());
        var service = host.Factory.Services.GetRequiredService<GalateaHostService>();
        var session = await service.GetSessionAsync("alice", CancellationToken.None);
        var turn = Assert.IsType<GalateaLiveTurn>(service.FindTurn(session, started.TurnId));
        Task run = Assert.IsAssignableFrom<Task>(turn.RunTask);
        Task first = await Task.WhenAny(completion.FailingAttemptEntered.Task, run).WaitAsync(TimeSpan.FromSeconds(15));
        using (var diagnostic = turn.Subscribe()) {
            Assert.True(first == completion.FailingAttemptEntered.Task,
                $"Runner ended before retry fixture: status={turn.Status}, calls={completion.Calls}, frames={Frames(diagnostic.ReplayFrames)}");
        }
        using var live = turn.Subscribe();
        Assert.Contains("committed-prefix", Frames(live.ReplayFrames));
        completion.ReleaseFailure.SetResult();

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var liveFrames = new List<GalateaSseFrame>();
        while (true) {
            var frame = await live.Reader.ReadAsync(deadline.Token);
            liveFrames.Add(frame);
            if (frame.EventName == "retry-wait") { break; }
        }
        Assert.Contains("failed-partial", Frames(liveFrames));
        Assert.Contains(liveFrames, frame => frame.EventName == "attempt-reset");
        using (var duringRetry = turn.Subscribe()) {
            string replay = Frames(duringRetry.ReplayFrames);
            Assert.Contains("committed-prefix", replay);
            Assert.DoesNotContain("failed-partial", replay);
            Assert.DoesNotContain("failed-reasoning", replay);
        }
        // RetryWait is published immediately before the delay timer is installed.
        // Advance repeatedly until the real production retry has dispatched, avoiding
        // reliance on a racing single clock jump or wall-clock backoff.
        while (completion.Calls < 3) {
            clock.Advance(TimeSpan.FromSeconds(6));
            await Task.Delay(1, deadline.Token);
        }
        await Assert.IsAssignableFrom<Task>(turn.RunTask).WaitAsync(deadline.Token);
        Assert.Equal("completed", turn.Status);
        Assert.Equal(3, completion.Calls);
        Assert.NotSame(completion.FailedObserver, completion.SuccessObserver);
        using var finalReplay = turn.Subscribe();
        string finalFrames = Frames(finalReplay.ReplayFrames);
        Assert.Contains("committed-prefix", finalFrames);
        Assert.Contains("event: text-delta\ndata: {\"delta\":\"fresh-visible\"}", finalFrames);
        Assert.DoesNotContain("failed-partial", finalFrames);
        Assert.DoesNotContain("failed-reasoning", finalFrames);
        Assert.DoesNotContain("<thi", finalFrames);

        using var replayResponse = await http.GetAsync(
            $"/api/v1/characters/alice/chat/turns/{started.TurnId}/events", deadline.Token);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        string wire = await replayResponse.Content.ReadAsStringAsync(deadline.Token);
        Assert.Equal(finalFrames, wire);
        Assert.Contains("event: done", wire);
        var headers = session.Engine.ReadCurrentLineageHeaders().HeadToRoot;
        Assert.Equal(2, headers.Count(header => header.Kind == SessionEventKind.AgentActionProduced));
        Assert.Single(headers, header => header.Kind == SessionEventKind.ToolExecutionStarted);
        Assert.Single(headers, header => header.Kind == SessionEventKind.ToolResultObserved);
        Assert.DoesNotContain(headers, header => header.Kind is SessionEventKind.CompletionAttemptStarted or SessionEventKind.CompletionAttemptFailed);
    }

    private static string Frames(IEnumerable<GalateaSseFrame> frames) =>
        string.Concat(frames.Select(frame => Encoding.UTF8.GetString(frame.Utf8.Span)));

    private sealed class EmptyCandidateSource : ICoherentContextCandidateSource {
        public ValueTask<SessionContextCandidateSelection> SelectAsync(SessionContextSelectionRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(new SessionContextCandidateSelection(
                SessionContextCandidateSelectionStatus.EmptyLineage, Candidate: null));
        public ValueTask<SessionContextCandidateMaterializationResult> MaterializeAsync(
            SessionContextCandidateDescriptor descriptor, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Empty synthetic lineage must not materialize context.");
    }

    private sealed class PreviewClient : ICompletionClientFactory, ICompletionClient {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal TaskCompletionSource FailingAttemptEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CompletionStreamObserver? FailedObserver { get; private set; }
        internal CompletionStreamObserver? SuccessObserver { get; private set; }
        public string Name => "retry-preview-fixture";
        public string ApiSpecId => "openai-chat-v1";
        public ICompletionClient Create(CompletionConnectionConfig connection) => this;

        public async Task<CompletionResult> StreamCompletionAsync(CompletionRequest request,
            CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            int call = Interlocked.Increment(ref _calls);
            Assert.NotNull(observer);
            if (call == 1) {
                var inspect = new RawToolCall("recap_grid_control", "committed-inspect", """{"action":"inspect"}""");
                observer.OnTextDelta("committed-prefix");
                observer.OnToolCall(inspect);
                return new(new ActionMessage([new ActionBlock.Text("committed-prefix"), new ActionBlock.ToolCall(inspect)]),
                    CompletionDescriptor.From(this, request));
            }
            if (call == 2) {
                FailedObserver = observer;
                FailingAttemptEntered.SetResult();
                await ReleaseFailure.Task.WaitAsync(cancellationToken);
                observer.OnTextDelta("failed-partial<thi");
                observer.OnReasoningDelta("failed-reasoning");
                observer.OnToolCall(new RawToolCall("recap_grid_control", "uncommitted-partial-tool", "{"));
                throw new CompletionFailureException(new(CompletionFailureKind.Transport), "synthetic disconnected stream");
            }
            Assert.Equal(3, call);
            SuccessObserver = observer;
            observer.OnTextDelta("fresh-visible");
            return new(new ActionMessage([new ActionBlock.Text("fresh-visible")]), CompletionDescriptor.From(this, request));
        }
    }
}
