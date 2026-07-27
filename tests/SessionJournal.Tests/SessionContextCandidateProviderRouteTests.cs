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
    public async Task NullCandidate_DurablyKeepsObservation_ThenResumeSelectsAgain() {
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
            SessionExecutionRecovery waiting = engine.ResolveExecutionTail();
            Assert.Equal(SessionExecutionPhase.AwaitingAgentAction, waiting.State.Phase);
            EventAddress observation = waiting.Head!.Value;
            Assert.Equal(1, source.SelectionCount);
            Assert.Equal(observation, source.Requests[0].CompletionBoundary);
            Assert.Equal(0, client.Calls);

            source.Candidate = fixture.Candidate;
            ResumeOutcome outcome = await engine.ResumeAsync(CancellationToken.None);

            Assert.True(outcome.Advanced);
            Assert.Equal(2, source.SelectionCount);
            Assert.Equal(observation, source.Requests[1].CompletionBoundary);
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
            Assert.Equal(3, source.SelectionCount);
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
            source.Requests.Select(static request => request.CompletionBoundary)
        );
        Assert.Equal(3, client.Calls);
        Assert.Equal(2, tool.Calls);
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
            "outstanding tool dependencies",
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
        TestContextCandidateSource source,
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
