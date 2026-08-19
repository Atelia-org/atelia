using System.Collections.Immutable;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionSupplementalContextIntegrationTests : IDisposable {
    private const string SelectedPrefix =
        "{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"selected\",\"observationContent\":\"";
    private const string SelectedSuffix = "\"}";
    private readonly List<string> _tempDirectories = [];

    public void Dispose() {
        foreach (string path in _tempDirectories) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch {
                // Best-effort cleanup for test-owned temporary directories.
            }
        }
    }

    [Fact]
    public void PublicContracts_ValidateRequiredExactValuesWithoutNormalization() {
        EventAddress address = EventAddressTextCodec.Parse(
            "ej1:00000000000000010000000100000000"
        );
        const string exact = "  exact observation\r\n";

        var request = new SessionSupplementalContextRequest(address, exact);
        var selected = new SessionSupplementalContextSelection.Selected(exact);

        Assert.Equal(address, request.ObservationAddress);
        Assert.Equal(exact, request.ExactObservationContent);
        Assert.Equal(exact, selected.ExactObservationContent);
        Assert.Throws<ArgumentException>(
            () => new SessionSupplementalContextRequest(address, "   ")
        );
        Assert.Throws<ArgumentException>(
            () => new SessionSupplementalContextRequest(
                address,
                new string(['\ud800'])
            )
        );
        Assert.Throws<ArgumentException>(
            () => new SessionSupplementalContextSelection.Selected("")
        );
        Assert.Throws<ArgumentException>(
            () => new SessionSupplementalContextSelection.Selected(
                new string(['\ud800'])
            )
        );
    }

    [Fact]
    public async Task Selected_UsesDurableObservationAndOrdersRecapSupplementalRaw() {
        string path = NewJournalPath();
        var client = new RecordingCompletionClient();
        client.Enqueue("answer");
        var candidateSource = new TestContextCandidateSource();
        var supplemental = new TestSupplementalContextSource(
            new SessionSupplementalContextSelection.Selected(
                "selected supplemental"
            )
        );
        CompletionRequest request;
        SessionSupplementalContextRequest sourceRequest;
        using (var engine = SessionJournalTestRuntime.Attach(
            SessionJournalEngine.Create(path, CreateOptions()),
            CreateRuntime(client, candidateSource) with {
                SupplementalContextSource = supplemental
            }
        )) {
            candidateSource.Candidate = ContextCandidateTestFixture
                .CreateAtCurrentHead(engine, "supplemental-order")
                .Candidate;

            _ = await engine.SendAsync(
                "exact current observation",
                CancellationToken.None
            );

            Assert.Equal(1, supplemental.CallCount);
            sourceRequest = Assert.Single(supplemental.Requests);
            Assert.Equal(
                "exact current observation",
                sourceRequest.ExactObservationContent
            );
            request = Assert.Single(client.Requests);
        }
        Assert.Equal(
            ReadSingleAddressByKind(path, SessionEventKind.ObservationAccepted),
            sourceRequest.ObservationAddress
        );
        Assert.Collection(
            request.PromptPrefix.SharedContextMessages,
            message => Assert.Contains(
                "bounded world supplemental-order",
                Assert.IsType<ObservationMessage>(message).Content,
                StringComparison.Ordinal
            ),
            message => Assert.Contains(
                "bounded self supplemental-order",
                Assert.IsType<ActionMessage>(message).GetFlattenedText(),
                StringComparison.Ordinal
            ),
            message => Assert.Equal(
                "selected supplemental",
                Assert.IsType<ObservationMessage>(message).Content
            ),
            message => Assert.Equal(
                "exact current observation",
                Assert.IsType<ObservationMessage>(message).Content
            )
        );
        (CompletionRequestPreparedBody manifest, int schemaVersion) =
            ReadPrepared(path);
        Assert.Equal(
            SessionRequestManifestCodec.PreparedV6BodySchemaVersion,
            schemaVersion
        );
        Assert.Equal(
            SessionSupplementalContextRecipe.RecipeId,
            manifest.Recipe.RecipeId
        );
        Assert.Equal(
            SessionSupplementalContextStatus.Selected,
            SessionSupplementalContextRecipe.ValidateAndPartition(
                manifest.Plan.ExactContextInputs
            ).Control.Status
        );
    }

    [Fact]
    public async Task NoMatch_WritesV6ButAddsNoProviderFacingMessage() {
        string path = NewJournalPath();
        var client = new RecordingCompletionClient();
        client.Enqueue("answer");
        var candidateSource = new TestContextCandidateSource();
        var supplemental = new TestSupplementalContextSource(
            new SessionSupplementalContextSelection.NoMatch()
        );
        CompletionRequest request;
        using (var engine = SessionJournalTestRuntime.Attach(
            SessionJournalEngine.Create(path, CreateOptions()),
            CreateRuntime(client, candidateSource) with {
                SupplementalContextSource = supplemental
            }
        )) {
            candidateSource.Candidate = ContextCandidateTestFixture
                .CreateAtCurrentHead(engine, "no-match")
                .Candidate;

            _ = await engine.SendAsync("raw observation", CancellationToken.None);
            request = Assert.Single(client.Requests);
        }
        Assert.DoesNotContain(
            request.PromptPrefix.SharedContextMessages,
            static message => message is ObservationMessage observation
                && observation.Content?.Contains(
                    SessionSupplementalContextRecipe.ControlSchemaId,
                    StringComparison.Ordinal
                ) == true
        );
        Assert.Equal(
            "raw observation",
            Assert.IsType<ObservationMessage>(
                request.PromptPrefix.SharedContextMessages[^1]
            ).Content
        );
        (CompletionRequestPreparedBody manifest, int schemaVersion) =
            ReadPrepared(path);
        Assert.Equal(6, schemaVersion);
        SessionSupplementalContextPartition partition =
            SessionSupplementalContextRecipe.ValidateAndPartition(
                manifest.Plan.ExactContextInputs
            );
        Assert.Equal(SessionSupplementalContextStatus.NoMatch, partition.Control.Status);
    }

    [Fact]
    public async Task SourceFailureAndCancellation_LeaveObservationWithoutPreparedOrProvider() {
        foreach (bool cancel in new[] { false, true }) {
            string path = NewJournalPath();
            var client = new RecordingCompletionClient();
            var candidateSource = new TestContextCandidateSource();
            using var cts = new CancellationTokenSource();
            var supplemental = new TestSupplementalContextSource(
                new SessionSupplementalContextSelection.NoMatch()
            ) {
                Handler = (_request, _) => {
                    if (cancel) {
                        cts.Cancel();
                        return new SessionSupplementalContextSelection.NoMatch();
                    }
                    throw new IOException("supplemental source failed");
                }
            };
            using (var engine = SessionJournalTestRuntime.Attach(
                SessionJournalEngine.Create(path, CreateOptions()),
                CreateRuntime(client, candidateSource) with {
                    SupplementalContextSource = supplemental
                }
            )) {
                candidateSource.Candidate = ContextCandidateTestFixture
                    .CreateAtCurrentHead(engine, $"failure-{cancel}")
                    .Candidate;

                if (cancel) {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(
                        () => engine.SendAsync("pending observation", cts.Token)
                    );
                }
                else {
                    await Assert.ThrowsAsync<IOException>(
                        () => engine.SendAsync(
                            "pending observation",
                            CancellationToken.None
                        )
                    );
                }

                Assert.Equal(
                    SessionEventKind.ObservationAccepted,
                    engine.ResolveExecutionTail().State.HeadKind
                );
            }

            Assert.Empty(ReadAddressesByKind(
                path,
                SessionEventKind.CompletionRequestPrepared
            ));
            Assert.Empty(ReadAddressesByKind(
                path,
                SessionEventKind.CompletionAttemptStarted
            ));
            Assert.Equal(0, client.Calls);
        }
    }

    [Fact]
    public async Task PostSelectionFailpoint_ReselectsOnResumeBeforePrepared() {
        string path = NewJournalPath();
        var client = new RecordingCompletionClient();
        var candidateSource = new TestContextCandidateSource();
        var supplemental = new TestSupplementalContextSource(
            new SessionSupplementalContextSelection.NoMatch()
        );
        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            CreateOptions(),
            CreateRuntime(client, candidateSource) with {
                SupplementalContextSource = supplemental
            },
            new SessionJournalTestHooks(
                SessionJournalFailpoint.AfterSupplementalContextSelected
            )
        )) {
            candidateSource.Candidate = ContextCandidateTestFixture
                .CreateAtCurrentHead(engine, "post-selection-crash")
                .Candidate;
            await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync("pending observation", CancellationToken.None)
            );
            Assert.Equal(1, supplemental.CallCount);
        }

        Assert.Empty(ReadAddressesByKind(
            path,
            SessionEventKind.CompletionRequestPrepared
        ));

        client.Enqueue("recovered");
        using (var reopened = SessionJournalTestRuntime.Attach(
            SessionJournalEngine.Open(path),
            CreateRuntime(client, candidateSource) with {
                SupplementalContextSource = supplemental
            }
        )) {
            ResumeOutcome outcome = await reopened.ResumeAsync(
                CancellationToken.None
            );

            Assert.True(outcome.Advanced);
            Assert.Equal(2, supplemental.CallCount);
        }
        Assert.Single(ReadAddressesByKind(
            path,
            SessionEventKind.CompletionRequestPrepared
        ));
    }

    [Fact]
    public async Task SourceResult_IsRejectedWhenDurableHeadDriftsDuringCall() {
        string path = NewJournalPath();
        var client = new RecordingCompletionClient();
        var candidateSource = new TestContextCandidateSource();
        EventJournal.EventJournal? mutationJournal = null;
        SessionJournalEngine? engine = null;
        var supplemental = new TestSupplementalContextSource(
            new SessionSupplementalContextSelection.NoMatch()
        ) {
            Handler = (request, _) => {
                Assert.True(mutationJournal!.MoveRef(
                    engine!.BranchRefId,
                    request.ObservationAddress,
                    null
                ).Unwrap());
                return new SessionSupplementalContextSelection.NoMatch();
            }
        };
        engine = SessionJournalEngine.CreateForTest(
            path,
            CreateOptions(),
            CreateRuntime(client, candidateSource) with {
                SupplementalContextSource = supplemental
            },
            new SessionJournalTestHooks(
                BeforeCommit: (kind, journal) => {
                    if (kind == SessionEventKind.ObservationAccepted) {
                        mutationJournal = journal;
                    }
                }
            )
        );
        using (engine) {
            candidateSource.Candidate = ContextCandidateTestFixture
                .CreateAtCurrentHead(engine, "head-drift")
                .Candidate;

            InvalidOperationException stale =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.SendAsync(
                        "drifting observation",
                        CancellationToken.None
                    )
                );

            Assert.Contains("stale", stale.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Null(engine.ReadCurrentHead());
            Assert.Equal(1, supplemental.CallCount);
            Assert.Equal(0, client.Calls);
        }
    }

    [Fact]
    public void NoMatch_HasExactCanonicalControlAndTerminalHash() {
        SessionRequestContextInput terminal =
            SessionSupplementalContextRecipe.CreateNoMatchTerminalInput();

        Assert.Equal(
            "{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"no-match\",\"observationContent\":null}",
            terminal.ContextSnapshot.ObservationMessage
        );
        Assert.Equal("", terminal.ContextSnapshot.SystemPromptFragment);
        Assert.Equal("", terminal.ContextSnapshot.ActionMessage);
        Assert.Equal(
            SessionArtifactContextSnapshotHasher.ComputeSha256(
                terminal.ContextSnapshot
            ),
            terminal.ContentSha256
        );
        SessionSupplementalContextControl control =
            SessionSupplementalContextRecipe.ParseControl(
                terminal.ContextSnapshot.ObservationMessage
            );
        Assert.Equal(SessionSupplementalContextStatus.NoMatch, control.Status);
        Assert.Null(control.ObservationContent);
    }

    [Fact]
    public void Selected_FixedEncoderPreservesScalarsAndCanonicalizesControls() {
        const string content =
            "A\"\\\b\t\n\f\r\u0001\u007f\u0085\u2028\u2029汉😀";

        string rendered =
            SessionSupplementalContextRecipe.RenderSelectedControl(content);

        Assert.Equal(
            SelectedPrefix
                + "A\\\"\\\\\\b\\t\\n\\f\\r\\u0001\\u007f\\u0085\\u2028\\u2029汉😀"
                + SelectedSuffix,
            rendered
        );
        SessionSupplementalContextControl parsed =
            SessionSupplementalContextRecipe.ParseControl(rendered);
        Assert.Equal(SessionSupplementalContextStatus.Selected, parsed.Status);
        Assert.Equal(content, parsed.ObservationContent);
    }

    [Theory]
    [InlineData(" {\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"no-match\",\"observationContent\":null}")]
    [InlineData("{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"no-match\",\"observationContent\":null}\n")]
    [InlineData("{\"status\":\"no-match\",\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"observationContent\":null}")]
    [InlineData("{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"no-match\",\"observationContent\":null,\"extra\":0}")]
    [InlineData("{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"no-match\",\"observationContent\":null}")]
    [InlineData("{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"No-Match\",\"observationContent\":null}")]
    [InlineData("{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"no-match\",\"observationContent\":\"not-null\"}")]
    [InlineData("{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"selected\",\"observationContent\":null}")]
    [InlineData("{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"selected\",\"observationContent\":\"\"}")]
    [InlineData("{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"selected\",\"observationContent\":\"\\u0061\"}")]
    [InlineData("\ufeff{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"no-match\",\"observationContent\":null}")]
    [InlineData("{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"selected\",\"observationContent\":\"\u0085\"}")]
    [InlineData("{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"selected\",\"observationContent\":\"\\u008A\"}")]
    [InlineData("{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"selected\",\"observationContent\":\"\\uD83D\\uDE00\"}")]
    [InlineData("{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"no-match\"}")]
    [InlineData("{\"Schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"no-match\",\"observationContent\":null}")]
    [InlineData("{\"schema\":1,\"status\":\"no-match\",\"observationContent\":null}")]
    [InlineData("{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":1,\"observationContent\":null}")]
    [InlineData("{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"selected\",\"observationContent\":1}")]
    [InlineData("{\"schema\":\"atelia.session-journal.supplemental-context.control.v2\",\"status\":\"no-match\",\"observationContent\":null}")]
    [InlineData("{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"no-match\",\"observationContent\":null")]
    public void Parser_RejectsShapeAndNonCanonicalByteGrammar(string value) {
        Assert.Throws<InvalidDataException>(
            () => SessionSupplementalContextRecipe.ParseControl(value)
        );
    }

    [Fact]
    public void EncoderAndParser_RejectInvalidUnicodeScalarData() {
        string invalid = new(['\ud800']);

        Assert.Throws<ArgumentException>(
            () => SessionSupplementalContextRecipe.RenderSelectedControl(invalid)
        );
        Assert.Throws<InvalidDataException>(
            () => SessionSupplementalContextRecipe.ParseControl(
                SelectedPrefix + invalid + SelectedSuffix
            )
        );
    }

    [Fact]
    public void Encoder_AcceptsExactSnapshotBoundAndRejectsOneByteMore() {
        int carrierOverhead = Encoding.UTF8.GetByteCount(
            SelectedPrefix + SelectedSuffix
        );
        int exactContentLength =
            SessionArtifactContextSnapshotHasher.MaxSnapshotUtf8Bytes
            - carrierOverhead;
        string exact = new('a', exactContentLength);

        string rendered =
            SessionSupplementalContextRecipe.RenderSelectedControl(exact);

        Assert.Equal(
            SessionArtifactContextSnapshotHasher.MaxSnapshotUtf8Bytes,
            Encoding.UTF8.GetByteCount(rendered)
        );
        SessionSupplementalContextControl decoded =
            SessionSupplementalContextRecipe.ParseControl(rendered);
        Assert.Equal(exact, decoded.ObservationContent);
        Assert.Throws<ArgumentException>(
            () => SessionSupplementalContextRecipe.RenderSelectedControl(
                exact + "a"
            )
        );
        string overBound = SelectedPrefix + exact + "a" + SelectedSuffix;
        Assert.Throws<InvalidDataException>(
            () => SessionSupplementalContextRecipe.ParseControl(overBound)
        );
    }

    [Fact]
    public void Expand_PreservesRecapOrderAndNeverExposesTerminalEnvelope() {
        ImmutableArray<SessionRequestContextInput> recap = [
            ContextInput(new("recap system", "", "")),
            ContextInput(new("", "recap observation", "")),
            ContextInput(new("", "", "recap action"))
        ];
        CompletionRequestPreparedBody manifest = PreparedV6Fixture.Create(
            "exact supplemental observation",
            recap
        );

        (string systemPrompt, ImmutableArray<IHistoryMessage> context) =
            SessionSupplementalContextRecipe.Expand(
                "base system",
                manifest.Plan.ExactContextInputs
            );

        Assert.Equal("base system\n\nrecap system", systemPrompt);
        Assert.Collection(
            context,
            message => Assert.Equal(
                "recap observation",
                Assert.IsType<ObservationMessage>(message).Content
            ),
            message => Assert.Equal(
                "recap action",
                Assert.IsType<ActionMessage>(message).GetFlattenedText()
            ),
            message => Assert.Equal(
                "exact supplemental observation",
                Assert.IsType<ObservationMessage>(message).Content
            )
        );
        Assert.DoesNotContain(
            context.OfType<ObservationMessage>(),
            static observation => observation.Content?.Contains(
                SessionSupplementalContextRecipe.ControlSchemaId,
                StringComparison.Ordinal
            ) == true
        );
    }

    [Fact]
    public void Expand_NoMatchAddsNoProviderFacingMessage() {
        CompletionRequestPreparedBody manifest = PreparedV6Fixture.Create(
            selectedObservationContent: null,
            recapInputs: []
        );

        (string systemPrompt, ImmutableArray<IHistoryMessage> context) =
            SessionSupplementalContextRecipe.Expand(
                "base system",
                manifest.Plan.ExactContextInputs
            );

        Assert.Equal("base system", systemPrompt);
        Assert.Empty(context);
    }

    private static SessionRequestContextInput ContextInput(
        SessionRequestArtifactContextSnapshot snapshot
    ) => new(
        SessionArtifactContextSnapshotHasher.ComputeSha256(snapshot),
        snapshot
    );

    private static SessionCreateOptions CreateOptions()
        => new("model-A", "system-A", "surface-A");

    private static SessionRuntime CreateRuntime(
        ICompletionClient client,
        ICoherentContextCandidateSource candidateSource
    ) => new(
        client,
        CompletionTarget: new SessionCompletionTargetIdentity(
            "supplemental-test-connection",
            "test",
            "supplemental-test-connection-v1",
            "supplemental-test-adapter-v1"
        ),
        MaxTokens: 256,
        ContextCandidateSource: candidateSource
    );

    private static EventAddress ReadSingleAddressByKind(
        string path,
        SessionEventKind kind
    ) => Assert.Single(ReadAddressesByKind(path, kind));

    private static EventAddress[] ReadAddressesByKind(
        string path,
        SessionEventKind kind
    ) {
        using var journal = EventJournal.EventJournal.OpenExisting(path);
        RefId main = journal.OpenBranch(SessionJournalDefaults.MainBranchName)
            .Unwrap();
        EventAddress head = journal.GetHead(main)!.Value;
        return [
            .. journal.ReadChronologicalChain(head, checkedRead: true)
                .Unwrap()
                .Where(address =>
                    journal.ReadEventHeaderPreview(address)
                        .Unwrap()
                        .OpaqueEventKind == (uint)kind
                )
        ];
    }

    private static (CompletionRequestPreparedBody Body, int SchemaVersion)
        ReadPrepared(string path) {
        EventAddress address = ReadSingleAddressByKind(
            path,
            SessionEventKind.CompletionRequestPrepared
        );
        using var journal = EventJournal.EventJournal.OpenExisting(path);
        using EventFrame frame = journal.ReadEvent(address).Unwrap();
        object body = SessionEventCodec.Decode(
            SessionEventKind.CompletionRequestPrepared,
            frame.Payload.ToArray(),
            out int schemaVersion
        );
        return (Assert.IsType<CompletionRequestPreparedBody>(body), schemaVersion);
    }

    private string NewJournalPath() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-session-supplemental-tests",
            Guid.NewGuid().ToString("N")
        );
        _tempDirectories.Add(path);
        return path;
    }

    private sealed class RecordingCompletionClient : ICompletionClient {
        private readonly Queue<string> _responses = [];

        public string Name => "supplemental-test-client";

        public string ApiSpecId => "supplemental-test-api-v1";

        internal int Calls { get; private set; }

        internal List<CompletionRequest> Requests { get; } = [];

        internal void Enqueue(string response) => _responses.Enqueue(response);

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Requests.Add(request);
            if (_responses.Count == 0) {
                throw new InvalidOperationException("No scripted response.");
            }
            return Task.FromResult(new CompletionResult(
                new ActionMessage([
                    new ActionBlock.Text(_responses.Dequeue())
                ]),
                new CompletionDescriptor(Name, ApiSpecId, request.ModelId)
            ));
        }
    }
}

internal sealed class TestSupplementalContextSource(
    SessionSupplementalContextSelection selection
) : ISessionSupplementalContextSource {
    private readonly List<SessionSupplementalContextRequest> _requests = [];

    internal int CallCount => _requests.Count;

    internal IReadOnlyList<SessionSupplementalContextRequest> Requests
        => _requests;

    internal Func<SessionSupplementalContextRequest, CancellationToken,
        SessionSupplementalContextSelection>? Handler { get; init; }

    public ValueTask<SessionSupplementalContextSelection> SelectAsync(
        SessionSupplementalContextRequest request,
        CancellationToken cancellationToken
    ) {
        _requests.Add(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            Handler?.Invoke(request, cancellationToken) ?? selection
        );
    }
}
