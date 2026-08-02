using System.Collections.Immutable;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionRuntimeRecoveryRequirementsTests
    : IDisposable {
    private static readonly SessionCompletionTargetIdentity Target = new(
        "recovery-connection",
        "test",
        "connection-fingerprint-v1",
        "adapter-fingerprint-v1"
    );
    private static readonly SessionToolRuntimeIdentity ToolIdentity = new(
        "recovery-tools",
        "implementation-set-v1",
        "capability-set-v1"
    );

    private readonly List<string> _paths = [];

    [Fact]
    public void EmptyIdleAndObservationProjectWithoutRuntimeOrMutation() {
        string emptyPath = NewPath();
        using (var journal = EventJournal.EventJournal.CreateNew(emptyPath)) {
            journal.CreateBranch(
                SessionJournalDefaults.MainBranchName,
                startPoint: null
            ).Unwrap();
        }
        using (var empty = SessionJournalEngine.Open(emptyPath)) {
            var requirements = Assert.IsType<
                SessionRuntimeRecoveryRequirements.NoRuntimeRequired
            >(empty.InspectRuntimeRecoveryRequirements());
            Assert.Null(requirements.CapturedHead);
            Assert.Equal(SessionExecutionPhase.Empty, requirements.Phase);
            Assert.Null(requirements.HeadKind);
        }

        string path = NewPath();
        using var engine = SessionJournalEngine.Create(
            path,
            Options()
        );
        EventAddress idleHead = engine.ReadCurrentHead()!.Value;
        var idle = Assert.IsType<
            SessionRuntimeRecoveryRequirements.NoRuntimeRequired
        >(engine.InspectRuntimeRecoveryRequirements());
        Assert.Equal(idleHead, idle.CapturedHead);
        Assert.Equal(SessionExecutionPhase.Idle, idle.Phase);
        Assert.Equal(SessionEventKind.SessionCreated, idle.HeadKind);

        EventAddress observation = engine.AppendObservation(
            "secret observation body"
        );
        var pending = Assert.IsType<
            SessionRuntimeRecoveryRequirements.NewRequestRequired
        >(engine.InspectRuntimeRecoveryRequirements());
        Assert.Equal(observation, pending.CapturedHead);
        Assert.Equal(
            SessionExecutionPhase.AwaitingAgentAction,
            pending.Phase
        );
        Assert.Equal(SessionEventKind.ObservationAccepted, pending.HeadKind);
        Assert.Equal(observation, engine.ReadCurrentHead());
    }

    [Fact]
    public async Task PreparedAndStartedKeepExactSanitizedIdentity() {
        string path = NewPath();
        var client = new NeverClient("exact-client", "exact-api-v1");
        var tool = new RecordingTool("lookup");
        ToolSession tools = new ToolRegistry([tool]).CreateSession();
        SessionRuntime runtime = Runtime(client, tools);
        EventAddress prepared = await CreatePreparedAsync(path, runtime);

        SessionRuntimeRecoveryRequirements.FrozenCompletionRequired
            safe;
        using (var inspection = SessionJournalEngine.OpenReadOnly(path)) {
            safe = Assert.IsType<
                SessionRuntimeRecoveryRequirements
                    .FrozenCompletionRequired
            >(inspection.InspectRuntimeRecoveryRequirements());
        }
        Assert.Equal(prepared, safe.CapturedHead);
        Assert.Equal(
            SessionExecutionPhase.AwaitingCompletionDispatch,
            safe.Phase
        );
        Assert.Equal(
            SessionEventKind.CompletionRequestPrepared,
            safe.HeadKind
        );
        Assert.Equal(Target, safe.CompletionTarget);
        Assert.Equal(client.Name, safe.ClientName);
        Assert.Equal(client.ApiSpecId, safe.ApiSpecId);
        Assert.Equal(
            SessionVisibleToolSetFingerprint.ComputeSha256(
                tools.VisibleDefinitions
            ),
            safe.VisibleToolSetSha256
        );
        Assert.Equal(ToolIdentity, safe.ToolRuntimeIdentity);
        Assert.Equal(
            SessionDurableDispatchState.NotStarted,
            safe.DispatchState
        );

        EventAddress started = AppendStarted(path, prepared);
        SessionRuntimeRecoveryRequirements.FrozenCompletionRequired
            uncertain;
        using (var inspection = SessionJournalEngine.OpenReadOnly(path)) {
            uncertain = Assert.IsType<
                SessionRuntimeRecoveryRequirements
                    .FrozenCompletionRequired
            >(inspection.InspectRuntimeRecoveryRequirements());
        }
        Assert.Equal(started, uncertain.CapturedHead);
        Assert.Equal(
            SessionExecutionPhase.AwaitingCompletion,
            uncertain.Phase
        );
        Assert.Equal(
            SessionEventKind.CompletionAttemptStarted,
            uncertain.HeadKind
        );
        Assert.Equal(safe.CompletionTarget, uncertain.CompletionTarget);
        Assert.Equal(safe.ClientName, uncertain.ClientName);
        Assert.Equal(safe.ApiSpecId, uncertain.ApiSpecId);
        Assert.Equal(
            safe.VisibleToolSetSha256,
            uncertain.VisibleToolSetSha256
        );
        Assert.Equal(
            safe.ToolRuntimeIdentity,
            uncertain.ToolRuntimeIdentity
        );
        Assert.Equal(
            SessionDurableDispatchState.StartedOutcomeUncertain,
            uncertain.DispatchState
        );
        Assert.Equal(0, client.Calls);
        Assert.Equal(0, tool.Calls);
        Assert.Equal(started, ReadHead(path));

        string text = JsonSerializer.Serialize(uncertain);
        Assert.DoesNotContain("secret prepared observation", text);
        Assert.DoesNotContain("lookup description secret", text);

        EventAddress failed = AppendFailure(path, started);
        using (var inspection = SessionJournalEngine.OpenReadOnly(path)) {
            var terminal = Assert.IsType<
                SessionRuntimeRecoveryRequirements.NoRuntimeRequired
            >(inspection.InspectRuntimeRecoveryRequirements());
            Assert.Equal(failed, terminal.CapturedHead);
            Assert.Equal(SessionExecutionPhase.TurnFailed, terminal.Phase);
            Assert.Equal(
                SessionEventKind.CompletionAttemptFailed,
                terminal.HeadKind
            );
        }
        Assert.Equal(0, client.Calls);
        Assert.Equal(0, tool.Calls);
    }

    [Fact]
    public void ToolContinuationDistinguishesDurableStartWithoutBodyLeak() {
        string path = NewPath();
        var client = new NeverClient();
        var tool = new RecordingTool("lookup");
        SessionRuntime runtime = Runtime(
            client,
            new ToolRegistry([tool]).CreateSession()
        );
        EventAddress action;
        using (var engine = SessionJournalEngine.Create(
                   path,
                   Options(),
                   runtime
               )) {
            engine.AppendObservation("run a tool");
            action = engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.ToolCall(new RawToolCall(
                        "lookup",
                        "call-secret",
                        "{\"secret\":true}"
                    ))
                ]),
                new CompletionDescriptor(
                    "import",
                    "import-v1",
                    "model-A"
                )
            );
            var pending = Assert.IsType<
                SessionRuntimeRecoveryRequirements
                    .ToolContinuationRequired
            >(engine.InspectRuntimeRecoveryRequirements());
            Assert.Equal(action, pending.CapturedHead);
            Assert.Equal(ToolIdentity, pending.ToolRuntimeIdentity);
            Assert.Equal(
                SessionDurableDispatchState.NotStarted,
                pending.DispatchState
            );
            Assert.DoesNotContain("call-secret", pending.ToString());
            Assert.DoesNotContain("secret", pending.ToString());
        }

        EventAddress started = AppendToolStarted(path, action);
        using (var reopened = SessionJournalEngine.OpenReadOnly(path)) {
            var uncertain = Assert.IsType<
                SessionRuntimeRecoveryRequirements
                    .ToolContinuationRequired
            >(reopened.InspectRuntimeRecoveryRequirements());
            Assert.Equal(started, uncertain.CapturedHead);
            Assert.Equal(ToolIdentity, uncertain.ToolRuntimeIdentity);
            Assert.Equal(
                SessionDurableDispatchState.StartedOutcomeUncertain,
                uncertain.DispatchState
            );
            string publicJson = JsonSerializer.Serialize(uncertain);
            Assert.DoesNotContain("operation-secret", publicJson);
            Assert.DoesNotContain("call-secret", publicJson);
        }
        Assert.Equal(0, client.Calls);
        Assert.Equal(0, tool.Calls);
        Assert.Equal(started, ReadHead(path));
    }

    [Fact]
    public async Task CorruptPreparedManifestStillThrowsInvalidData() {
        string path = NewPath();
        var client = new NeverClient();
        EventAddress prepared = await CreatePreparedAsync(
            path,
            Runtime(client)
        );
        CompletionRequestPreparedBody source = ReadBody<
            CompletionRequestPreparedBody
        >(path, prepared, SessionEventKind.CompletionRequestPrepared);
        EventAddress parent = ReadParent(path, prepared)!.Value;
        CompletionRequestPreparedBody corrupt = source with {
            Commitment = source.Commitment with {
                Sha256 = new string('0', 64)
            }
        };
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            RefId main = journal.OpenBranch(
                SessionJournalDefaults.MainBranchName
            ).Unwrap();
            Assert.True(journal.MoveRef(main, prepared, parent).Unwrap());
            _ = journal.CommitToRef(
                SessionJournalDefaults.MainBranchName,
                parent,
                SessionEventCodec.Encode(
                    SessionEventKind.CompletionRequestPrepared,
                    corrupt
                ),
                opaqueEventKind:
                    (uint)SessionEventKind.CompletionRequestPrepared,
                hint: default
            ).Unwrap();
        }

        using var reopened = SessionJournalEngine.OpenReadOnly(path);
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => reopened.InspectRuntimeRecoveryRequirements()
        );
        Assert.Contains("commitment", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task ExpectedHeadBoundSendAndResumeRejectLaterTail() {
        string sendPath = NewPath();
        var sendClient = new NeverClient();
        SessionRuntime sendRuntime = Runtime(sendClient);
        using (var engine = SessionJournalEngine.Create(
                   sendPath,
                   Options(),
                   sendRuntime
               )) {
            EventAddress capturedIdle = engine.ReadCurrentHead()!.Value;
            EventAddress laterSetup = engine.AppendSystemPromptSetup(
                "later prompt"
            );

            SessionJournalExpectedHeadMismatchException mismatch =
                await Assert.ThrowsAsync<
                    SessionJournalExpectedHeadMismatchException
                >(() => engine.SendAsync(
                    capturedIdle,
                    "must not send",
                    CancellationToken.None
                ));

            Assert.Equal(capturedIdle, mismatch.ExpectedHead);
            Assert.Equal(laterSetup, mismatch.ObservedHead);
            Assert.Equal(laterSetup, engine.ReadCurrentHead());
            Assert.Equal(0, sendClient.Calls);
        }

        string resumePath = NewPath();
        var resumeClient = new NeverClient();
        SessionRuntime resumeRuntime = Runtime(resumeClient);
        EventAddress prepared = await CreatePreparedAsync(
            resumePath,
            resumeRuntime
        );
        EventAddress started = AppendStarted(resumePath, prepared);
        using var reopened = SessionJournalEngine.Open(
            resumePath,
            resumeRuntime
        );

        SessionJournalExpectedHeadMismatchException resumeMismatch =
            await Assert.ThrowsAsync<
                SessionJournalExpectedHeadMismatchException
            >(() => reopened.ResumeAsync(
                prepared,
                CancellationToken.None
            ));

        Assert.Equal(prepared, resumeMismatch.ExpectedHead);
        Assert.Equal(started, resumeMismatch.ObservedHead);
        Assert.Equal(started, reopened.ReadCurrentHead());
        Assert.Equal(0, resumeClient.Calls);
    }

    public void Dispose() {
        foreach (string path in _paths) {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private async Task<EventAddress> CreatePreparedAsync(
        string path,
        SessionRuntime runtime
    ) {
        using var engine = SessionJournalEngine.CreateForTest(
            path,
            Options(),
            runtime,
            new SessionJournalTestHooks(
                SessionJournalFailpoint.AfterRequestPreparedCommitted
            )
        );
        await Assert.ThrowsAsync<SessionJournalFailpointException>(
            () => engine.SendAsync(
                "secret prepared observation",
                CancellationToken.None
            )
        );
        return engine.ReadCurrentHead()!.Value;
    }

    private static EventAddress AppendStarted(
        string path,
        EventAddress prepared
    ) {
        using var journal = EventJournal.EventJournal.OpenExisting(path);
        return journal.CommitToRef(
            SessionJournalDefaults.MainBranchName,
            prepared,
            SessionEventCodec.Encode(
                SessionEventKind.CompletionAttemptStarted,
                new CompletionAttemptStartedBody()
            ),
            opaqueEventKind:
                (uint)SessionEventKind.CompletionAttemptStarted,
            hint: default
        ).Unwrap().EventAddress;
    }

    private static EventAddress AppendToolStarted(
        string path,
        EventAddress action
    ) {
        using var journal = EventJournal.EventJournal.OpenExisting(path);
        return journal.CommitToRef(
            SessionJournalDefaults.MainBranchName,
            action,
            SessionEventCodec.Encode(
                SessionEventKind.ToolExecutionStarted,
                new ToolExecutionStartedBody(
                    "call-secret",
                    "lookup",
                    "{\"secret\":true}",
                    "operation-secret",
                    1,
                    ToolIdentity
                )
            ),
            opaqueEventKind:
                (uint)SessionEventKind.ToolExecutionStarted,
            hint: default
        ).Unwrap().EventAddress;
    }

    private static EventAddress AppendFailure(
        string path,
        EventAddress started
    ) {
        using var journal = EventJournal.EventJournal.OpenExisting(path);
        return journal.CommitToRef(
            SessionJournalDefaults.MainBranchName,
            started,
            SessionEventCodec.Encode(
                SessionEventKind.CompletionAttemptFailed,
                new CompletionAttemptFailedBody(
                    CompletionTerminationKind.Failed,
                    "test-failure",
                    "known terminal failure",
                    Array.Empty<string>()
                )
            ),
            opaqueEventKind:
                (uint)SessionEventKind.CompletionAttemptFailed,
            hint: default
        ).Unwrap().EventAddress;
    }

    private static SessionRuntime Runtime(
        NeverClient client,
        ToolSession? tools = null
    ) {
        var candidates = new TestContextCandidateSource {
            IsEmptyLineage = true
        };
        return new SessionRuntime(
            client,
            tools,
            Target,
            MaxTokens: 123,
            ToolRuntimeIdentity: ToolIdentity,
            ContextCandidateSource: candidates
        );
    }

    private static SessionCreateOptions Options() => new(
        "model-A",
        "system prompt secret",
        "surface-A"
    );

    private string NewPath() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-runtime-recovery-requirements-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }

    private static EventAddress ReadHead(string path) {
        using var journal = EventJournal.EventJournal.OpenExisting(path);
        RefId main = journal.OpenBranch(
            SessionJournalDefaults.MainBranchName
        ).Unwrap();
        return journal.GetHead(main)!.Value;
    }

    private static EventAddress? ReadParent(
        string path,
        EventAddress address
    ) {
        using var journal = EventJournal.EventJournal.OpenExisting(path);
        return journal.ReadEventHeaderPreview(address).Unwrap().Parent;
    }

    private static T ReadBody<T>(
        string path,
        EventAddress address,
        SessionEventKind kind
    ) where T : class {
        using var engine = SessionJournalEngine.OpenReadOnly(path);
        return Assert.IsType<T>(SessionEventCodec.Decode(
            kind,
            engine.ReadPayloadBytes(address),
            out _
        ));
    }

    private sealed class NeverClient(
        string name = "never-client",
        string apiSpecId = "never-api-v1"
    ) : ICompletionClient {
        public string Name { get; } = name;
        public string ApiSpecId { get; } = apiSpecId;
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
            throw new InvalidOperationException("Provider must not run.");
        }
    }

    private sealed class RecordingTool(string name) : ITool {
        public ToolDefinition Definition { get; } = new(
            name,
            "lookup description secret",
            new ToolSchema.Object()
        );
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
                    "unused"
                )
            );
        }
    }
}
