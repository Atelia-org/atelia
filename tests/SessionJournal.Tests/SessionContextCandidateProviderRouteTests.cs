using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionContextCandidateProviderRouteTests : IDisposable {
    private static readonly SessionToolRuntimeIdentity ToolRuntimeIdentity = new(
        "candidate-test-host",
        "candidate-test-implementations-v1",
        "candidate-test-capabilities-v1"
    );

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
    public async Task MissingCandidate_FailsBeforeObservation_ThenSendCanRetry() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        client.Enqueue(Terminal("done"));
        var source = new TestContextCandidateSource();
        using (var engine = SessionJournalEngine.Create(
            path,
            CreateOptions(),
            CreateRuntime(client, source)
        )) {
            TestContextCandidateFixture fixture =
                ContextCandidateTestFixture.CreateAtCurrentHead(engine);

            SessionJournalNotReadyException error =
                await Assert.ThrowsAsync<SessionJournalNotReadyException>(
                    () => engine.SendAsync("remember this", CancellationToken.None)
                );

            Assert.Equal(
                SessionJournalNotReadyReason.ContextCandidateUnavailable,
                error.Reason
            );
            SessionExecutionRecovery waiting =
                engine.ResolveExecutionTail();
            Assert.Equal(
                SessionExecutionPhase.Idle,
                waiting.State.Phase
            );
            Assert.Equal(1, source.SelectionCount);
            Assert.Equal(
                fixture.Anchor,
                source.Requests[0].CompletionBoundary
            );
            Assert.Equal(0, client.Calls);

            source.Candidate = fixture.Candidate;
            TurnResult outcome = await engine.SendAsync(
                "remember this",
                CancellationToken.None
            );

            Assert.Equal(
                "done",
                outcome.Message.GetFlattenedText()
            );
            Assert.Equal(3, source.SelectionCount);
            Assert.Equal(1, client.Calls);
        }
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("coherence-group")]
    [InlineData("token-budget")]
    public async Task InvalidSelectionOptions_FailBeforeObservationOrSelection(
        string invalidCase
    ) {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        var source = new TestContextCandidateSource();
        SessionContextSelectionOptions options = invalidCase switch {
            "mode" => new(
                "default",
                (SessionContextSelectionMode)42
            ),
            "coherence-group" => new(" "),
            "token-budget" => new("default", RawSuffixTokenBudget: 0),
            _ => throw new ArgumentOutOfRangeException(nameof(invalidCase))
        };
        using var engine = SessionJournalEngine.Create(
            path,
            CreateOptions(),
            CreateRuntime(client, source) with { ContextSelection = options }
        );
        EventAddress head = engine.ResolveExecutionTail().Head!.Value;

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => engine.SendAsync("must not persist", CancellationToken.None)
        );

        Assert.Equal(head, engine.ResolveExecutionTail().Head);
        Assert.Equal(0, source.SelectionCount);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task PreAppendDiscoveryStopsAtBoundWithoutTrustingCountOrIndexer() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        var source = new ScriptedDiscoverySource();
        using var engine = SessionJournalEngine.Create(
            path,
            CreateOptions(),
            CreateRuntime(client, source) with {
                ContextSelection = new(
                    "default",
                    SessionContextSelectionMode.Latest,
                    MaxCandidateCount: 64
                )
            }
        );
        TestContextCandidateFixture fixture =
            ContextCandidateTestFixture.CreateAtCurrentHead(engine);
        var descriptor = new SessionContextCandidateDescriptor(
            "bounded",
            0,
            fixture.Candidate.RawStartExclusive,
            fixture.Candidate.AnchorSetups
        );
        var candidates = new CountSpoofingCandidateList(
            descriptor,
            actualCount: 65
        );
        source.Enqueue(candidates, fixture.Candidate);
        EventAddress head = engine.ResolveExecutionTail().Head!.Value;

        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                () => engine.SendAsync(
                    "must not persist",
                    CancellationToken.None
                )
            );

        Assert.Contains(
            "discovery bound",
            error.Message,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Equal(1, candidates.EnumerationCount);
        Assert.Equal(65, candidates.YieldCount);
        Assert.Equal(head, engine.ResolveExecutionTail().Head);
        Assert.Equal(0, source.MaterializationCount);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task PostAppendDiscoveryStopsAtBoundWithoutTrustingCountOrIndexer() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        var source = new ScriptedDiscoverySource();
        using var engine = SessionJournalEngine.Create(
            path,
            CreateOptions(),
            CreateRuntime(client, source) with {
                ContextSelection = new(
                    "default",
                    SessionContextSelectionMode.Latest,
                    MaxCandidateCount: 64
                )
            }
        );
        TestContextCandidateFixture fixture =
            ContextCandidateTestFixture.CreateAtCurrentHead(engine);
        var descriptor = new SessionContextCandidateDescriptor(
            "bounded",
            0,
            fixture.Candidate.RawStartExclusive,
            fixture.Candidate.AnchorSetups
        );
        source.Enqueue(
            new[] { descriptor },
            fixture.Candidate
        );
        var candidates = new CountSpoofingCandidateList(
            descriptor,
            actualCount: 65
        );
        source.Enqueue(candidates, fixture.Candidate);

        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                () => engine.SendAsync(
                    "persist exactly once",
                    CancellationToken.None
                )
            );

        Assert.Contains(
            "discovery bound",
            error.Message,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Equal(1, candidates.EnumerationCount);
        Assert.Equal(65, candidates.YieldCount);
        Assert.Equal(1, source.MaterializationCount);
        Assert.Equal(
            SessionExecutionPhase.AwaitingAgentAction,
            engine.ResolveExecutionTail().State.Phase
        );
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task ProjectedTotalBudgetFailure_DoesNotAppendObservation() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        var source = new TestContextCandidateSource();
        using var engine = SessionJournalEngine.Create(
            path,
            CreateOptions(),
            CreateRuntime(client, source) with {
                ContextSelection =
                    new SessionContextSelectionOptions(
                        "default",
                        SessionContextSelectionMode.Latest,
                        TotalContextTokenBudget: 1
                    )
            }
        );
        TestContextCandidateFixture fixture =
            ContextCandidateTestFixture.CreateAtCurrentHead(engine);
        source.Candidate = fixture.Candidate;
        EventAddress head =
            engine.InspectExecutionBoundary().Head!.Value;
        int eventCount =
            engine.ReadCurrentLineageHeaders()
                .HeadToRoot.Count;

        SessionJournalNotReadyException error =
            await Assert.ThrowsAsync<
                SessionJournalNotReadyException
            >(
                () => engine.SendAsync(
                    "must remain ephemeral",
                    CancellationToken.None
                )
            );

        Assert.Equal(
            SessionJournalNotReadyReason
                .ContextCandidateUnavailable,
            error.Reason
        );
        Assert.Equal(head, engine.InspectExecutionBoundary().Head);
        Assert.Equal(
            eventCount,
            engine.ReadCurrentLineageHeaders()
                .HeadToRoot.Count
        );
        Assert.Equal(1, source.SelectionCount);
        Assert.Equal(1, source.MaterializationCount);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task EmptyLineageBootstrap_CommitsPreparedV5AndReopensWithoutDerivedSource() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        var source = new TestContextCandidateSource {
            IsEmptyLineage = true
        };
        SessionRuntime runtime = CreateRuntime(client, source) with {
            ContextSelection =
                new SessionContextSelectionOptions(
                    "default",
                    BootstrapRawSuffixTokenBudget: 4096
                )
        };
        using (var engine =
               SessionJournalEngine.CreateForTest(
                   path,
                   CreateOptions(),
                   runtime,
                   new SessionJournalTestHooks(
                       SessionJournalFailpoint
                           .AfterRequestPreparedCommitted
                   )
               )) {
            SessionJournalFailpointException error =
                await Assert.ThrowsAsync<
                    SessionJournalFailpointException
                >(
                    () => engine.SendAsync(
                        "bootstrap",
                        CancellationToken.None
                    )
                );
            Assert.Equal(
                SessionJournalFailpoint
                    .AfterRequestPreparedCommitted,
                error.Failpoint
            );
            Assert.Equal(2, source.SelectionCount);
            Assert.Equal(0, source.MaterializationCount);
            Assert.Equal(0, engine.FullProjectionInvocationCount);
        }
        EventAddress prepared = Assert.Single(
            ReadAddressesByKind(
                path,
                SessionEventKind.CompletionRequestPrepared
            )
        );
        CompletionRequestPreparedBody manifest =
            ReadBody<CompletionRequestPreparedBody>(
                path,
                prepared,
                SessionEventKind.CompletionRequestPrepared
            );
        Assert.Empty(manifest.Plan.ExactContextInputs);

        client.Enqueue(Terminal("resumed"));
        using var reopened = SessionJournalEngine.Open(
            path,
            runtime with {
                ContextCandidateSource = null,
                MemoryLifecycle = null
            }
        );
        ResumeOutcome outcome = await reopened.ResumeAsync(
            CancellationToken.None
        );

        Assert.True(outcome.Advanced);
        Assert.Equal("resumed", outcome.Message!.GetFlattenedText());
        Assert.Equal(1, client.Calls);
        Assert.Equal(0, reopened.FullProjectionInvocationCount);
        Assert.Equal(2, source.SelectionCount);
    }

    [Fact]
    public async Task NthPreviousSelectsExactProviderOrdinal() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        client.Enqueue(Terminal("done"));
        var source = new TestContextCandidateSource();
        using var engine = SessionJournalEngine.Create(
            path,
            CreateOptions(),
            CreateRuntime(client, source)
        );
        TestContextCandidateFixture older =
            ContextCandidateTestFixture.CreateAtCurrentHead(
                engine,
                "older"
            );
        engine.AppendObservation("prior turn");
        _ = engine.AppendImportedAgentAction(
            new ActionMessage([
                new ActionBlock.Text("prior answer")
            ]),
            new CompletionDescriptor(
                "import",
                "v1",
                "model-A"
            )
        );
        TestContextCandidateFixture newer =
            ContextCandidateTestFixture.CreateAtCurrentHead(
                engine,
                "newer"
            );
        source.Candidates = [
            newer.Candidate,
            older.Candidate
        ];
        engine.UseRuntime(
            CreateRuntime(client, source) with {
                ContextSelection =
                    new SessionContextSelectionOptions(
                        "default",
                        SessionContextSelectionMode.NthPrevious,
                        NthPreviousOrdinal: 1
                    )
            }
        );

        _ = await engine.SendAsync(
            "choose older",
            CancellationToken.None
        );

        Assert.All(
            source.MaterializedHandles,
            handle => Assert.Equal(
                "test-candidate-1",
                handle
            )
        );
        Assert.Equal(0, engine.FullProjectionInvocationCount);
    }

    [Fact]
    public async Task BudgetedFallsForwardWhenOlderCandidateExceedsTotalBudget() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        client.Enqueue(Terminal("done"));
        var source = new TestContextCandidateSource();
        using var engine = SessionJournalEngine.Create(
            path,
            CreateOptions(),
            CreateRuntime(client, source)
        );
        TestContextCandidateFixture oldBase =
            ContextCandidateTestFixture.CreateAtCurrentHead(
                engine,
                "older"
            );
        string oversizedMemory = new('x', 6_000);
        SessionContextCandidate older =
            ContextCandidateTestFixture.CreateCandidate(
                engine,
                oldBase.Anchor,
                engine.ResolveGoverningSetup(
                    oldBase.Anchor
                ),
                ContextCandidateTestFixture.Contribution(
                    MemoryPackCarrier.Observation,
                    "fixture.large",
                    oversizedMemory,
                    oldBase.Anchor
                )
            );
        engine.AppendObservation("prior turn");
        _ = engine.AppendImportedAgentAction(
            new ActionMessage([
                new ActionBlock.Text("prior answer")
            ]),
            new CompletionDescriptor(
                "import",
                "v1",
                "model-A"
            )
        );
        SessionContextCandidate newer =
            ContextCandidateTestFixture
                .CreateAtCurrentHead(engine, "newer")
                .Candidate;
        source.Candidates = [newer, older];
        engine.UseRuntime(
            CreateRuntime(client, source) with {
                ContextSelection =
                    new SessionContextSelectionOptions(
                        "default",
                        SessionContextSelectionMode.Budgeted,
                        RawSuffixTokenBudget: 10_000,
                        TotalContextTokenBudget: 1_000
                    )
            }
        );

        _ = await engine.SendAsync(
            "fit total budget",
            CancellationToken.None
        );

        Assert.Contains(
            "test-candidate-1",
            source.MaterializedHandles
        );
        Assert.Equal(
            "test-candidate-0",
            source.MaterializedHandles[^1]
        );
        Assert.Equal(0, engine.FullProjectionInvocationCount);
    }

    [Fact]
    public async Task ObservationAndToolContinuations_SelectAtEveryExactBoundary() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        client.Enqueue(ToolCall("lookup", "call-1"));
        client.Enqueue(ToolCall("lookup", "call-2"));
        client.Enqueue(Terminal("done"));
        var source = new TestContextCandidateSource();
        var tool = new RecordingTool("lookup");
        using (var engine = SessionJournalEngine.Create(
            path,
            CreateOptions(),
            CreateRuntime(
                client,
                source,
                new ToolRegistry([tool]).CreateSession()
            )
        )) {
            TestContextCandidateFixture fixture =
                ContextCandidateTestFixture.CreateAtCurrentHead(engine);
            source.Candidate = fixture.Candidate;

            TurnResult result = await engine.SendAsync(
                "use the tool twice",
                CancellationToken.None
            );

            Assert.Equal("done", result.Message.GetFlattenedText());
            Assert.Equal(4, source.SelectionCount);
        }
        EventAddress observation = Assert.Single(
            ReadAddressesByKind(path, SessionEventKind.ObservationAccepted)
        );
        EventAddress[] toolResults = ReadAddressesByKind(
            path,
            SessionEventKind.ToolResultObserved
        );
        Assert.Equal(2, toolResults.Length);
        Assert.Equal(
            new[] { observation, toolResults[0], toolResults[1] },
            source.Requests.Skip(1).Select(
                static request =>
                    request.CompletionBoundary
            )
        );
        Assert.Equal(3, client.Calls);
        Assert.Equal(2, tool.Calls);
    }

    [Fact]
    public async Task SendAndToolContinuation_InvokeLifecycleOnlyAtSafeUnpreparedBoundaries() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        client.Enqueue(ToolCall("lookup", "call-1"));
        client.Enqueue(Terminal("done"));
        var source = new TestContextCandidateSource();
        var lifecycle = new TestMemoryLifecycle();
        var tool = new RecordingTool("lookup");
        using var engine = SessionJournalEngine.Create(
            path,
            CreateOptions(),
            CreateRuntime(
                client,
                source,
                new ToolRegistry([tool]).CreateSession()
            ) with {
                MemoryLifecycle = lifecycle
            }
        );
        TestContextCandidateFixture fixture =
            ContextCandidateTestFixture.CreateAtCurrentHead(engine);
        source.Candidate = fixture.Candidate;

        _ = await engine.SendAsync(
            "use one tool",
            CancellationToken.None
        );

        Assert.Collection(
            lifecycle.Requests,
            idle => {
                Assert.Equal(SessionExecutionPhase.Idle, idle.Phase);
                Assert.Equal("use one tool", idle.PendingObservation);
            },
            observation => {
                Assert.Equal(
                    SessionExecutionPhase.AwaitingAgentAction,
                    observation.Phase
                );
                Assert.Null(observation.PendingObservation);
            },
            toolResult => {
                Assert.Equal(
                    SessionExecutionPhase.AwaitingAgentAction,
                    toolResult.Phase
                );
                Assert.Null(toolResult.PendingObservation);
            }
        );
        Assert.Equal(3, source.SelectionCount);
        Assert.Equal(0, engine.FullProjectionInvocationCount);
    }

    [Fact]
    public async Task LifecycleRawMutationIsRejectedByPostCallbackHeadCas() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        var source = new TestContextCandidateSource();
        var lifecycle = new TestMemoryLifecycle {
            OnPrepare = static (engine, _) =>
                engine.AppendObservation("intruder")
        };
        using var engine = SessionJournalEngine.Create(
            path,
            CreateOptions(),
            CreateRuntime(client, source) with {
                MemoryLifecycle = lifecycle
            }
        );
        TestContextCandidateFixture fixture =
            ContextCandidateTestFixture.CreateAtCurrentHead(engine);
        source.Candidate = fixture.Candidate;

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.SendAsync(
                    "must not be appended",
                    CancellationToken.None
                )
            );

        Assert.Contains(
            "stale",
            error.Message,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Equal(1, lifecycle.InvocationCount);
        Assert.Equal(0, source.SelectionCount);
        Assert.Equal(0, client.Calls);
        Assert.Equal(
            SessionExecutionPhase.AwaitingAgentAction,
            engine.ResolveExecutionTail().State.Phase
        );
    }

    [Fact]
    public async Task CandidateWithToolActionAnchor_FailsOnPublicResumeRoute() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        var source = new TestContextCandidateSource();
        var tool = new RecordingTool("lookup");
        using var engine = SessionJournalEngine.Create(
            path,
            CreateOptions(),
            CreateRuntime(
                client,
                source,
                new ToolRegistry([tool]).CreateSession()
            )
        );
        engine.AppendObservation("start imported tool");
        EventAddress toolAction = engine.AppendImportedAgentAction(
            new ActionMessage([
                new ActionBlock.ToolCall(
                    new RawToolCall("lookup", "call-1", "{}")
                )
            ]),
            new CompletionDescriptor("import", "import-v1", "model-A")
        );
        await Assert.ThrowsAsync<SessionJournalNotReadyException>(
            () => engine.ResumeAsync(CancellationToken.None)
        );
        SessionGoverningSetup setup = engine.ResolveGoverningSetup(toolAction);
        source.Candidate = ContextCandidateTestFixture.CreateCandidate(
            engine,
            toolAction,
            setup,
            ContextCandidateTestFixture.Contribution(
                MemoryPackCarrier.Action,
                "fixture.autobiography",
                "unsafe action anchor",
                toolAction
            )
        );

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => engine.ResumeAsync(CancellationToken.None)
        );

        Assert.Contains(
            "not replay-safe",
            error.Message,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Equal(2, source.SelectionCount);
        Assert.Equal(0, client.Calls);
    }

    private static SessionCreateOptions CreateOptions()
        => new("model-A", "system-A", "surface-A");

    private static SessionRuntime CreateRuntime(
        ICompletionClient client,
        ICoherentContextCandidateSource source,
        ToolSession? toolSession = null
    ) => new(
        client,
        toolSession,
        new SessionCompletionTargetIdentity(
            "candidate-test-connection",
            "test",
            "candidate-test-connection-v1",
            "candidate-test-adapter-v1"
        ),
        MaxTokens: 256,
        ToolRuntimeIdentity: ToolRuntimeIdentity,
        ContextCandidateSource: source
    );

    private static Func<CompletionRequest, CompletionResult> Terminal(string text)
        => request => new CompletionResult(
            new ActionMessage([new ActionBlock.Text(text)]),
            Descriptor(request)
        );

    private static Func<CompletionRequest, CompletionResult> ToolCall(
        string toolName,
        string callId
    ) => request => new CompletionResult(
        new ActionMessage([
            new ActionBlock.ToolCall(new RawToolCall(toolName, callId, "{}"))
        ]),
        Descriptor(request)
    );

    private static CompletionDescriptor Descriptor(CompletionRequest request)
        => new("candidate-test-client", "candidate-test-api-v1", request.ModelId);

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

    private static EventAddress[] ReadAllAddresses(string path) {
        using var journal =
            EventJournal.EventJournal.OpenExisting(path);
        RefId main = journal.OpenBranch(
            SessionJournalDefaults.MainBranchName
        ).Unwrap();
        EventAddress head = journal.GetHead(main)!.Value;
        return [
            .. journal.ReadChronologicalChain(
                head,
                checkedRead: true
            ).Unwrap()
        ];
    }

    private static T ReadBody<T>(
        string path,
        EventAddress address,
        SessionEventKind kind
    ) where T : class {
        using var journal = EventJournal.EventJournal.OpenExisting(path);
        using EventFrame frame = journal.ReadEvent(address).Unwrap();
        return Assert.IsType<T>(
            SessionEventCodec.Decode(kind, frame.Payload.ToArray(), out _)
        );
    }

    private string NewJournalPath() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-session-context-provider-tests",
            Guid.NewGuid().ToString("N")
        );
        _tempDirectories.Add(path);
        return path;
    }

    private sealed class ScriptedClient : ICompletionClient {
        private readonly Queue<Func<CompletionRequest, CompletionResult>> _responses = [];

        public string Name => "candidate-test-client";

        public string ApiSpecId => "candidate-test-api-v1";

        public int Calls { get; private set; }

        internal void Enqueue(Func<CompletionRequest, CompletionResult> response)
            => _responses.Enqueue(response);

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (_responses.Count == 0) {
                throw new InvalidOperationException("No scripted response.");
            }
            return Task.FromResult(_responses.Dequeue()(request));
        }
    }

    private sealed class ScriptedDiscoverySource
        : ICoherentContextCandidateSource {
        private readonly Queue<(
            IReadOnlyList<SessionContextCandidateDescriptor>
                Descriptors,
            SessionContextCandidate Candidate
        )> _discoveries = [];
        private SessionContextCandidate? _currentCandidate;

        internal int MaterializationCount { get; private set; }

        internal void Enqueue(
            IReadOnlyList<SessionContextCandidateDescriptor>
                descriptors,
            SessionContextCandidate candidate
        ) => _discoveries.Enqueue((descriptors, candidate));

        public ValueTask<SessionContextCandidateDiscovery>
            DiscoverAsync(
            SessionContextSelectionRequest request,
            CancellationToken cancellationToken
        ) {
            request.ValidateShape();
            cancellationToken.ThrowIfCancellationRequested();
            (
                IReadOnlyList<SessionContextCandidateDescriptor>
                    descriptors,
                SessionContextCandidate candidate
            ) = _discoveries.Dequeue();
            _currentCandidate = candidate;
            return ValueTask.FromResult(
                new SessionContextCandidateDiscovery(
                    SessionContextCandidateDiscoveryStatus
                        .Candidates,
                    descriptors
                )
            );
        }

        public ValueTask<SessionContextCandidate>
            MaterializeAsync(
            SessionContextCandidateDescriptor descriptor,
            CancellationToken cancellationToken
        ) {
            _ = descriptor;
            cancellationToken.ThrowIfCancellationRequested();
            MaterializationCount++;
            return ValueTask.FromResult(
                _currentCandidate
                ?? throw new InvalidOperationException(
                    "No discovery preceded materialization."
                )
            );
        }
    }

    private sealed class CountSpoofingCandidateList(
        SessionContextCandidateDescriptor candidate,
        int actualCount
    ) : IReadOnlyList<SessionContextCandidateDescriptor> {
        internal int EnumerationCount { get; private set; }
        internal int YieldCount { get; private set; }

        public int Count => 0;

        public SessionContextCandidateDescriptor this[int index]
            => throw new InvalidOperationException(
                $"Indexer must not be trusted ({index})."
            );

        public IEnumerator<SessionContextCandidateDescriptor>
            GetEnumerator() {
            EnumerationCount++;
            for (int index = 0; index < actualCount; index++) {
                YieldCount++;
                yield return candidate;
            }
        }

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator()
            => GetEnumerator();
    }

    private sealed class RecordingTool(string name) : ITool {
        public ToolDefinition Definition { get; } =
            new(name, $"Tool {name}.", new ToolSchema.Object());

        public int Calls { get; private set; }

        public ValueTask<ToolExecuteResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken
        ) {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(
                ToolExecuteResult.FromText(
                    ToolExecutionStatus.Success,
                    $"tool result {Calls}"
                )
            );
        }
    }
}
