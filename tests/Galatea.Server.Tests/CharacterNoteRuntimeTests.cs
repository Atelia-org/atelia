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
using Atelia.MdJson;
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

    [Fact]
    public async Task SharedClientOverlapsMailAndNoteThenReceiptAttachesOnce() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig helper = Connection("helper");
        var mainClient = new QueueClient(
            Message(new ActionBlock.Text(Action)),
            Message(new ActionBlock.Text("Receipt acknowledged.")),
            Message(new ActionBlock.Text("Continue."))
        );
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
            connectionOptionIds: [main.Id],
            outboundMailExtractorConnectionId: helper.Id,
            characterNoteExtractorConnectionId: helper.Id
        );
        (GalateaHostService service, CharacterSessionHost session) =
            await GetRuntimeAsync(host);

        await session.TurnLock.WaitAsync();
        try {
            GalateaLiveTurn turn = service.StartTurn(
                session,
                "first",
                new GalateaTurnOptions(main.Id),
                sender: GalateaDelegateTestConfiguration.PlayerSender
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
            Assert.NotNull(session.CharacterMemoryReconciler!.ReadPendingReceiptDelivery());
            global::Atelia.MemoPod.MemoPod saved =
                global::Atelia.MemoPod.MemoPod.Open(
                    session.Character.CharacterMemoryStateDir,
                    CharacterNoteDefaultPodV1.PodId
                );
            Assert.Equal(MemoPodPhase.Frozen, saved.Phase);
            Assert.Equal(NoteText, Assert.Single(saved.List()).ExactText);
            await service.ReconcileDurableAdmissionAsync(
                session,
                CancellationToken.None
            );
            Assert.Equal(2, helperClient.Requests.Count);
            Assert.NotNull(session.CharacterMemoryReconciler!.ReadPendingReceiptDelivery());

            GalateaLiveTurn receiptTurn = service.StartTurn(
                session,
                "second",
                new GalateaTurnOptions(main.Id),
                sender: GalateaDelegateTestConfiguration.PlayerSender
            );
            Assert.NotNull(session.CharacterMemoryReconciler!.ReadPendingReceiptDelivery());
            await service.RunTurnAsync(session, receiptTurn, CancellationToken.None);
            PlayerTurnObservation receiptInput = ReadLatestObservation(session);
            PlayerTurnNotice.NoteSaveReceipt receiptNotice = Assert.IsType<PlayerTurnNotice.NoteSaveReceipt>(
                Assert.Single(receiptInput.Notices)
            );
            Assert.Equal(NoteText, Assert.Single(Assert.IsType<CharacterNoteReceiptSelection>(receiptNotice.Selection).ExactTexts));
            SessionInputContent storedReceiptInput = Assert.Single(session.Engine.ReadRecentCompletedTurns(1)
                .RequireSnapshot().Turns).ObservationContent;
            Assert.Equal(GalateaInputProjector.Instance.Project(storedReceiptInput),
                Assert.IsType<CompletionRequest>(mainClient.LastRequest).PromptPrefix.SharedContextMessages
                    .OfType<ObservationMessage>().Last().Content);
            Assert.Null(session.CharacterMemoryReconciler?.ReadPendingReceiptDelivery());
            service.FinishTurn(session, receiptTurn);

            GalateaLiveTurn next = service.StartTurn(
                session,
                "third",
                new GalateaTurnOptions(main.Id),
                sender: GalateaDelegateTestConfiguration.PlayerSender
            );
            await service.RunTurnAsync(session, next, CancellationToken.None);
            Assert.Empty(ReadLatestObservation(session).Notices);
            service.FinishTurn(session, next);
        }
        finally {
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task SecondTurnOriginBarrierBlocksSavedMemoAndKeepsUnrelatedRecall() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig note = Connection("note");
        var recallProvider = new NewlySavedMemoRecallProvider();
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(
                    Message(new ActionBlock.Text(Action)),
                    Message(new ActionBlock.Text("second reply"))
                ),
                [note.Id] = new QueueClient(
                    Message(NoteTool()),
                    Message()
                ),
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, note],
            connectionOptionIds: [main.Id],
            characterNoteExtractorConnectionId: note.Id,
            playerTurnRecallProviderFactory: (_, _) => recallProvider
        );
        (GalateaHostService service, CharacterSessionHost session) =
            await GetRuntimeAsync(host);

        await session.TurnLock.WaitAsync();
        try {
            GalateaLiveTurn first = service.StartTurn(
                session,
                "first",
                new GalateaTurnOptions(main.Id),
                sender: GalateaDelegateTestConfiguration.PlayerSender
            );
            await service.RunTurnAsync(
                    session,
                    first,
                    CancellationToken.None
                )
                .WaitAsync(Deadline);
            service.FinishTurn(session, first);
            Assert.Equal(NoteText, Assert.Single(
                global::Atelia.MemoPod.MemoPod.Open(
                    session.Character.CharacterMemoryStateDir,
                    CharacterNoteDefaultPodV1.PodId
                ).List()
            ).ExactText);

            GalateaLiveTurn second = service.StartTurn(
                session,
                "second",
                new GalateaTurnOptions(main.Id),
                sender: GalateaDelegateTestConfiguration.PlayerSender
            );
            await service.RunTurnAsync(
                    session,
                    second,
                    CancellationToken.None
                )
                .WaitAsync(Deadline);
            service.FinishTurn(session, second);

            Assert.Equal(2, recallProvider.Requests.Count);
            Assert.False(recallProvider.Requests[0]
                .Context.CharacterNoteOriginBarrier.Contains(
                    CharacterNoteDefaultPodV1.PodId,
                    MemoId.Parse("m1:00000001")
                ));
            Assert.True(recallProvider.Requests[1]
                .Context.CharacterNoteOriginBarrier.Contains(
                    CharacterNoteDefaultPodV1.PodId,
                    MemoId.Parse("m1:00000001")
                ));
            Assert.Equal([0, 1], recallProvider.ReturnedCounts);
            SessionCompletedTurnProjection latest = session.Engine
                .ReadRecentCompletedTurns(1)
                .RequireSnapshot()
                .Turns
                .Single();
            Assert.True(latest.ObservationContent.IsStructured);
            PlayerTurnObservation observation = GalateaObservationContent.ReadPlayerTurn(latest.ObservationContent);
            Assert.Equal(
                NewlySavedMemoRecallProvider.UnrelatedCandidate,
                Assert.Single(observation.Recalls)
            );
        }
        finally {
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task DiagnosticSinkFollowsBuildConfigurationAndDebugPayloadBoundary() {
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
                    ExactText
                ))),
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, mail, note],
            connectionOptionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, CharacterSessionHost session) =
            await GetRuntimeAsync(host);
        var diagnostics = new List<string>();
        service.CharacterNoteDiagnosticSinkForTest = diagnostics.Add;

        await session.TurnLock.WaitAsync();
        try {
            GalateaLiveTurn turn = service.StartTurn(
                session,
                "diagnostics",
                new GalateaTurnOptions(main.Id),
                sender: GalateaDelegateTestConfiguration.PlayerSender
            );
            await service.RunTurnAsync(
                    session,
                    turn,
                    CancellationToken.None
                )
                .WaitAsync(Deadline);
            service.FinishTurn(session, turn);

            AssertCharacterNoteDiagnosticsForBuild(diagnostics, observed => {
                string artifactJson = Assert.Single(DiagnosticEvents(
                    observed,
                    "character-note-durable-memo"
                ));
                string batchJson = Assert.Single(DiagnosticEvents(
                    observed,
                    "character-note-extraction-batch"
                ));
                Assert.All(observed, static json => {
                    Assert.DoesNotContain('\r', json);
                    Assert.DoesNotContain('\n', json);
                });
                Assert.Contains("\\n", artifactJson,
                    StringComparison.Ordinal);
                using (JsonDocument artifact = JsonDocument.Parse(
                    artifactJson
                )) {
                    Assert.Equal(
                        ExactText,
                        artifact.RootElement.GetProperty("exactText")
                            .GetString()
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
                        "durable-pending",
                        batch.RootElement.GetProperty("receiptOutcome")
                            .GetString()
                    );
                    Assert.True(batch.RootElement.GetProperty("mailMs")
                        .GetInt64() >= 0);
                    Assert.True(batch.RootElement.GetProperty("noteMs")
                        .GetInt64() >= 0);
                }
                Assert.All(observed, json => {
                    Assert.DoesNotContain(ActionMarker, json,
                        StringComparison.Ordinal);
                    Assert.DoesNotContain("sensitive-endpoint", json,
                        StringComparison.Ordinal);
                    Assert.DoesNotContain("sensitive-secret", json,
                        StringComparison.Ordinal);
                });
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
            connectionOptionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, CharacterSessionHost session) =
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
                new GalateaTurnOptions(main.Id),
                sender: GalateaDelegateTestConfiguration.PlayerSender
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
            Assert.Null(session.CharacterMemoryReconciler?.ReadPendingReceiptDelivery());
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
                    session.Character.CharacterMemoryStateDir,
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
                    session.Character.CharacterMemoryStateDir,
                    CharacterNoteDefaultPodV1.PodId
                ).List());
            }
            if (outcome == NoteOutcome.Timeout) {
                Assert.True(noteClient.CancellationObserved);
            }
            AssertCharacterNoteDiagnosticsForBuild(diagnostics, observed => {
                AssertNoArtifactDiagnostics(observed);
                string batchJson = Assert.Single(DiagnosticEvents(
                    observed,
                    "character-note-extraction-batch"
                ));
                Assert.DoesNotContain("note provider unavailable", batchJson,
                    StringComparison.Ordinal);
                Assert.DoesNotContain("http://localhost:8000", batchJson,
                    StringComparison.Ordinal);
                Assert.DoesNotContain("test-key", batchJson,
                    StringComparison.Ordinal);
            });
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
            connectionOptionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, CharacterSessionHost session) =
            await GetRuntimeAsync(host);
        service.CharacterNoteExtractionDeadlineForTest =
            TimeSpan.FromMilliseconds(100);

        await session.TurnLock.WaitAsync();
        try {
            GalateaLiveTurn turn = service.StartTurn(
                session,
                "slow mail",
                new GalateaTurnOptions(main.Id),
                sender: GalateaDelegateTestConfiguration.PlayerSender
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
            Assert.NotNull(session.CharacterMemoryReconciler!.ReadPendingReceiptDelivery());
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
            connectionOptionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, CharacterSessionHost session) =
            await GetRuntimeAsync(host);

        await session.TurnLock.WaitAsync();
        GalateaLiveTurn turn = service.StartTurn(
            session,
            "mail fails after note apply",
            new GalateaTurnOptions(main.Id),
            sender: GalateaDelegateTestConfiguration.PlayerSender
        );
        try {
            Task run = service.RunTurnAsync(
                session,
                turn,
                CancellationToken.None
            );
            await WaitUntilAsync(() => session.CharacterMemoryReconciler!
                .ReadPendingReceiptDelivery() is not null);
            releaseMail.TrySetResult();

            Exception? observed = await Record.ExceptionAsync(() => run);
            Assert.Same(expected, observed);
            Assert.Equal(1, noteClient.DispatchCount);
            Assert.NotNull(session.CharacterMemoryReconciler!.ReadPendingReceiptDelivery());
            Assert.Equal(NoteText, Assert.Single(
                global::Atelia.MemoPod.MemoPod.Open(
                    session.Character.CharacterMemoryStateDir,
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
            connectionOptionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, CharacterSessionHost session) =
            await GetRuntimeAsync(host);

        await session.TurnLock.WaitAsync();
        GalateaLiveTurn turn = service.StartTurn(
            session,
            "double failure",
            new GalateaTurnOptions(main.Id),
            sender: GalateaDelegateTestConfiguration.PlayerSender
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
            Assert.Null(session.CharacterMemoryReconciler?.ReadPendingReceiptDelivery());
        }
        finally {
            service.FinishTurn(session, turn);
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task NoteDeadlineOceBeforeMailFailureRetainsBothSlots() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig mail = Connection("mail");
        CompletionConnectionConfig note = Connection("note");
        var releaseMail = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var noteClient = new OutcomeNoteClient(NoteOutcome.Timeout);
        var expectedMail = new GalateaTurnException(
            "mail after Note deadline",
            "mail-after-note-deadline"
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
                [note.Id] = noteClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, mail, note],
            connectionOptionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, CharacterSessionHost session) =
            await GetRuntimeAsync(host);
        service.CharacterNoteExtractionDeadlineForTest =
            TimeSpan.FromMilliseconds(100);

        await session.TurnLock.WaitAsync();
        GalateaLiveTurn turn = service.StartTurn(
            session,
            "deadline then mail failure",
            new GalateaTurnOptions(main.Id),
            sender: GalateaDelegateTestConfiguration.PlayerSender
        );
        try {
            Task run = service.RunTurnAsync(
                session,
                turn,
                CancellationToken.None
            );
            await WaitUntilAsync(() => noteClient.CancellationObserved);
            releaseMail.TrySetResult();

            GalateaTurnException observed = await Assert.ThrowsAsync<
                GalateaTurnException>(() => run);
            Assert.Equal("mail-after-note-deadline", observed.FailureReason);
            AggregateException ordered = Assert.IsType<AggregateException>(
                observed.InnerException
            );
            Assert.Collection(
                ordered.InnerExceptions,
                failure => Assert.Same(expectedMail, failure),
                failure => Assert.IsAssignableFrom<
                    OperationCanceledException>(failure)
            );
            Assert.Null(session.CharacterMemoryReconciler?.ReadPendingReceiptDelivery());
        }
        finally {
            releaseMail.TrySetResult();
            service.FinishTurn(session, turn);
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task SameExceptionInstanceInMailAndNoteRetainsMailSlot() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig mail = Connection("mail");
        CompletionConnectionConfig note = Connection("note");
        var shared = new GalateaTurnException(
            "shared failure instance",
            "shared-slot-failure"
        );
        var noteClient = new ThrowingSignalClient(shared);
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(Message(
                    new ActionBlock.Text(Action)
                )),
                [mail.Id] = new FailAfterSignalClient(
                    noteClient.Entered.Task,
                    shared
                ),
                [note.Id] = noteClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, mail, note],
            connectionOptionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, CharacterSessionHost session) =
            await GetRuntimeAsync(host);

        await session.TurnLock.WaitAsync();
        GalateaLiveTurn turn = service.StartTurn(
            session,
            "same failure slots",
            new GalateaTurnOptions(main.Id),
            sender: GalateaDelegateTestConfiguration.PlayerSender
        );
        try {
            GalateaTurnException observed = await Assert.ThrowsAsync<
                GalateaTurnException>(() => service.RunTurnAsync(
                    session,
                    turn,
                    CancellationToken.None
                ));
            Assert.Equal(
                "character-memory-state-invalid",
                observed.FailureReason
            );
            AggregateException ordered = Assert.IsType<AggregateException>(
                observed.InnerException
            );
            GalateaTurnException notePrimary = Assert.IsType<
                GalateaTurnException>(ordered.InnerExceptions[0]);
            Assert.Same(shared, notePrimary.InnerException);
            Assert.Same(shared, ordered.InnerExceptions[1]);
            Assert.Null(session.CharacterMemoryReconciler?.ReadPendingReceiptDelivery());
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
            connectionOptionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, CharacterSessionHost session) =
            await GetRuntimeAsync(host);
        var diagnostics = new List<string>();
        service.CharacterNoteDiagnosticSinkForTest = diagnostics.Add;

        await session.TurnLock.WaitAsync();
        GalateaLiveTurn turn = service.StartTurn(
            session,
            "mail failure",
            new GalateaTurnOptions(main.Id),
            sender: GalateaDelegateTestConfiguration.PlayerSender
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
            Assert.Null(session.CharacterMemoryReconciler?.ReadPendingReceiptDelivery());
            AssertCharacterNoteDiagnosticsForBuild(diagnostics, observed => {
                AssertNoArtifactDiagnostics(observed);
                string batchJson = Assert.Single(DiagnosticEvents(
                    observed,
                    "character-note-extraction-batch"
                ));
                Assert.DoesNotContain("mail extractor unavailable", batchJson,
                    StringComparison.Ordinal);
            });
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
            connectionOptionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, CharacterSessionHost session) =
            await GetRuntimeAsync(host);

        await session.TurnLock.WaitAsync();
        GalateaLiveTurn turn = service.StartTurn(
            session,
            "fatal cancellation callback",
            new GalateaTurnOptions(main.Id),
            sender: GalateaDelegateTestConfiguration.PlayerSender
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
            Assert.Equal(1, noteClient.CallbackInvocations);
            Assert.Same(expectedMail, observed.InnerExceptions[0]);
            AggregateException cancellation = Assert.IsType<
                AggregateException>(observed.InnerExceptions[1]);
            Assert.Contains(fatal, cancellation.Flatten().InnerExceptions);
            Assert.Null(session.CharacterMemoryReconciler?.ReadPendingReceiptDelivery());
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
            connectionOptionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, CharacterSessionHost session) =
            await GetRuntimeAsync(host);
        var diagnostics = new List<string>();
        service.CharacterNoteDiagnosticSinkForTest = diagnostics.Add;

        await session.TurnLock.WaitAsync();
        GalateaLiveTurn turn = service.StartTurn(
            session,
            "fatal mail failure",
            new GalateaTurnOptions(main.Id),
            sender: GalateaDelegateTestConfiguration.PlayerSender
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
            Assert.Null(session.CharacterMemoryReconciler?.ReadPendingReceiptDelivery());
            AssertCharacterNoteDiagnosticsForBuild(
                diagnostics,
                static observed => Assert.Empty(observed)
            );
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
            connectionOptionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, CharacterSessionHost session) =
            await GetRuntimeAsync(host);
        var diagnostics = new List<string>();
        service.CharacterNoteDiagnosticSinkForTest = diagnostics.Add;
        using var callerCts = new CancellationTokenSource();

        await session.TurnLock.WaitAsync();
        GalateaLiveTurn turn = service.StartTurn(
            session,
            "cancel",
            new GalateaTurnOptions(main.Id),
            sender: GalateaDelegateTestConfiguration.PlayerSender
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
            Assert.Null(session.CharacterMemoryReconciler?.ReadPendingReceiptDelivery());
            AssertCharacterNoteDiagnosticsForBuild(
                diagnostics,
                static observed => AssertNoArtifactDiagnostics(observed)
            );
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
            connectionOptionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, CharacterSessionHost session) =
            await GetRuntimeAsync(host);

        using var callerCts = new CancellationTokenSource();
        await session.TurnLock.WaitAsync();
        GalateaLiveTurn turn = service.StartTurn(
            session,
            "caller cancel with fatal Note",
            new GalateaTurnOptions(main.Id),
            sender: GalateaDelegateTestConfiguration.PlayerSender
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
            Assert.Null(session.CharacterMemoryReconciler?.ReadPendingReceiptDelivery());
        }
        finally {
            service.FinishTurn(session, turn);
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task HeadChangeBeforeNoteCaptureCreatesNoReceipt() {
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
            connectionOptionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, CharacterSessionHost session) =
            await GetRuntimeAsync(host);
        var diagnostics = new List<string>();
        service.CharacterNoteDiagnosticSinkForTest = diagnostics.Add;

        await session.TurnLock.WaitAsync();
        GalateaLiveTurn turn = service.StartTurn(
            session,
            "head fence",
            new GalateaTurnOptions(main.Id),
            sender: GalateaDelegateTestConfiguration.PlayerSender
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
            Assert.Null(session.CharacterMemoryReconciler?.ReadPendingReceiptDelivery());
            AssertCharacterNoteDiagnosticsForBuild(diagnostics, observed => {
                AssertNoArtifactDiagnostics(observed);
                string batchJson = Assert.Single(DiagnosticEvents(
                    observed,
                    "character-note-extraction-batch"
                ));
                using JsonDocument batch = JsonDocument.Parse(batchJson);
                Assert.Equal(
                    "head-changed",
                    batch.RootElement.GetProperty("receiptOutcome").GetString()
                );
            });
        }
        finally {
            noteClient.Release();
            service.FinishTurn(session, turn);
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task HeadChangeOutranksNonFatalMailAndPreservesAppliedReceipt() {
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
            connectionOptionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, CharacterSessionHost session) =
            await GetRuntimeAsync(host);

        await session.TurnLock.WaitAsync();
        GalateaLiveTurn turn = service.StartTurn(
            session,
            "head authority",
            new GalateaTurnOptions(main.Id),
            sender: GalateaDelegateTestConfiguration.PlayerSender
        );
        try {
            Task run = service.RunTurnAsync(
                session,
                turn,
                CancellationToken.None
            );
            await WaitUntilAsync(() => session.CharacterMemoryReconciler!
                .ReadPendingReceiptDelivery() is not null);
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
            Assert.NotNull(session.CharacterMemoryReconciler!.ReadPendingReceiptDelivery());
        }
        finally {
            releaseMail.TrySetResult();
            service.FinishTurn(session, turn);
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task PendingCaptureSurvivesRewindAndAdmissionCreatesDurableReceiptWithoutProvider() {
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
            connectionOptionIds: [main.Id],
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

        (GalateaHostService service, CharacterSessionHost session) =
            await GetRuntimeAsync(host);

        Assert.Equal(0, noteClient.DispatchCount);
        Assert.NotNull(session.CharacterMemoryReconciler!.ReadStatusSnapshot().ActiveCapture);
        await session.TurnLock.WaitAsync();
        try {
            await service.ReconcileDurableAdmissionAsync(session, CancellationToken.None);
        }
        finally { session.TurnLock.Release(); }
        Assert.NotNull(session.CharacterMemoryReconciler!.ReadPendingReceiptDelivery());
        Assert.Null(session.CharacterMemoryReconciler!
            .ReadStatusSnapshot().ActiveCapture);
        Assert.Equal(NoteText, Assert.Single(
            global::Atelia.MemoPod.MemoPod.Open(
                session.Character.CharacterMemoryStateDir,
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
            Assert.NotNull(session.CharacterMemoryReconciler!.ReadPendingReceiptDelivery());
        }
        finally {
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task StartingAndDiscardingTurnsDoesNotConsumeDurableReceipt() {
        CompletionConnectionConfig main = Connection("test");
        await using GalateaTestHost host = CreateReceiptHost(main);
        (GalateaHostService service, CharacterSessionHost session) =
            await GetRuntimeAsync(host);
        await SaveNoteAsync(service, session, main.Id);

        await session.TurnLock.WaitAsync();
        try {
            Assert.IsType<GalateaReadyReplyTurnStartResult.Empty>(
                service.StartReadyReplyTurn(
                    session,
                    new GalateaTurnOptions(main.Id)
                )
            );
            Assert.NotNull(session.CharacterMemoryReconciler!.ReadPendingReceiptDelivery());

            GalateaLiveTurn inbound = service.StartInboundMailTurn(
                session,
                MailboxMessage.CreateInbound(
                    session.Character.CharacterName,
                    "outside",
                    null,
                    "body"
                ),
                new GalateaTurnOptions(main.Id)
            );
            Assert.NotNull(session.CharacterMemoryReconciler!.ReadPendingReceiptDelivery());
            service.FinishTurn(session, inbound);

            GalateaLiveTurn recovery = service.StartRecovery(
                session,
                new GalateaTurnOptions(
                    main.Id,
                    GalateaTurnMode.Resume
                )
            );
            Assert.NotNull(session.CharacterMemoryReconciler!.ReadPendingReceiptDelivery());
            service.FinishTurn(session, recovery);

            GalateaLiveTurn ordinary = service.StartTurn(
                session,
                "ordinary",
                new GalateaTurnOptions(main.Id),
                sender: GalateaDelegateTestConfiguration.PlayerSender
            );
            Assert.NotNull(session.CharacterMemoryReconciler!.ReadPendingReceiptDelivery());
            service.FinishTurn(session, ordinary);
            Assert.NotNull(session.CharacterMemoryReconciler!.ReadPendingReceiptDelivery());
        }
        finally {
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task PlayerTurnWithReplyDeliversDurableReceiptInSameObservation() {
        CompletionConnectionConfig main = Connection("test");
        var transport = new ReadyReplyTransport();
        await using GalateaTestHost host = CreateReceiptHost(main, transport);
        (GalateaHostService service, CharacterSessionHost session) =
            await GetRuntimeAsync(host);
        await SaveNoteAsync(service, session, main.Id);
        await ProduceReadyReplyAsync(session);
        Assert.Equal(1, transport.StartCount);

        await session.TurnLock.WaitAsync();
        try {
            GalateaLiveTurn turn = service.StartTurn(
                session,
                "ordinary with reply",
                new GalateaTurnOptions(main.Id),
                sender: GalateaDelegateTestConfiguration.PlayerSender
            );
            GalateaFreshInput.PlayerAction input = Assert.IsType<
                GalateaFreshInput.PlayerAction>(turn.FreshInput);
            Assert.IsType<PlayerTurnNotice.Reply>(
                Assert.Single(input.Notices)
            );
            Assert.NotNull(session.CharacterMemoryReconciler!.ReadPendingReceiptDelivery());
            await service.RunTurnAsync(session, turn, CancellationToken.None);
            service.FinishTurn(session, turn);
            PlayerTurnObservation observation = ReadLatestObservation(session);
            Assert.Single(observation.Notices.OfType<PlayerTurnNotice.Reply>());
            CharacterNoteReceiptSelection receipt = Assert.IsType<CharacterNoteReceiptSelection>(
                Assert.Single(observation.Notices.OfType<PlayerTurnNotice.NoteSaveReceipt>()).Selection);
            Assert.Equal(NoteText, Assert.Single(receipt.ExactTexts));
            Assert.Equal("m1:00000001", Assert.Single(receipt.MemoIds).Value);
            Assert.Null(session.CharacterMemoryReconciler!.ReadPendingReceiptDelivery());
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
            connectionOptionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, CharacterSessionHost session) =
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
            Assert.NotNull(session.CharacterMemoryReconciler!.ReadPendingReceiptDelivery());
            Assert.Equal(NoteText, Assert.Single(
                global::Atelia.MemoPod.MemoPod.Open(
                    session.Character.CharacterMemoryStateDir,
                    CharacterNoteDefaultPodV1.PodId
                ).List()
            ).ExactText);
        }
        finally {
            session.TurnLock.Release();
        }
    }

    private static async Task<(GalateaHostService Service,
        CharacterSessionHost Session)> GetRuntimeAsync(GalateaTestHost host) {
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await service.GetSessionAsync(
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

    private static void AssertCharacterNoteDiagnosticsForBuild(
        IReadOnlyList<string> diagnostics,
        Action<IReadOnlyList<string>> assertDebugDiagnostics
    ) {
#if DEBUG
        assertDebugDiagnostics(diagnostics);
#else
        Assert.Empty(diagnostics);
#endif
    }

    private static void AssertNoArtifactDiagnostics(
        IEnumerable<string> diagnostics
    ) => Assert.Empty(DiagnosticEvents(
        diagnostics,
        "character-note-durable-memo"
    ));

    private static GalateaTestHost CreateReceiptHost(
        CompletionConnectionConfig main, IGalateaDurableDelegateTransport? delegateTransport = null
    ) {
        CompletionConnectionConfig note = Connection("note");
        return GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(StringComparer.Ordinal) {
                [main.Id] = new QueueClient(
                    Message(new ActionBlock.Text(Action)),
                    Message(new ActionBlock.Text("Receipt acknowledged."))),
                // Save Action, imported delegate source reconciled at admission,
                // and the final receipt acknowledgement each have their own extraction.
                [note.Id] = new QueueClient(Message(NoteTool()), Message(), Message()),
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, note],
            connectionOptionIds: [main.Id],
            characterNoteExtractorConnectionId: note.Id,
            delegateTransport: delegateTransport);
    }

    private static async Task SaveNoteAsync(
        GalateaHostService service, CharacterSessionHost session, string connectionId
    ) {
        await session.TurnLock.WaitAsync();
        GalateaLiveTurn turn = service.StartTurn(session, "save", new(connectionId), sender: GalateaDelegateTestConfiguration.PlayerSender);
        try {
            await service.RunTurnAsync(session, turn, CancellationToken.None).WaitAsync(Deadline);
        }
        finally {
            service.FinishTurn(session, turn);
            session.TurnLock.Release();
        }
        Assert.NotNull(session.CharacterMemoryReconciler!.ReadPendingReceiptDelivery());
    }

    private static PlayerTurnObservation ReadLatestObservation(CharacterSessionHost session) {
        SessionInputContent content = Assert.Single(session.Engine.ReadRecentCompletedTurns(1)
            .RequireSnapshot().Turns).ObservationContent;
        Assert.True(content.IsStructured);
        return GalateaObservationContent.ReadPlayerTurn(content);
    }

    private static async Task ProduceReadyReplyAsync(CharacterSessionHost session) {
        const string VisibleAction = "ready reply source";
        GalateaDelegationSqliteStore store = session.DelegationHandle!.Store;
        string sourceAction = EventAddressTextCodec.Format(
            AppendAction(session.Engine, VisibleAction)
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
                )],
                new GalateaSenderSnapshot("character", session.Character.CharacterId, session.Character.CharacterName.Value)
            )
        );
        string dispatchId = Assert.Single(captured.DispatchIds);
        // The supervisor is the only driver of the mail state machine. Manual
        // Started/Completed writes raced its recovery pulse and invalidated CAS.
        session.DelegationHandle!.Signal();
        await WaitUntilAsync(() => store.ReadSnapshot().Notices.Any(value =>
            value.DispatchId == dispatchId && value.State == GalateaReplyNoticeState.Ready));
        GalateaReplyNoticeSnapshot notice = Assert.Single(store.ReadSnapshot().Notices);
        Assert.Equal(GalateaReplyNoticeKind.Reply, notice.Kind);
        Assert.Equal("ready reply", notice.Body);
    }

    private sealed class ReadyReplyTransport : IGalateaDurableDelegateTransport {
        private int _startCount;
        internal int StartCount => Volatile.Read(ref _startCount);

        public Task<GalateaDelegateBindingEstablished> EnsureBindingAsync(
            GalateaEnsureDelegateBindingRequest request, CancellationToken ct
        ) => Task.FromResult(new GalateaDelegateBindingEstablished(
            request.BindingOperationId, "runtime-test-thread"));

        public Task<GalateaDelegateTurnAccepted> StartTurnAsync(
            GalateaStartDelegateTurnRequest request, CancellationToken ct
        ) {
            JsonElement task = MdJsonSerializer.Read(request.Task);
            Assert.Equal("task", task.GetProperty("body").GetString());
            Assert.Equal("character", task.GetProperty("sender").GetProperty("kind").GetString());
            Assert.Equal("alice", task.GetProperty("sender").GetProperty("id").GetString());
            Interlocked.Increment(ref _startCount);
            return Task.FromResult(new GalateaDelegateTurnAccepted(
                request.DispatchId, request.ThreadId, "runtime-test-turn"));
        }

        public Task<GalateaDelegateDispatchInspection> InspectDispatchAsync(
            GalateaInspectDelegateDispatchRequest request, CancellationToken ct
        ) {
            Assert.Equal("runtime-test-turn", request.ExpectedTurnId);
            return Task.FromResult<GalateaDelegateDispatchInspection>(
                new GalateaDelegateDispatchInspection.Completed(request.DispatchId,
                    request.ThreadId, "runtime-test-turn", "ready reply", GalateaDelegateInspectionSource.Live));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
        string text = NoteText
    ) => new(new RawToolCall(
        CharacterNoteExtractor.ToolName,
        "note-call",
        JsonSerializer.Serialize(new { text })
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
                    message = Message(NoteTool(" "));
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
            if (!Assert.IsType<string>(Assert.IsType<ObservationMessage>(
                    Assert.Single(request.TailMessages)).Content)
                .Contains(Action, StringComparison.Ordinal)) {
                return new CompletionResult(Message(), CompletionDescriptor.From(this, request));
            }
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
        private int _callbackInvocations;

        public string Name => "cancellation-callback-failure";
        public string ApiSpecId => "test-v1";
        internal int ActiveCalls => Volatile.Read(ref _activeCalls);
        internal int CallbackInvocations => Volatile.Read(ref _callbackInvocations);
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
            var canceled = new TaskCompletionSource<CompletionResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            // One callback owns both cancellation and the injected failure.
            // A separate Task.Delay registration could cancel first and let
            // its continuation dispose this callback before it ever executes.
            using CancellationTokenRegistration registration =
                cancellationToken.Register(() => {
                    Interlocked.Increment(ref _callbackInvocations);
                    canceled.TrySetCanceled(cancellationToken);
                    throw callbackFailure;
                });
            Entered.TrySetResult();
            try {
                return await canceled.Task;
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

    private sealed class ThrowingSignalClient(Exception failure)
        : ICompletionClient {
        public string Name => "throwing-signal";
        public string ApiSpecId => "test-v1";
        internal TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = request;
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            Entered.TrySetResult();
            return Task.FromException<CompletionResult>(failure);
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
        internal CompletionRequest? LastRequest { get; private set; }

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            ActionMessage message;
            lock (_gate) {
                message = _messages.Dequeue();
                LastRequest = request;
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

    private sealed class NewlySavedMemoRecallProvider
        : IGalateaPlayerTurnRecallProvider {
        private static readonly MemoId CandidateMemoId =
            MemoId.Parse("m1:00000001");
        private static readonly MemoId UnrelatedMemoId =
            MemoId.Parse("m1:00000099");
        private static readonly PlayerTurnRecall SavedCandidate = new(
            new RecallEntry(
                RecallType.MemoExactText,
                GalateaMemoRecallSourceIdCodec.Format(CharacterNoteDefaultPodV1.PodId, CandidateMemoId)
            ),
            CharacterNoteDefaultPodV1.EmptyStateIdentity,
            "Saved candidate title",
            NoteText
        );
        internal static PlayerTurnRecall UnrelatedCandidate { get; } = PlayerTurnRecall.FromText(
            new RecallEntry(
                RecallType.MemoSummary,
                "unrelated-test-candidate"
            ),
            "unrelated-version-1",
            "unrelated summary"
        );

        internal List<GalateaPlayerTurnRecallRequest> Requests { get; } = [];
        internal List<int> ReturnedCounts { get; } = [];

        public ValueTask<IReadOnlyList<PlayerTurnRecall>> SelectRecallsAsync(
            GalateaPlayerTurnRecallRequest request,
            CancellationToken cancellationToken
        ) {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            bool candidateAvailable = Requests.Count > 1;
            bool blocked = request.Context.CharacterNoteOriginBarrier.Contains(
                CharacterNoteDefaultPodV1.PodId,
                CandidateMemoId
            );
            bool unrelatedBlocked = request.Context.CharacterNoteOriginBarrier
                .Contains(
                    CharacterNoteDefaultPodV1.PodId,
                    UnrelatedMemoId
                );
            var selected = new List<PlayerTurnRecall>();
            if (candidateAvailable && !blocked) {
                selected.Add(SavedCandidate);
            }
            if (candidateAvailable && !unrelatedBlocked) {
                selected.Add(UnrelatedCandidate);
            }
            IReadOnlyList<PlayerTurnRecall> returned =
                Array.AsReadOnly(selected.ToArray());
            ReturnedCounts.Add(returned.Count);
            return ValueTask.FromResult(returned);
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
