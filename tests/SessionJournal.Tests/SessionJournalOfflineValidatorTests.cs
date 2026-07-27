using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionJournalOfflineValidatorTests : IDisposable {
    private readonly List<string> _paths = [];

    [Fact]
    public async Task ReconstructsEveryHistoricalPreparedCommitment() {
        string path = NewPath();
        var client = new NeverCalledCompletionClient();
        EventAddress prepared;
        var candidateSource = new TestContextCandidateSource();
        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            ),
            new SessionRuntime(
                client,
                CompletionTarget: new SessionCompletionTargetIdentity(
                    "offline-validation",
                    "test",
                    "offline-validation-v1",
                    "offline-adapter-v1"
                ),
                ContextCandidateSource: candidateSource
            ),
            new SessionJournalTestHooks(
                SessionJournalFailpoint.AfterRequestPreparedCommitted
            )
        )) {
            await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                path,
                engine,
                candidateSource,
                fixtureId: "offline-validator-historical-prepared"
            );
            await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync(
                    "prepared corruption",
                    CancellationToken.None
                )
            );
            prepared = engine.ResolveExecutionTail().Head
                ?? throw new Xunit.Sdk.XunitException(
                    "Failpoint did not commit Prepared."
                );
        }

        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            RefId main = journal.OpenBranch(
                SessionJournalDefaults.MainBranchName
            ).Unwrap();
            using EventFrame preparedFrame =
                journal.ReadEvent(prepared).Unwrap();
            EventAddress observation = preparedFrame.Header.Parent
                ?? throw new Xunit.Sdk.XunitException(
                    "Prepared has no observation parent."
                );
            var body = (CompletionRequestPreparedBody)
                SessionEventCodec.Decode(
                    SessionEventKind.CompletionRequestPrepared,
                    preparedFrame.Payload,
                    out _
                );
            Assert.True(journal.MoveRef(main, prepared, observation).Unwrap());
            CompletionRequestPreparedBody corrupt = body with {
                Commitment = body.Commitment with {
                    Sha256 = new string('0', 64)
                }
            };
            EventAddress corruptPrepared = Commit(
                journal,
                observation,
                SessionEventKind.CompletionRequestPrepared,
                corrupt
            );
            EventAddress started = Commit(
                journal,
                corruptPrepared,
                SessionEventKind.CompletionAttemptStarted,
                new CompletionAttemptStartedBody()
            );
            _ = Commit(
                journal,
                started,
                SessionEventKind.CompletionAttemptFailed,
                new CompletionAttemptFailedBody(
                    CompletionTerminationKind.Failed,
                    "offline-test",
                    "terminal",
                    Array.AsReadOnly(Array.Empty<string>())
                )
            );
        }

        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await SessionJournalOfflineValidator.ValidateAsync(
                    path
                )
            );
        Assert.Contains(
            "commitment",
            error.Message,
            StringComparison.Ordinal
        );
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task RetiredRawDerivedSetKind_FailsOfflineValidationAtHeaderBoundary() {
        const uint retiredRawDerivedSetKind = 12;
        string path = NewPath();
        using (EventJournal.EventJournal journal = CreateJournal(path)) {
            EventAddress runtime = Commit(
                journal,
                expectedParent: null,
                SessionEventKind.RuntimeConfigSetup,
                new SessionRuntimeConfiguration(
                    "model-A",
                    "surface-A",
                    SessionJournalDefaults.Schema
                )
            );
            EventAddress prompt = Commit(
                journal,
                runtime,
                SessionEventKind.SystemPromptSetup,
                new SystemPromptSetupBody("system-A")
            );
            EventAddress created = Commit(
                journal,
                prompt,
                SessionEventKind.SessionCreated,
                new SessionCreatedBody()
            );
            _ = journal.CommitToRef(
                SessionJournalDefaults.MainBranchName,
                created,
                """{"v":1,"body":{}}"""u8.ToArray(),
                opaqueEventKind: retiredRawDerivedSetKind,
                hint: default
            ).Unwrap();
        }

        using (var engine = SessionJournalEngine.Open(path)) {
            InvalidDataException onlineError =
                Assert.Throws<InvalidDataException>(
                    () => engine.ResolveExecutionTail()
            );
            Assert.Contains(
                "Unknown SessionJournal event kind '12'",
                onlineError.Message,
                StringComparison.Ordinal
            );
        }

        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await SessionJournalOfflineValidator.ValidateAsync(
                    path
                )
            );
        Assert.Contains(
            "Invalid SessionJournal event header",
            error.Message,
            StringComparison.Ordinal
        );
    }

    public void Dispose() {
        foreach (string path in _paths) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch {
                // Best-effort cleanup for validator fixtures.
            }
        }
    }

    private static EventJournal.EventJournal CreateJournal(string path) {
        EventJournal.EventJournal journal =
            EventJournal.EventJournal.CreateNew(path);
        journal.CreateBranch(
            SessionJournalDefaults.MainBranchName,
            startPoint: null
        ).Unwrap();
        return journal;
    }

    private static EventAddress Commit(
        EventJournal.EventJournal journal,
        EventAddress? expectedParent,
        SessionEventKind kind,
        object body
    ) => journal.CommitToRef(
        SessionJournalDefaults.MainBranchName,
        expectedParent,
        SessionEventCodec.Encode(kind, body),
        opaqueEventKind: (uint)kind,
        hint: default
    ).Unwrap().EventAddress;

    private string NewPath() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-session-offline-validator-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }

    private sealed class NeverCalledCompletionClient : ICompletionClient {
        public string Name => "offline-validation-client";

        public string ApiSpecId => "offline-validation-api-v1";

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
            throw new Xunit.Sdk.XunitException(
                "Prepared failpoint must run before provider invocation."
            );
        }
    }
}
