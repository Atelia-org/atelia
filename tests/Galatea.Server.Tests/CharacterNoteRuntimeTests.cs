using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.Galatea.Server.Mailbox;
using Atelia.MemoPod;
using Atelia.SessionJournal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class CharacterNoteRuntimeTests {
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(8);
    private static readonly CompletionDescriptor Invocation = new(
        "runtime-test",
        "runtime-test-v1",
        "model-a"
    );
    private const string Action = """
        [Galatea] I sent mail body to Alice and completed sending.
        [Galatea] I submitted a long-term Note save request with exact text: remember blue, and completed the submission.
        """;
    private const string NoteText = "remember blue";
    private const string NoteEvidence =
        "I submitted a long-term Note save request with exact text: remember blue, and completed the submission.";

    [Fact]
    public async Task SharedClientOverlapsMailAndNoteThenReceiptAttachesOnce() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig helper = Connection("helper");
        var mainClient = new QueueClient(Message(
            new ActionBlock.Text(Action)
        ));
        var helperClient = new OverlapExtractorClient();
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = mainClient,
                [helper.Id] = helperClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, helper],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: helper.Id,
            characterNoteExtractorConnectionId: helper.Id
        );
        (GalateaHostService service, UserSessionHost session) =
            await GetRuntimeAsync(host);

        await session.TurnLock.WaitAsync();
        try {
            GalateaLiveTurn turn = service.StartTurn(
                session,
                "first",
                new GalateaTurnOptions(main.Id)
            );
            Task run = service.RunTurnAsync(
                session,
                turn,
                CancellationToken.None
            );

            await helperClient.BothEntered.Task.WaitAsync(Deadline);
            Assert.Equal(2, helperClient.MaximumActive);
            Assert.False(run.IsCompleted);
            helperClient.Release();
            await run.WaitAsync(Deadline);
            service.FinishTurn(session, turn);

            Assert.Equal("completed", turn.Status);
            Assert.Equal(2, helperClient.Requests.Count);
            Assert.All(helperClient.Requests, request => Assert.Contains(
                Action,
                Assert.IsType<ObservationMessage>(
                    Assert.Single(request.TailMessages)
                ).Content,
                StringComparison.Ordinal
            ));
            GalateaDelegationStateSnapshot durable = session
                .DelegationHandle!.Store.ReadSnapshot();
            Assert.Single(durable.Captures);
            Assert.Equal("Alice", Assert.Single(durable.Mails).Recipient);
            Assert.Equal(1, session.NoteSaveReceipts.Count);
            global::Atelia.MemoPod.MemoPod saved =
                global::Atelia.MemoPod.MemoPod.Open(
                    session.User.CharacterMemoryStateDir,
                    CharacterNoteDefaultPodV1.PodId
                );
            Assert.Equal(MemoPodPhase.Frozen, saved.Phase);
            Assert.Equal(NoteText, Assert.Single(saved.List()).ExactText);
            await service.ReconcileDurableAdmissionAsync(
                session,
                CancellationToken.None
            );
            Assert.Equal(2, helperClient.Requests.Count);
            Assert.Equal(1, session.NoteSaveReceipts.Count);

            GalateaLiveTurn receiptTurn = service.StartTurn(
                session,
                "second",
                new GalateaTurnOptions(main.Id)
            );
            GalateaFreshInput.PlayerAction receiptInput = Assert.IsType<
                GalateaFreshInput.PlayerAction>(receiptTurn.FreshInput);
            Assert.IsType<PlayerTurnNotice.NoteSaveReceipt>(
                Assert.Single(receiptInput.Notices)
            );
            Assert.Equal(0, session.NoteSaveReceipts.Count);
            service.FinishTurn(session, receiptTurn);

            GalateaLiveTurn next = service.StartTurn(
                session,
                "third",
                new GalateaTurnOptions(main.Id)
            );
            Assert.Empty(Assert.IsType<GalateaFreshInput.PlayerAction>(
                next.FreshInput
            ).Notices);
            service.FinishTurn(session, next);
        }
        finally {
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task DiagnosticSinkCapturesSingleLineJsonWithinContentBoundary() {
        const string ExactText = "remember blue\nsecond line";
        const string Evidence =
            "I completed submitting a long-term Note save request:\nremember blue\nsecond line";
        const string ActionMarker = "FULL-ACTION-MUST-NOT-BE-LOGGED";
        string action = $"""
            [Galatea] {ActionMarker}; I sent a mail and completed sending.
            [Galatea] {Evidence}
            """;
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig mail = Connection("mail");
        CompletionConnectionConfig note = Connection("note") with {
            BaseAddress = "https://sensitive-endpoint.invalid/",
            ApiKey = "sensitive-secret",
        };
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(Message(
                    new ActionBlock.Text(action)
                )),
                [mail.Id] = new QueueClient(Message()),
                [note.Id] = new QueueClient(Message(NoteTool(
                    ExactText,
                    Evidence
                ))),
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, mail, note],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, UserSessionHost session) =
            await GetRuntimeAsync(host);
        var diagnostics = new List<string>();
        service.CharacterNoteDiagnosticSinkForTest = diagnostics.Add;

        await session.TurnLock.WaitAsync();
        try {
            GalateaLiveTurn turn = service.StartTurn(
                session,
                "diagnostics",
                new GalateaTurnOptions(main.Id)
            );
            await service.RunTurnAsync(
                    session,
                    turn,
                    CancellationToken.None
                )
                .WaitAsync(Deadline);
            service.FinishTurn(session, turn);

            string artifactJson = Assert.Single(DiagnosticEvents(
                diagnostics,
                "character-note-durable-memo"
            ));
            string batchJson = Assert.Single(DiagnosticEvents(
                diagnostics,
                "character-note-extraction-batch"
            ));
            Assert.All(diagnostics, static json => {
                Assert.DoesNotContain('\r', json);
                Assert.DoesNotContain('\n', json);
            });
            Assert.Contains("\\n", artifactJson, StringComparison.Ordinal);
            using (JsonDocument artifact = JsonDocument.Parse(artifactJson)) {
                Assert.Equal(
                    ExactText,
                    artifact.RootElement.GetProperty("exactText").GetString()
                );
                Assert.Equal(
                    CharacterNoteDefaultPodV1.PodId.Value,
                    artifact.RootElement.GetProperty("podId").GetString()
                );
                Assert.False(artifact.RootElement.TryGetProperty(
                    "evidenceQuote",
                    out _
                ));
            }
            using (JsonDocument batch = JsonDocument.Parse(batchJson)) {
                Assert.Equal(
                    "captured",
                    batch.RootElement.GetProperty("mailOutcome").GetString()
                );
                Assert.Equal(
                    "applied-now",
                    batch.RootElement.GetProperty("noteOutcome").GetString()
                );
                Assert.Equal(
                    "queued",
                    batch.RootElement.GetProperty("receiptOutcome").GetString()
                );
                Assert.True(batch.RootElement.GetProperty("mailMs")
                    .GetInt64() >= 0);
                Assert.True(batch.RootElement.GetProperty("noteMs")
                    .GetInt64() >= 0);
            }
            Assert.All(diagnostics, json => {
                Assert.DoesNotContain(ActionMarker, json,
                    StringComparison.Ordinal);
                Assert.DoesNotContain("sensitive-endpoint", json,
                    StringComparison.Ordinal);
                Assert.DoesNotContain("sensitive-secret", json,
                    StringComparison.Ordinal);
            });
        }
        finally {
            session.TurnLock.Release();
        }
    }

    [Theory]
    [InlineData(NoteOutcome.Zero)]
    [InlineData(NoteOutcome.ProviderFailure)]
    [InlineData(NoteOutcome.Invalid)]
    [InlineData(NoteOutcome.Timeout)]
    public async Task NoteBestEffortFailureMatrixKeepsMailAndMainSuccessful(
        NoteOutcome outcome
    ) {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig mail = Connection("mail");
        CompletionConnectionConfig note = Connection("note");
        var noteClient = new OutcomeNoteClient(outcome);
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(Message(
                    new ActionBlock.Text(Action)
                )),
                [mail.Id] = new QueueClient(Message()),
                [note.Id] = noteClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, mail, note],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, UserSessionHost session) =
            await GetRuntimeAsync(host);
        var diagnostics = new List<string>();
        service.CharacterNoteDiagnosticSinkForTest = diagnostics.Add;
        if (outcome == NoteOutcome.Timeout) {
            service.CharacterNoteExtractionDeadlineForTest =
                TimeSpan.FromMilliseconds(100);
        }

        await session.TurnLock.WaitAsync();
        try {
            GalateaLiveTurn turn = service.StartTurn(
                session,
                "failure matrix",
                new GalateaTurnOptions(main.Id)
            );
            await service.RunTurnAsync(
                    session,
                    turn,
                    CancellationToken.None
                )
                .WaitAsync(Deadline);
            service.FinishTurn(session, turn);

            Assert.Equal("completed", turn.Status);
            Assert.Single(session.DelegationHandle!.Store
                .ReadSnapshot().Captures);
            Assert.Equal(0, session.NoteSaveReceipts.Count);
            if (outcome == NoteOutcome.Zero) {
                int dispatches = noteClient.DispatchCount;
                await service.ReconcileDurableAdmissionAsync(
                    session,
                    CancellationToken.None
                );
                Assert.Equal(dispatches, noteClient.DispatchCount);
                Assert.Null(session.CharacterMemoryReconciler!
                    .ReadStatusSnapshot().ActiveCapture);
                Assert.Empty(global::Atelia.MemoPod.MemoPod.Open(
                    session.User.CharacterMemoryStateDir,
                    CharacterNoteDefaultPodV1.PodId
                ).List());
            }
            else {
                int dispatches = noteClient.DispatchCount;
                GalateaTurnException blocked = await Assert.ThrowsAsync<
                    GalateaTurnException>(async () =>
                        await service.ReconcileDurableAdmissionAsync(
                            session,
                            CancellationToken.None
                        )
                    );
                Assert.StartsWith(
                    "character-memory-",
                    blocked.FailureReason,
                    StringComparison.Ordinal
                );
                Assert.Equal(dispatches + 1, noteClient.DispatchCount);
                Assert.Null(session.CharacterMemoryReconciler!
                    .ReadStatusSnapshot().ActiveCapture);
                Assert.Empty(global::Atelia.MemoPod.MemoPod.Open(
                    session.User.CharacterMemoryStateDir,
                    CharacterNoteDefaultPodV1.PodId
                ).List());
            }
            if (outcome == NoteOutcome.Timeout) {
                Assert.True(noteClient.CancellationObserved);
            }
            AssertNoArtifactDiagnostics(diagnostics);
            string batchJson = Assert.Single(DiagnosticEvents(
                diagnostics,
                "character-note-extraction-batch"
            ));
            Assert.DoesNotContain("note provider unavailable", batchJson,
                StringComparison.Ordinal);
            Assert.DoesNotContain("http://localhost:8000", batchJson,
                StringComparison.Ordinal);
            Assert.DoesNotContain("test-key", batchJson,
                StringComparison.Ordinal);
        }
        finally {
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task CompletedNoteIsNotRetimedOutWhileMailFinishesLater() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig mail = Connection("mail");
        CompletionConnectionConfig note = Connection("note");
        var mailClient = new GatedMessageClient(Message());
        var noteClient = new QueueClient(Message(NoteTool()));
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(Message(
                    new ActionBlock.Text(Action)
                )),
                [mail.Id] = mailClient,
                [note.Id] = noteClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, mail, note],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, UserSessionHost session) =
            await GetRuntimeAsync(host);
        service.CharacterNoteExtractionDeadlineForTest =
            TimeSpan.FromMilliseconds(100);

        await session.TurnLock.WaitAsync();
        try {
            GalateaLiveTurn turn = service.StartTurn(
                session,
                "slow mail",
                new GalateaTurnOptions(main.Id)
            );
            Task run = service.RunTurnAsync(
                session,
                turn,
                CancellationToken.None
            );
            await mailClient.Entered.Task.WaitAsync(Deadline);
            await WaitUntilAsync(() => noteClient.DispatchCount == 1);
            await Task.Delay(200);
            mailClient.Release();
            await run.WaitAsync(Deadline);
            service.FinishTurn(session, turn);

            Assert.Equal("completed", turn.Status);
            Assert.Equal(1, session.NoteSaveReceipts.Count);
        }
        finally {
            mailClient.Release();
            session.TurnLock.Release();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MailFailureAfterAppliedMemoPreservesMemoAndHonestReceiptPolicy(
        bool fatal
    ) {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig mail = Connection("mail");
        CompletionConnectionConfig note = Connection("note");
        var releaseMail = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        Exception expected = fatal
            ? new OutOfMemoryException("fatal mail failure after Note apply")
            : new GalateaTurnException(
                "mail failed after Note apply",
                "mail-test-failure"
            );
        var noteClient = new QueueClient(Message(NoteTool()));
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(Message(
                    new ActionBlock.Text(Action)
                )),
                [mail.Id] = new FailAfterSignalClient(
                    releaseMail.Task,
                    expected
                ),
                [note.Id] = noteClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, mail, note],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, UserSessionHost session) =
            await GetRuntimeAsync(host);

        await session.TurnLock.WaitAsync();
        GalateaLiveTurn turn = service.StartTurn(
            session,
            "mail fails after note apply",
            new GalateaTurnOptions(main.Id)
        );
        try {
            Task run = service.RunTurnAsync(
                session,
                turn,
                CancellationToken.None
            );
            await WaitUntilAsync(() => global::Atelia.MemoPod.MemoPod.Open(
                session.User.CharacterMemoryStateDir,
                CharacterNoteDefaultPodV1.PodId
            ).List().Length == 1);
            releaseMail.TrySetResult();

            Exception? observed = await Record.ExceptionAsync(() => run);
            Assert.Same(expected, observed);
            Assert.Equal(1, noteClient.DispatchCount);
            Assert.Equal(fatal ? 0 : 1, session.NoteSaveReceipts.Count);
            Assert.Equal(NoteText, Assert.Single(
                global::Atelia.MemoPod.MemoPod.Open(
                    session.User.CharacterMemoryStateDir,
                    CharacterNoteDefaultPodV1.PodId
                ).List()
            ).ExactText);
        }
        finally {
            releaseMail.TrySetResult();
            service.FinishTurn(session, turn);
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task IndependentMailAndPreCaptureNoteFailuresRetainOrderedSecondary() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig mail = Connection("mail");
        CompletionConnectionConfig note = Connection("note");
        var noteClient = new OutcomeNoteClient(
            NoteOutcome.ProviderFailure
        );
        var expectedMail = new GalateaTurnException(
            "mail primary",
            "mail-double-failure"
        );
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(Message(
                    new ActionBlock.Text(Action)
                )),
                [mail.Id] = new FailAfterSignalClient(
                    noteClient.Entered.Task,
                    expectedMail
                ),
                [note.Id] = noteClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, mail, note],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, UserSessionHost session) =
            await GetRuntimeAsync(host);

        await session.TurnLock.WaitAsync();
        GalateaLiveTurn turn = service.StartTurn(
            session,
            "double failure",
            new GalateaTurnOptions(main.Id)
        );
        try {
            GalateaTurnException observed = await Assert.ThrowsAsync<
                GalateaTurnException>(() => service.RunTurnAsync(
                    session,
                    turn,
                    CancellationToken.None
                ));

            Assert.Equal("mail-double-failure", observed.FailureReason);
            AggregateException ordered = Assert.IsType<AggregateException>(
                observed.InnerException
            );
            Assert.Collection(
                ordered.InnerExceptions,
                failure => Assert.Same(expectedMail, failure),
                failure => Assert.IsType<TextExtractionException>(failure)
            );
            Assert.Equal(0, session.NoteSaveReceipts.Count);
        }
        finally {
            service.FinishTurn(session, turn);
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task MailFailureCancelsAndDrainsBlockedNote() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig mail = Connection("mail");
        CompletionConnectionConfig note = Connection("note");
        var noteClient = new BlockingClient();
        var mailClient = new FailAfterSignalClient(noteClient.Entered.Task);
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(Message(
                    new ActionBlock.Text(Action)
                )),
                [mail.Id] = mailClient,
                [note.Id] = noteClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, mail, note],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, UserSessionHost session) =
            await GetRuntimeAsync(host);
        var diagnostics = new List<string>();
        service.CharacterNoteDiagnosticSinkForTest = diagnostics.Add;

        await session.TurnLock.WaitAsync();
        GalateaLiveTurn turn = service.StartTurn(
            session,
            "mail failure",
            new GalateaTurnOptions(main.Id)
        );
        try {
            GalateaTurnException failure = await Assert.ThrowsAsync<
                GalateaTurnException>(() => service.RunTurnAsync(
                    session,
                    turn,
                    CancellationToken.None
                ));

            Assert.Equal("delegation-extraction-unavailable",
                failure.FailureReason);
            await noteClient.Drained.Task.WaitAsync(Deadline);
            Assert.Equal(0, noteClient.ActiveCalls);
            Assert.True(noteClient.CancellationObserved);
            Assert.Empty(session.DelegationHandle!.Store
                .ReadSnapshot().Captures);
            Assert.Equal(0, session.NoteSaveReceipts.Count);
            AssertNoArtifactDiagnostics(diagnostics);
            string batchJson = Assert.Single(DiagnosticEvents(
                diagnostics,
                "character-note-extraction-batch"
            ));
            Assert.DoesNotContain("mail extractor unavailable", batchJson,
                StringComparison.Ordinal);
        }
        finally {
            service.FinishTurn(session, turn);
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task MailAbortCallbackFatalIsNotHiddenByInducedNoteCancellation() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig mail = Connection("mail");
        CompletionConnectionConfig note = Connection("note");
        var fatal = new OutOfMemoryException(
            "note cancellation callback fatal"
        );
        var noteClient = new CancellationCallbackFailureClient(fatal);
        var expectedMail = new GalateaTurnException(
            "mail failed",
            "mail-callback-failure"
        );
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(Message(
                    new ActionBlock.Text(Action)
                )),
                [mail.Id] = new FailAfterSignalClient(
                    noteClient.Entered.Task,
                    expectedMail
                ),
                [note.Id] = noteClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, mail, note],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, UserSessionHost session) =
            await GetRuntimeAsync(host);

        await session.TurnLock.WaitAsync();
        GalateaLiveTurn turn = service.StartTurn(
            session,
            "fatal cancellation callback",
            new GalateaTurnOptions(main.Id)
        );
        try {
            AggregateException observed = await Assert.ThrowsAsync<
                AggregateException>(() => service.RunTurnAsync(
                    session,
                    turn,
                    CancellationToken.None
                ));

            await noteClient.Drained.Task.WaitAsync(Deadline);
            Assert.Equal(0, noteClient.ActiveCalls);
            Assert.Same(expectedMail, observed.InnerExceptions[0]);
            AggregateException cancellation = Assert.IsType<
                AggregateException>(observed.InnerExceptions[1]);
            Assert.Contains(fatal, cancellation.Flatten().InnerExceptions);
            Assert.Equal(0, session.NoteSaveReceipts.Count);
        }
        finally {
            service.FinishTurn(session, turn);
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task FatalMailFailureStillCancelsAndDrainsBlockedNote() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig mail = Connection("mail");
        CompletionConnectionConfig note = Connection("note");
        var noteClient = new BlockingClient();
        var fatal = new OutOfMemoryException("fatal mail failure");
        var mailClient = new FailAfterSignalClient(
            noteClient.Entered.Task,
            fatal
        );
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(Message(
                    new ActionBlock.Text(Action)
                )),
                [mail.Id] = mailClient,
                [note.Id] = noteClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, mail, note],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, UserSessionHost session) =
            await GetRuntimeAsync(host);
        var diagnostics = new List<string>();
        service.CharacterNoteDiagnosticSinkForTest = diagnostics.Add;

        await session.TurnLock.WaitAsync();
        GalateaLiveTurn turn = service.StartTurn(
            session,
            "fatal mail failure",
            new GalateaTurnOptions(main.Id)
        );
        try {
            OutOfMemoryException observed = await Assert.ThrowsAsync<
                OutOfMemoryException>(() => service.RunTurnAsync(
                    session,
                    turn,
                    CancellationToken.None
                ));

            Assert.Same(fatal, observed);
            await noteClient.Drained.Task.WaitAsync(Deadline);
            Assert.True(noteClient.CancellationObserved);
            Assert.Equal(0, noteClient.ActiveCalls);
            Assert.Equal(0, session.NoteSaveReceipts.Count);
            Assert.Empty(diagnostics);
        }
        finally {
            service.FinishTurn(session, turn);
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task CallerCancellationDrainsBothExtractorsAndPropagates() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig mail = Connection("mail");
        CompletionConnectionConfig note = Connection("note");
        var mailClient = new BlockingClient();
        var noteClient = new BlockingClient();
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(Message(
                    new ActionBlock.Text(Action)
                )),
                [mail.Id] = mailClient,
                [note.Id] = noteClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, mail, note],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, UserSessionHost session) =
            await GetRuntimeAsync(host);
        var diagnostics = new List<string>();
        service.CharacterNoteDiagnosticSinkForTest = diagnostics.Add;
        using var callerCts = new CancellationTokenSource();

        await session.TurnLock.WaitAsync();
        GalateaLiveTurn turn = service.StartTurn(
            session,
            "cancel",
            new GalateaTurnOptions(main.Id)
        );
        try {
            Task run = service.RunTurnAsync(
                session,
                turn,
                callerCts.Token
            );
            await mailClient.Entered.Task.WaitAsync(Deadline);
            await noteClient.Entered.Task.WaitAsync(Deadline);
            callerCts.Cancel();

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run
            );
            await mailClient.Drained.Task.WaitAsync(Deadline);
            await noteClient.Drained.Task.WaitAsync(Deadline);
            Assert.Equal(0, mailClient.ActiveCalls);
            Assert.Equal(0, noteClient.ActiveCalls);
            Assert.Equal(0, session.NoteSaveReceipts.Count);
            AssertNoArtifactDiagnostics(diagnostics);
        }
        finally {
            service.FinishTurn(session, turn);
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task FatalNoteFailureWinsCallerCancellationAfterBothDrain() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig mail = Connection("mail");
        CompletionConnectionConfig note = Connection("note");
        var mailClient = new BlockingClient();
        var fatal = new OutOfMemoryException(
            "fatal Note failure during caller cancellation"
        );
        var noteClient = new FatalOnCancellationClient(fatal);
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(Message(
                    new ActionBlock.Text(Action)
                )),
                [mail.Id] = mailClient,
                [note.Id] = noteClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, mail, note],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, UserSessionHost session) =
            await GetRuntimeAsync(host);

        using var callerCts = new CancellationTokenSource();
        await session.TurnLock.WaitAsync();
        GalateaLiveTurn turn = service.StartTurn(
            session,
            "caller cancel with fatal Note",
            new GalateaTurnOptions(main.Id)
        );
        try {
            Task run = service.RunTurnAsync(
                session,
                turn,
                callerCts.Token
            );
            await Task.WhenAll(
                mailClient.Entered.Task,
                noteClient.Entered.Task
            ).WaitAsync(Deadline);
            callerCts.Cancel();

            AggregateException observed = await Assert.ThrowsAsync<
                AggregateException>(() => run);
            await Task.WhenAll(
                mailClient.Drained.Task,
                noteClient.Drained.Task
            ).WaitAsync(Deadline);
            Assert.Contains(fatal, observed.Flatten().InnerExceptions);
            Assert.Equal(0, mailClient.ActiveCalls);
            Assert.Equal(0, noteClient.ActiveCalls);
            Assert.Equal(0, session.NoteSaveReceipts.Count);
        }
        finally {
            service.FinishTurn(session, turn);
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task HeadChangeAfterMailCaptureDropsSuccessfulNoteReceipt() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig mail = Connection("mail");
        CompletionConnectionConfig note = Connection("note");
        var noteClient = new GatedNoteClient();
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(Message(
                    new ActionBlock.Text(Action)
                )),
                [mail.Id] = new QueueClient(Message()),
                [note.Id] = noteClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, mail, note],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, UserSessionHost session) =
            await GetRuntimeAsync(host);
        var diagnostics = new List<string>();
        service.CharacterNoteDiagnosticSinkForTest = diagnostics.Add;

        await session.TurnLock.WaitAsync();
        GalateaLiveTurn turn = service.StartTurn(
            session,
            "head fence",
            new GalateaTurnOptions(main.Id)
        );
        try {
            Task run = service.RunTurnAsync(
                session,
                turn,
                CancellationToken.None
            );
            await noteClient.Entered.Task.WaitAsync(Deadline);
            await WaitUntilAsync(
                () => session.DelegationHandle!.Store
                    .ReadSnapshot().Captures.Count == 1
            );
            EventAddress action = session.Engine.ReadCurrentHead()
                ?? throw new Xunit.Sdk.XunitException(
                    "The completed Action head is unavailable."
                );
            _ = Assert.IsType<SessionTurnRetractionResult.Moved>(
                session.Engine.RewindLatestCompletedTurn(action)
            );
            noteClient.Release();

            GalateaTurnException failure = await Assert.ThrowsAsync<
                GalateaTurnException>(() => run);
            Assert.Equal("delegation-state-changed", failure.FailureReason);
            Assert.Single(session.DelegationHandle!.Store
                .ReadSnapshot().Captures);
            Assert.Equal(0, session.NoteSaveReceipts.Count);
            AssertNoArtifactDiagnostics(diagnostics);
            string batchJson = Assert.Single(DiagnosticEvents(
                diagnostics,
                "character-note-extraction-batch"
            ));
            using JsonDocument batch = JsonDocument.Parse(batchJson);
            Assert.Equal(
                "head-changed",
                batch.RootElement.GetProperty("receiptOutcome").GetString()
            );
        }
        finally {
            noteClient.Release();
            service.FinishTurn(session, turn);
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task HeadChangeOutranksNonFatalMailAndRetainsItWithoutReceipt() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig mail = Connection("mail");
        CompletionConnectionConfig note = Connection("note");
        var releaseMail = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var expectedMail = new GalateaTurnException(
            "mail failed after durable Note apply",
            "mail-after-head-change"
        );
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(Message(
                    new ActionBlock.Text(Action)
                )),
                [mail.Id] = new FailAfterSignalClient(
                    releaseMail.Task,
                    expectedMail
                ),
                [note.Id] = new QueueClient(Message(NoteTool())),
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, mail, note],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, UserSessionHost session) =
            await GetRuntimeAsync(host);

        await session.TurnLock.WaitAsync();
        GalateaLiveTurn turn = service.StartTurn(
            session,
            "head authority",
            new GalateaTurnOptions(main.Id)
        );
        try {
            Task run = service.RunTurnAsync(
                session,
                turn,
                CancellationToken.None
            );
            await WaitUntilAsync(() => global::Atelia.MemoPod.MemoPod.Open(
                session.User.CharacterMemoryStateDir,
                CharacterNoteDefaultPodV1.PodId
            ).List().Length == 1);
            EventAddress action = session.Engine.ReadCurrentHead()
                ?? throw new Xunit.Sdk.XunitException(
                    "The completed Action head is unavailable."
                );
            Assert.IsType<SessionTurnRetractionResult.Moved>(
                session.Engine.RewindLatestCompletedTurn(action)
            );
            releaseMail.TrySetResult();

            GalateaTurnException observed = await Assert.ThrowsAsync<
                GalateaTurnException>(() => run);
            Assert.Equal("delegation-state-changed", observed.FailureReason);
            AggregateException retained = Assert.IsType<AggregateException>(
                observed.InnerException
            );
            Assert.Equal(
                "delegation-state-changed",
                Assert.IsType<GalateaTurnException>(
                    retained.InnerExceptions[0]
                ).FailureReason
            );
            Assert.Same(expectedMail, retained.InnerExceptions[1]);
            Assert.Equal(0, session.NoteSaveReceipts.Count);
        }
        finally {
            releaseMail.TrySetResult();
            service.FinishTurn(session, turn);
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task PendingCaptureSurvivesRewindAndAdmissionSettlesWithoutProviderOrReceipt() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig mail = Connection("mail");
        CompletionConnectionConfig note = Connection("note");
        var noteClient = new QueueClient();
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(),
                [mail.Id] = new QueueClient(),
                [note.Id] = noteClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, mail, note],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        var owner = new CharacterMemoryStoreOwner(
            "alice",
            CharacterMemorySessionComposition.CreateSessionRepositoryId(
                host.SessionDirectory
            )
        );
        CharacterMemoryStoreBaseline baseline;
        GalateaTerminalActionExtractionTarget target;
        EventAddress action;
        using (SessionJournalEngine engine = SessionJournalEngine.Open(
                   host.SessionDirectory)) {
            baseline = new CharacterMemoryStoreBaseline(
                engine.ReadView.ReadPhysicalAppendFrontier(),
                EventAddressTextCodec.FormatNullable(
                    engine.ReadCurrentHead()
                )
            );
            action = AppendAction(engine, Action);
            target = Assert.IsType<
                GalateaTerminalActionExtractionReadResult.Available
            >(GalateaTerminalActionExtractionTargetReader.ReadAt(
                engine,
                action
            )).Target;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(
            host.CharacterMemoryStateDirectory
        )!);
        using (CharacterNoteDefaultPodReconciler provisioned =
            await CharacterNoteDefaultPodReconciler.CreateNewAsync(
                host.CharacterMemoryStateDirectory,
                owner,
                baseline,
                DisabledCharacterNoteExtractor.Instance
            )) { }
        using (CharacterMemorySqliteStore store =
            CharacterMemorySqliteStore.OpenExisting(
                host.CharacterMemoryStateDirectory,
                owner
            )) {
            CharacterMemoryCaptureResult captured = store.CaptureNew(new(
                EventAddressTextCodec.Format(action),
                target.VisibleTextSha256,
                target.VisibleTextUtf8Bytes,
                "historical-character-note-contract",
                [NoteText]
            ));
            Assert.Equal(
                CharacterMemoryCaptureDisposition.Captured,
                captured.Disposition
            );
        }
        using (SessionJournalEngine engine = SessionJournalEngine.Open(
                   host.SessionDirectory)) {
            Assert.IsType<SessionTurnRetractionResult.Moved>(
                engine.RewindLatestCompletedTurn(action)
            );
        }

        (GalateaHostService service, UserSessionHost session) =
            await GetRuntimeAsync(host);

        Assert.Equal(0, noteClient.DispatchCount);
        Assert.Equal(0, session.NoteSaveReceipts.Count);
        Assert.Null(session.CharacterMemoryReconciler!
            .ReadStatusSnapshot().ActiveCapture);
        Assert.Equal(NoteText, Assert.Single(
            global::Atelia.MemoPod.MemoPod.Open(
                session.User.CharacterMemoryStateDir,
                CharacterNoteDefaultPodV1.PodId
            ).List()
        ).ExactText);

        await session.TurnLock.WaitAsync();
        try {
            await service.ReconcileDurableAdmissionAsync(
                session,
                CancellationToken.None
            );
            Assert.Equal(0, noteClient.DispatchCount);
            Assert.Equal(0, session.NoteSaveReceipts.Count);
        }
        finally {
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task QueueIsClaimedOnlyByOrdinaryEmptyPlayerTurn() {
        CompletionConnectionConfig main = Connection("test");
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(Message()),
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main]
        );
        (GalateaHostService service, UserSessionHost session) =
            await GetRuntimeAsync(host);
        CharacterNoteSaveReceipt receipt = CreateReceipt();
        Assert.True(session.NoteSaveReceipts.TryEnqueue(receipt));

        await session.TurnLock.WaitAsync();
        try {
            Assert.IsType<GalateaReadyReplyTurnStartResult.Empty>(
                service.StartReadyReplyTurn(
                    session,
                    new GalateaTurnOptions(main.Id)
                )
            );
            Assert.Equal(1, session.NoteSaveReceipts.Count);

            GalateaLiveTurn inbound = service.StartInboundMailTurn(
                session,
                MailboxMessage.CreateInbound(
                    session.User.CharacterName,
                    "outside",
                    null,
                    "body"
                ),
                new GalateaTurnOptions(main.Id)
            );
            Assert.Equal(1, session.NoteSaveReceipts.Count);
            service.FinishTurn(session, inbound);

            GalateaLiveTurn recovery = service.StartRecovery(
                session,
                new GalateaTurnOptions(
                    main.Id,
                    GalateaTurnMode.Resume
                )
            );
            Assert.Equal(1, session.NoteSaveReceipts.Count);
            service.FinishTurn(session, recovery);

            GalateaLiveTurn ordinary = service.StartTurn(
                session,
                "ordinary",
                new GalateaTurnOptions(main.Id)
            );
            Assert.Same(
                receipt.Notice,
                Assert.Single(Assert.IsType<GalateaFreshInput.PlayerAction>(
                    ordinary.FreshInput
                ).Notices)
            );
            Assert.Equal(0, session.NoteSaveReceipts.Count);
            service.FinishTurn(session, ordinary);
        }
        finally {
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task CreatedReplyCutoffDoesNotClaimQueuedNoteReceipt() {
        CompletionConnectionConfig main = Connection("test");
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(Message()),
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main]
        );
        (GalateaHostService service, UserSessionHost session) =
            await GetRuntimeAsync(host);
        ProduceReadyReply(session);
        CharacterNoteSaveReceipt receipt = CreateReceipt();
        Assert.True(session.NoteSaveReceipts.TryEnqueue(receipt));

        await session.TurnLock.WaitAsync();
        try {
            GalateaLiveTurn turn = service.StartTurn(
                session,
                "ordinary with reply",
                new GalateaTurnOptions(main.Id)
            );
            GalateaFreshInput.PlayerAction input = Assert.IsType<
                GalateaFreshInput.PlayerAction>(turn.FreshInput);
            Assert.IsType<PlayerTurnNotice.Reply>(
                Assert.Single(input.Notices)
            );
            Assert.Equal(1, session.NoteSaveReceipts.Count);
            turn.DurableReplyLease!.RollbackBeforeEffect();
            service.FinishTurn(session, turn);
        }
        finally {
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task RecoveryCompletionUsesCommonPostCompletionSavePath() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig mail = Connection("mail");
        CompletionConnectionConfig note = Connection("note");
        var noteClient = new QueueClient(Message(NoteTool()));
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(Message(
                    new ActionBlock.Text(Action)
                )),
                [mail.Id] = new QueueClient(Message()),
                [note.Id] = noteClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, mail, note],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, UserSessionHost session) =
            await GetRuntimeAsync(host);
        Assert.Equal(0, noteClient.DispatchCount);
        EventAddress observationHead = session.Engine.AppendObservation(
            GalateaHostService.WrapUserMessageForEngine(
                "recover",
                DateTimeOffset.UnixEpoch
            )
        );

        await session.TurnLock.WaitAsync();
        try {
            GalateaLiveTurn recovery = service.StartRecovery(
                session,
                new GalateaTurnOptions(
                    main.Id,
                    GalateaTurnMode.Resume,
                    ExpectedHead: observationHead
                )
            );
            await service.RunTurnAsync(
                    session,
                    recovery,
                    CancellationToken.None
                )
                .WaitAsync(Deadline);
            service.FinishTurn(session, recovery);

            Assert.Equal(1, noteClient.DispatchCount);
            Assert.Equal(1, session.NoteSaveReceipts.Count);
            Assert.Equal(NoteText, Assert.Single(
                global::Atelia.MemoPod.MemoPod.Open(
                    session.User.CharacterMemoryStateDir,
                    CharacterNoteDefaultPodV1.PodId
                ).List()
            ).ExactText);
        }
        finally {
            session.TurnLock.Release();
        }
    }

    private static async Task<(GalateaHostService Service,
        UserSessionHost Session)> GetRuntimeAsync(GalateaTestHost host) {
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        return (service, session);
    }

    private static IReadOnlyList<string> DiagnosticEvents(
        IEnumerable<string> diagnostics,
        string eventName
    ) => diagnostics.Where(json => {
        using JsonDocument document = JsonDocument.Parse(json);
        return string.Equals(
            document.RootElement.GetProperty("event").GetString(),
            eventName,
            StringComparison.Ordinal
        );
    }).ToArray();

    private static void AssertNoArtifactDiagnostics(
        IEnumerable<string> diagnostics
    ) => Assert.Empty(DiagnosticEvents(
        diagnostics,
        "character-note-durable-memo"
    ));

    private static CharacterNoteSaveReceipt CreateReceipt() {
        Assert.True(CharacterNoteSaveReceipt.TryCreate(
            [new CharacterNoteAppliedMemo(
                "0000000100000001",
                0,
                CharacterNoteDefaultPodV1.PodId,
                MemoId.Parse("m1:00000001"),
                "queued note"
            )],
            out CharacterNoteSaveReceipt? receipt
        ));
        return receipt;
    }

    private static void ProduceReadyReply(UserSessionHost session) {
        const string VisibleAction = "ready reply source";
        GalateaDelegationSqliteStore store = session.DelegationHandle!.Store;
        string sourceAction = EventAddressTextCodec.Format(
            session.Engine.ReadCurrentHead()
                ?? throw new Xunit.Sdk.XunitException(
                    "The test session has no current head."
                )
        );
        GalateaDelegationCaptureResult captured = store.CaptureActionBatch(
            new GalateaDelegationCaptureRequest(
                sourceAction,
                Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(VisibleAction)
                )).ToLowerInvariant(),
                Encoding.UTF8.GetByteCount(VisibleAction),
                "runtime-test-extractor-contract",
                [new SendMailIntent(
                    GalateaDelegateConfigReader.CanonicalRecipient,
                    Subject: null,
                    Body: "task",
                    InReplyToMessageId: null,
                    EvidenceQuote: "evidence"
                )]
            )
        );
        GalateaDelegationStateSnapshot snapshot = store.ReadSnapshot();
        GalateaRouteBindingSnapshot binding = store.BeginThreadBinding(
            "runtime-test-binding",
            snapshot.Route.Revision
        );
        _ = store.CompleteThreadBinding(
            binding.BindingOperationId!,
            "runtime-test-thread",
            binding.Revision
        );
        snapshot = store.ReadSnapshot();
        string dispatchId = Assert.Single(captured.DispatchIds);
        GalateaOutboundMailSnapshot mail = snapshot.Mails.Single(value =>
            string.Equals(
                value.DispatchId,
                dispatchId,
                StringComparison.Ordinal
            )
        );
        GalateaOutboundMailSnapshot started = store.StartQueuedMail(
            dispatchId,
            mail.Revision,
            snapshot.Route.Revision
        );
        _ = store.RecordCompletedMail(
            dispatchId,
            started.Revision,
            "runtime-test-thread",
            "runtime-test-turn",
            "ready reply"
        );
    }

    private static async Task WaitUntilAsync(Func<bool> condition) {
        using var deadline = new CancellationTokenSource(Deadline);
        while (!condition()) {
            await Task.Delay(10, deadline.Token);
        }
    }

    private static EventAddress AppendAction(
        SessionJournalEngine engine,
        string visibleText
    ) {
        _ = engine.AppendObservation("runtime pending fixture");
        return engine.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text(visibleText)]),
            Invocation
        );
    }

    private static CompletionConnectionConfig Connection(string id) => new(
        id,
        "openai-chat",
        string.Equals(id, "test", StringComparison.Ordinal)
            ? "model-a"
            : id + "-model",
        "openai-chat/strict",
        "http://localhost:8000/",
        ApiKey: "test-key"
    );

    private static bool HasTool(CompletionRequest request, string name) =>
        request.PromptPrefix.OutputContract.Tools.Any(definition =>
            string.Equals(definition.Name, name, StringComparison.Ordinal)
        );

    private static ActionBlock.ToolCall MailTool() => new(new RawToolCall(
        OutboundMailExtractor.ToolName,
        "mail-call",
        JsonSerializer.Serialize(new {
            recipient = "Alice",
            subject = (string?)null,
            body = "mail body",
            inReplyToMessageId = (string?)null,
            evidenceQuote = "completed sending",
        }, new JsonSerializerOptions {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        })
    ));

    private static ActionBlock.ToolCall NoteTool(
        string exactText = NoteText,
        string evidenceQuote = NoteEvidence
    ) => new(new RawToolCall(
        CharacterNoteExtractor.ToolName,
        "note-call",
        JsonSerializer.Serialize(new { exactText, evidenceQuote })
    ));

    private static ActionMessage Message(params ActionBlock[] blocks) =>
        new(blocks);

    public enum NoteOutcome {
        Zero,
        ProviderFailure,
        Invalid,
        Timeout,
    }

    private sealed class OutcomeNoteClient(NoteOutcome outcome)
        : ICompletionClient {
        private int _cancellationObserved;
        private int _dispatchCount;

        public string Name => "character-note-outcome";
        public string ApiSpecId => "test-v1";
        internal bool CancellationObserved =>
            Volatile.Read(ref _cancellationObserved) != 0;
        internal int DispatchCount => Volatile.Read(ref _dispatchCount);
        internal TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public async Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            Interlocked.Increment(ref _dispatchCount);
            Entered.TrySetResult();
            ActionMessage message;
            switch (outcome) {
                case NoteOutcome.Zero:
                    message = Message();
                    break;
                case NoteOutcome.ProviderFailure:
                    throw new TextExtractionException(
                        TextExtractionFailureKind.ClientUnavailable,
                        "note provider unavailable"
                    );
                case NoteOutcome.Invalid:
                    message = Message(NoteTool("not grounded", NoteEvidence));
                    break;
                case NoteOutcome.Timeout:
                    try {
                        await Task.Delay(
                            Timeout.InfiniteTimeSpan,
                            cancellationToken
                        );
                    }
                    catch (OperationCanceledException) {
                        Interlocked.Exchange(ref _cancellationObserved, 1);
                        throw;
                    }
                    throw new Xunit.Sdk.XunitException(
                        "Infinite delay unexpectedly completed."
                    );
                default:
                    throw new ArgumentOutOfRangeException();
            }
            return new CompletionResult(
                message,
                CompletionDescriptor.From(this, request)
            );
        }
    }

    private sealed class OverlapExtractorClient : ICompletionClient {
        private readonly ConcurrentQueue<CompletionRequest> _requests = new();
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _active;
        private int _maximumActive;

        public string Name => "overlap-extractors";
        public string ApiSpecId => "test-v1";
        internal TaskCompletionSource BothEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        internal int MaximumActive => Volatile.Read(ref _maximumActive);
        internal IReadOnlyList<CompletionRequest> Requests =>
            _requests.ToArray();

        internal void Release() => _release.TrySetResult();

        public async Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            _requests.Enqueue(request);
            int active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            if (active == 2) { BothEntered.TrySetResult(); }
            try {
                await _release.Task.WaitAsync(cancellationToken);
                ActionMessage message = HasTool(
                    request,
                    OutboundMailExtractor.ToolName
                )
                    ? Message(MailTool())
                    : HasTool(request, CharacterNoteExtractor.ToolName)
                        ? Message(NoteTool())
                        : throw new Xunit.Sdk.XunitException(
                            "Unexpected extractor request."
                        );
                return new CompletionResult(
                    message,
                    CompletionDescriptor.From(this, request)
                );
            }
            finally {
                Interlocked.Decrement(ref _active);
            }
        }

        private void UpdateMaximum(int candidate) {
            int current;
            while (candidate > (current = Volatile.Read(
                       ref _maximumActive))) {
                if (Interlocked.CompareExchange(
                        ref _maximumActive,
                        candidate,
                        current) == current) {
                    return;
                }
            }
        }
    }

    private sealed class BlockingClient : ICompletionClient {
        private int _activeCalls;
        private int _cancellationObserved;

        public string Name => "blocking-extractor";
        public string ApiSpecId => "test-v1";
        internal int ActiveCalls => Volatile.Read(ref _activeCalls);
        internal bool CancellationObserved =>
            Volatile.Read(ref _cancellationObserved) != 0;
        internal TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        internal TaskCompletionSource Drained { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public async Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = request;
            _ = observer;
            Interlocked.Increment(ref _activeCalls);
            Entered.TrySetResult();
            try {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new Xunit.Sdk.XunitException(
                    "Infinite delay unexpectedly completed."
                );
            }
            catch (OperationCanceledException) {
                Interlocked.Exchange(ref _cancellationObserved, 1);
                throw;
            }
            finally {
                if (Interlocked.Decrement(ref _activeCalls) == 0) {
                    Drained.TrySetResult();
                }
            }
        }
    }

    private sealed class CancellationCallbackFailureClient(
        Exception callbackFailure
    ) : ICompletionClient {
        private int _activeCalls;

        public string Name => "cancellation-callback-failure";
        public string ApiSpecId => "test-v1";
        internal int ActiveCalls => Volatile.Read(ref _activeCalls);
        internal TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        internal TaskCompletionSource Drained { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public async Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = request;
            _ = observer;
            Interlocked.Increment(ref _activeCalls);
            using CancellationTokenRegistration registration =
                cancellationToken.Register(() => throw callbackFailure);
            Entered.TrySetResult();
            try {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken
                );
                throw new Xunit.Sdk.XunitException(
                    "Infinite delay unexpectedly completed."
                );
            }
            finally {
                if (Interlocked.Decrement(ref _activeCalls) == 0) {
                    Drained.TrySetResult();
                }
            }
        }
    }

    private sealed class FatalOnCancellationClient(Exception fatal)
        : ICompletionClient {
        private int _activeCalls;

        public string Name => "fatal-on-cancellation";
        public string ApiSpecId => "test-v1";
        internal int ActiveCalls => Volatile.Read(ref _activeCalls);
        internal TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        internal TaskCompletionSource Drained { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public async Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = request;
            _ = observer;
            Interlocked.Increment(ref _activeCalls);
            Entered.TrySetResult();
            try {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken
                );
                throw new Xunit.Sdk.XunitException(
                    "Infinite delay unexpectedly completed."
                );
            }
            catch (OperationCanceledException) {
                throw fatal;
            }
            finally {
                if (Interlocked.Decrement(ref _activeCalls) == 0) {
                    Drained.TrySetResult();
                }
            }
        }
    }

    private sealed class FailAfterSignalClient(
        Task signal,
        Exception? failure = null
    )
        : ICompletionClient {
        public string Name => "mail-failure";
        public string ApiSpecId => "test-v1";

        public async Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = request;
            _ = observer;
            await signal.WaitAsync(cancellationToken);
            throw failure ?? new IOException("mail extractor unavailable");
        }
    }

    private sealed class GatedNoteClient : ICompletionClient {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public string Name => "gated-note";
        public string ApiSpecId => "test-v1";
        internal TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        internal void Release() => _release.TrySetResult();

        public async Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            Entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new CompletionResult(
                Message(NoteTool()),
                CompletionDescriptor.From(this, request)
            );
        }
    }

    private sealed class GatedMessageClient(ActionMessage message)
        : ICompletionClient {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public string Name => "gated-message";
        public string ApiSpecId => "test-v1";
        internal TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        internal void Release() => _release.TrySetResult();

        public async Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            Entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new CompletionResult(
                message,
                CompletionDescriptor.From(this, request)
            );
        }
    }

    private sealed class QueueClient(params ActionMessage[] messages)
        : ICompletionClient {
        private readonly Queue<ActionMessage> _messages = new(messages);
        private readonly object _gate = new();
        private int _dispatchCount;

        public string Name => "queued-runtime";
        public string ApiSpecId => "test-v1";
        internal int DispatchCount => Volatile.Read(ref _dispatchCount);

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            ActionMessage message;
            lock (_gate) {
                message = _messages.Dequeue();
            }
            Interlocked.Increment(ref _dispatchCount);
            foreach (ActionBlock.Text text in message.Blocks
                         .OfType<ActionBlock.Text>()) {
                observer?.OnTextDelta(text.Content);
            }
            return Task.FromResult(new CompletionResult(
                message,
                CompletionDescriptor.From(this, request)
            ));
        }
    }

    private sealed class RoutingFactory(
        IReadOnlyDictionary<string, ICompletionClient> clients
    ) : ICompletionClientFactory {
        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) => clients[connection.Id];
    }
}
