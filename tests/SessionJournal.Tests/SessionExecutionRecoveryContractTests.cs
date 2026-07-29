using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionExecutionRecoveryContractTests : IDisposable {
    private readonly List<string> _tempDirectories = [];

    [Theory]
    [InlineData(1)]
    [InlineData(10001)]
    public async Task ResumeIdle_ColdPrefixDiagnosticsStayTailBounded(
        int turnCount
    ) {
        string path = CreateColdIdleJournal(turnCount);
        using var reopened = SessionJournalEngine.Open(path);
        SessionJournalReadDiagnostics before = reopened.CaptureReadDiagnostics();

        ResumeOutcome outcome = await reopened.ResumeAsync(CancellationToken.None);

        SessionJournalReadDiagnostics delta =
            reopened.CaptureReadDiagnostics() - before;
        Assert.False(outcome.Advanced);
        Assert.Equal(2, delta.HeaderPreviewReadCount);
        Assert.Equal(2, delta.PayloadReadCount);
        Assert.True(delta.LogicalPayloadByteCount > 0);
        Assert.Equal(0, delta.ChronologicalChainReadCount);
        Assert.Equal(0, delta.ChronologicalEventCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    public async Task ResumeStartedRefusal_DiagnosticsStayLocalAcrossColdPrefixLengths(
        int turnCount
    ) {
        string path = CreateColdIdleJournal(turnCount);
        var client = new NeverCompletionClient();
        var candidateSource = new TestContextCandidateSource();
        SessionRuntime runtime = CreateRuntime(client) with {
            ContextCandidateSource = candidateSource
        };
        using (var preparing = SessionJournalEngine.OpenForTest(
            path,
            runtime,
            new SessionJournalTestHooks(
                SessionJournalFailpoint.AfterCompletionAttemptStartedCommitted
            )
        )) {
            await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                path,
                preparing,
                candidateSource,
                fixtureId: $"prepared-refusal-{turnCount}"
            );
            await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => preparing.SendAsync("pending", CancellationToken.None)
            );
        }

        int selectionCountBeforeResume =
            candidateSource.SelectionCount;
        int materializationCountBeforeResume =
            candidateSource.MaterializationCount;
        using var reopened = SessionJournalEngine.Open(path, runtime);
        SessionJournalReadDiagnostics before = reopened.CaptureReadDiagnostics();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reopened.ResumeAsync(CancellationToken.None)
        );

        SessionJournalReadDiagnostics delta =
            reopened.CaptureReadDiagnostics() - before;
        Assert.Equal(1, delta.HeaderPreviewReadCount);
        Assert.Equal(3, delta.PayloadReadCount);
        Assert.True(delta.LogicalPayloadByteCount > 0);
        Assert.Equal(0, delta.ChronologicalChainReadCount);
        Assert.Equal(0, delta.ChronologicalEventCount);
        Assert.Equal(0, client.Calls);
        Assert.Equal(
            selectionCountBeforeResume,
            candidateSource.SelectionCount
        );
        Assert.Equal(
            materializationCountBeforeResume,
            candidateSource.MaterializationCount
        );
    }

    public void Dispose() {
        foreach (string path in _tempDirectories) {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private string CreateColdIdleJournal(int turnCount) {
        string tempRoot = Directory.Exists("/dev/shm")
            ? "/dev/shm"
            : Path.GetTempPath();
        string path = Path.Combine(
            tempRoot,
            "atelia-session-journal-cold-prefix-tests",
            Guid.NewGuid().ToString("N")
        );
        _tempDirectories.Add(path);
        using (SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
        }

        using var journal = EventJournal.EventJournal.OpenExisting(path);
        RefId main = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
        EventAddress head = journal.GetHead(main)
            ?? throw new InvalidDataException("Created SessionJournal has no head.");
        for (int i = 0; i < turnCount; i++) {
            EventAddress observation = Commit(
                journal,
                head,
                SessionEventKind.ObservationAccepted,
                new ObservationAcceptedBody($"observation-{i}")
            );
            head = observation;
            head = Commit(
                journal,
                head,
                SessionEventKind.ImportedAgentAction,
                new AgentActionProducedBody(
                    new ActionMessage([new ActionBlock.Text($"action-{i}")]),
                    Invocation(),
                    BuildCorrelationId(observation),
                    new SessionExecutionCheckpoint(0),
                    ToolRuntimeIdentity: null
                )
            );
        }
        return path;
    }

    private static EventAddress Commit(
        EventJournal.EventJournal journal,
        EventAddress parent,
        SessionEventKind kind,
        object body
    ) => journal.CommitToRef(
        SessionJournalDefaults.MainBranchName,
        parent,
        SessionEventCodec.Encode(kind, body),
        opaqueEventKind: (uint)kind,
        hint: default
    ).Unwrap().EventAddress;

    private static string BuildCorrelationId(EventAddress observation)
        => $"atelia.session-journal.turn.v1:{EventAddressTextCodec.Format(observation)}";

    private static CompletionDescriptor Invocation()
        => new("scripted", "test-api-v1", "model-A");

    private static SessionRuntime CreateRuntime(ICompletionClient client)
        => new(
            client,
            CompletionTarget: new SessionCompletionTargetIdentity(
                "test-connection",
                "test",
                "test-connection-fingerprint-v1",
                "test-request-adapter-v1"
            ),
            ContextCandidateSource: new TestContextCandidateSource()
        );

    private sealed class NeverCompletionClient : ICompletionClient {
        public string Name => "scripted";

        public string ApiSpecId => "test-api-v1";

        public int Calls { get; private set; }

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = request;
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            throw new InvalidOperationException("Completion should not be called.");
        }
    }
}
