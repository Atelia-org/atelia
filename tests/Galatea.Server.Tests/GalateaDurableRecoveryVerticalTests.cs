using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedRecap.Planner;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaDurableRecoveryVerticalTests {
    private static readonly TimeSpan CompletionDeadline =
        TimeSpan.FromSeconds(5);

    [Fact]
    public async Task NewMessage_WhenObservationAwaitsAction_ReturnsRecoveryConflictWithoutCalls() {
        var completionFactory = new TrackingCompletionClientFactory();
        var normalizer = new TrackingNormalizer();
        await using var host = GalateaTestHost.Create(
            completionFactory,
            normalizer
        );
        string sessionPath = GetSessionPath(host);
        EventAddress pendingHead = AppendPendingObservation(sessionPath);
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/chat/turns",
            new ChatStreamRequest(
                "must not be accepted",
                ConnectionId: "test"
            )
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using JsonDocument body = await ReadJsonAsync(response);
        Assert.Equal(
            "recovery-required",
            body.RootElement.GetProperty("code").GetString()
        );
        Assert.Equal(
            nameof(SessionExecutionPhase.AwaitingAgentAction),
            body.RootElement.GetProperty("phase").GetString()
        );
        Assert.Equal(
            EventAddressTextCodec.Format(pendingHead),
            body.RootElement.GetProperty("head").GetString()
        );
        Assert.Equal(0, normalizer.NormalizeCallCount);
        Assert.Equal(0, completionFactory.CreateCallCount);
        Assert.Equal(0, completionFactory.Client.DispatchCallCount);

        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        Assert.Equal(pendingHead, session.Engine.ReadCurrentHead());
        Assert.Equal(
            SessionExecutionPhase.AwaitingAgentAction,
            session.Engine.InspectExecutionBoundary().Phase
        );
    }

    [Fact]
    public async Task NewMessage_WhenTurnFailed_AbandonsExactHeadBeforeSend() {
        var completionFactory = new TrackingCompletionClientFactory(
            "answer after abandon"
        );
        var normalizer = new TrackingNormalizer();
        await using var host = GalateaTestHost.Create(
            completionFactory,
            normalizer
        );
        string sessionPath = GetSessionPath(host);
        CompletionConnectionConfig connection = GetConnection(host);
        EventAddress failedHead = await CreateFailedBoundaryAsync(
            sessionPath,
            connection
        );
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);

        CurrentTurnDto? before = await client.GetFromJsonAsync<
            CurrentTurnDto
        >("/api/chat/turns/current");
        Assert.NotNull(before);
        Assert.Equal("idle", before!.Status);
        Assert.Equal(
            nameof(SessionExecutionPhase.TurnFailed),
            before.DurablePhase
        );
        Assert.Equal(
            EventAddressTextCodec.Format(failedHead),
            before.RecoveryHead
        );

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/chat/turns",
            new ChatStreamRequest(
                "continue after failure",
                ConnectionId: "test"
            )
        );
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        StartTurnResponseDto? started = await response.Content
            .ReadFromJsonAsync<StartTurnResponseDto>();
        Assert.NotNull(started);

        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        GalateaLiveTurn liveTurn = Assert.IsType<GalateaLiveTurn>(
            service.FindTurn(session, started!.TurnId)
        );
        await Assert.IsAssignableFrom<Task>(liveTurn.RunTask)
            .WaitAsync(CompletionDeadline);

        Assert.Equal("completed", liveTurn.Status);
        Assert.Equal(1, normalizer.NormalizeCallCount);
        Assert.Equal(1, completionFactory.CreateCallCount);
        Assert.Equal(1, completionFactory.Client.DispatchCallCount);
        Assert.Equal(
            SessionExecutionPhase.Idle,
            session.Engine.InspectExecutionBoundary().Phase
        );
        Assert.NotEqual(failedHead, session.Engine.ReadCurrentHead());
        SessionCompletedTurnProjection completed =
            session.Engine.ReadRecentCompletedTurns().Turns[^1];
        Assert.Equal(
            GalateaUserMessageEnvelope.Wrap("continue after failure"),
            completed.ObservationContent
        );
    }

    [Fact]
    public async Task Resume_WhenTurnFailed_RejectsBeforeRuntimeWork() {
        var completionFactory = new TrackingCompletionClientFactory(
            "must not dispatch"
        );
        var normalizer = new TrackingNormalizer();
        await using var host = GalateaTestHost.Create(
            completionFactory,
            normalizer
        );
        string sessionPath = GetSessionPath(host);
        EventAddress failedHead = await CreateFailedBoundaryAsync(
            sessionPath,
            GetConnection(host)
        );
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/chat/turns/resume",
            new ResumeTurnRequest(
                EventAddressTextCodec.Format(failedHead),
                ConnectionId: null,
                RestartUncertainCompletion: false
            )
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using JsonDocument body = await ReadJsonAsync(response);
        Assert.Equal(
            "failed-turn-must-be-abandoned",
            body.RootElement.GetProperty("code").GetString()
        );
        Assert.Equal(
            nameof(SessionExecutionPhase.TurnFailed),
            body.RootElement.GetProperty("phase").GetString()
        );
        Assert.Equal(0, normalizer.NormalizeCallCount);
        Assert.Equal(0, completionFactory.CreateCallCount);
        Assert.Equal(0, completionFactory.Client.DispatchCallCount);

        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        Assert.Equal(failedHead, session.Engine.ReadCurrentHead());
    }

    [Fact]
    public async Task ResumeMatchingObservation_CompletesWithoutNormalizingAgain() {
        var completionFactory = new TrackingCompletionClientFactory(
            "resumed answer"
        );
        var normalizer = new TrackingNormalizer();
        await using var host = GalateaTestHost.Create(
            completionFactory,
            normalizer
        );
        string sessionPath = GetSessionPath(host);
        EventAddress pendingHead = AppendPendingObservation(sessionPath);
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);

        StartTurnResponseDto started = await ResumeAsync(
            client,
            pendingHead,
            connectionId: "test"
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        GalateaLiveTurn liveTurn = Assert.IsType<GalateaLiveTurn>(
            service.FindTurn(session, started.TurnId)
        );
        await Assert.IsAssignableFrom<Task>(liveTurn.RunTask)
            .WaitAsync(CompletionDeadline);

        Assert.Equal("completed", liveTurn.Status);
        Assert.Equal(0, normalizer.NormalizeCallCount);
        Assert.Equal(1, completionFactory.CreateCallCount);
        Assert.Equal(1, completionFactory.Client.DispatchCallCount);
        Assert.Equal(
            SessionExecutionPhase.Idle,
            session.Engine.InspectExecutionBoundary().Phase
        );
        SessionCompletedTurnProjection completed = Assert.Single(
            session.Engine.ReadRecentCompletedTurns().Turns
        );
        Assert.Equal(
            GalateaUserMessageEnvelope.Wrap("already normalized"),
            completed.ObservationContent
        );
        Assert.Equal(
            "resumed answer",
            completed.TerminalAction.Message.GetFlattenedText()
        );
    }

    [Fact]
    public async Task ResumePrepared_ExactBindsAndDoesNotReadDeletedPlannerConfig() {
        var completionFactory = new TrackingCompletionClientFactory(
            "prepared recovery answer"
        );
        var normalizer = new TrackingNormalizer();
        using var callLogs = new TemporaryCallLogDirectory();
        await using var host = GalateaTestHost.Create(
            completionFactory,
            normalizer,
            callLogDirectory: callLogs.Path
        );
        string sessionPath = GetSessionPath(host);
        CompletionConnectionConfig connection = GetConnection(host);
        EventAddress preparedHead = await CreateRecoveryBoundaryAsync(
            sessionPath,
            connection,
            completionFactory.Client,
            "AfterRequestPreparedCommitted",
            SessionExecutionPhase.AwaitingCompletionDispatch
        );
        File.Delete(RecapEpochConfigLoader.GetCanonicalPath(sessionPath));
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);

        StartTurnResponseDto started = await ResumeAsync(
            client,
            preparedHead,
            connectionId: null
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        GalateaLiveTurn liveTurn = Assert.IsType<GalateaLiveTurn>(
            service.FindTurn(session, started.TurnId)
        );
        await Assert.IsAssignableFrom<Task>(liveTurn.RunTask)
            .WaitAsync(CompletionDeadline);

        Assert.Equal("completed", liveTurn.Status);
        Assert.Equal(0, normalizer.NormalizeCallCount);
        Assert.Equal(1, completionFactory.CreateCallCount);
        Assert.Equal(1, completionFactory.Client.DispatchCallCount);
        Assert.False(File.Exists(
            RecapEpochConfigLoader.GetCanonicalPath(sessionPath)
        ));
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(callLogs.Path, "agent"),
            "*.json"
        ));
        Assert.Equal(
            SessionExecutionPhase.Idle,
            session.Engine.InspectExecutionBoundary().Phase
        );
    }

    [Fact]
    public async Task ResumeStarted_DefaultRefusesBeforeClientCreation() {
        var completionFactory = new TrackingCompletionClientFactory(
            "must not dispatch"
        );
        var normalizer = new TrackingNormalizer();
        await using var host = GalateaTestHost.Create(
            completionFactory,
            normalizer
        );
        string sessionPath = GetSessionPath(host);
        CompletionConnectionConfig connection = GetConnection(host);
        EventAddress startedHead = await CreateRecoveryBoundaryAsync(
            sessionPath,
            connection,
            completionFactory.Client,
            "AfterCompletionAttemptStartedCommitted",
            SessionExecutionPhase.AwaitingCompletion
        );
        File.Delete(RecapEpochConfigLoader.GetCanonicalPath(sessionPath));
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/chat/turns/resume",
            new ResumeTurnRequest(
                EventAddressTextCodec.Format(startedHead),
                ConnectionId: null,
                RestartUncertainCompletion: false
            )
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using JsonDocument body = await ReadJsonAsync(response);
        Assert.Equal(
            "uncertain-completion-restart-required",
            body.RootElement.GetProperty("code").GetString()
        );
        Assert.Equal(
            nameof(SessionExecutionPhase.AwaitingCompletion),
            body.RootElement.GetProperty("phase").GetString()
        );
        Assert.Equal(0, normalizer.NormalizeCallCount);
        Assert.Equal(0, completionFactory.CreateCallCount);
        Assert.Equal(0, completionFactory.Client.DispatchCallCount);
        Assert.False(File.Exists(
            RecapEpochConfigLoader.GetCanonicalPath(sessionPath)
        ));

        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        Assert.Equal(startedHead, session.Engine.ReadCurrentHead());
        CurrentTurnDto? current = await client
            .GetFromJsonAsync<CurrentTurnDto>(
                "/api/chat/turns/current"
            );
        Assert.NotNull(current);
        Assert.Equal("recovery-required", current!.Status);
        Assert.True(current.RestartRequired);
        Assert.Equal(
            EventAddressTextCodec.Format(startedHead),
            current.RecoveryHead
        );
    }

    [Fact]
    public async Task ResumeStarted_ExplicitExactHeadRestartCompletes() {
        var completionFactory = new TrackingCompletionClientFactory(
            "restarted answer"
        );
        var normalizer = new TrackingNormalizer();
        await using var host = GalateaTestHost.Create(
            completionFactory,
            normalizer
        );
        string sessionPath = GetSessionPath(host);
        CompletionConnectionConfig connection = GetConnection(host);
        EventAddress startedHead = await CreateRecoveryBoundaryAsync(
            sessionPath,
            connection,
            completionFactory.Client,
            "AfterCompletionAttemptStartedCommitted",
            SessionExecutionPhase.AwaitingCompletion
        );
        File.Delete(RecapEpochConfigLoader.GetCanonicalPath(sessionPath));
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/chat/turns/resume",
            new ResumeTurnRequest(
                EventAddressTextCodec.Format(startedHead),
                ConnectionId: null,
                RestartUncertainCompletion: true
            )
        );
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        StartTurnResponseDto? started = await response.Content
            .ReadFromJsonAsync<StartTurnResponseDto>();
        Assert.NotNull(started);

        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        GalateaLiveTurn liveTurn = Assert.IsType<GalateaLiveTurn>(
            service.FindTurn(session, started!.TurnId)
        );
        await Assert.IsAssignableFrom<Task>(liveTurn.RunTask)
            .WaitAsync(CompletionDeadline);

        Assert.Equal("completed", liveTurn.Status);
        Assert.Equal(0, normalizer.NormalizeCallCount);
        Assert.Equal(1, completionFactory.CreateCallCount);
        Assert.Equal(1, completionFactory.Client.DispatchCallCount);
        Assert.False(File.Exists(
            RecapEpochConfigLoader.GetCanonicalPath(sessionPath)
        ));
        Assert.Equal(
            SessionExecutionPhase.Idle,
            session.Engine.InspectExecutionBoundary().Phase
        );
        Assert.Equal(
            "restarted answer",
            session.Engine.ReadRecentCompletedTurns().Turns[^1]
                .TerminalAction.Message.GetFlattenedText()
        );
    }

    private static string GetSessionPath(GalateaTestHost host) =>
        host.Factory.Services
            .GetRequiredService<GalateaConfig>()
            .Users.Single().SessionDir;

    private static CompletionConnectionConfig GetConnection(
        GalateaTestHost host
    ) => host.Factory.Services
        .GetRequiredService<GalateaConfig>()
        .Connections.Single(static connection => connection.Id == "test");

    private static EventAddress AppendPendingObservation(
        string sessionPath
    ) {
        using var engine = SessionJournalEngine.Open(sessionPath);
        return engine.AppendObservation(
            GalateaUserMessageEnvelope.Wrap("already normalized")
        );
    }

    private static async Task<EventAddress> CreateRecoveryBoundaryAsync(
        string sessionPath,
        CompletionConnectionConfig connection,
        ICompletionClient client,
        string failpointName,
        SessionExecutionPhase expectedPhase
    ) {
        SessionRuntime runtime = CreateFixtureRuntime(
            connection,
            client
        );
        Assembly assembly = typeof(SessionJournalEngine).Assembly;
        Type failpointType = assembly.GetType(
            "Atelia.SessionJournal.SessionJournalFailpoint",
            throwOnError: true
        )!;
        Type hooksType = assembly.GetType(
            "Atelia.SessionJournal.SessionJournalTestHooks",
            throwOnError: true
        )!;
        object failpoint = Enum.Parse(failpointType, failpointName);
        ConstructorInfo hooksConstructor = Assert.Single(
            hooksType.GetConstructors(
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.NonPublic
            ),
            constructor => constructor.GetParameters().Length == 6
        );
        object hooks = hooksConstructor.Invoke([
            failpoint,
            null,
            null,
            null,
            null,
            null
        ]);
        MethodInfo openForTest = Assert.Single(
            typeof(SessionJournalEngine).GetMethods(
                BindingFlags.Static | BindingFlags.NonPublic
            ),
            method => {
                if (method.Name != "OpenForTest") { return false; }
                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 3
                    && parameters[0].ParameterType == typeof(string)
                    && parameters[1].ParameterType
                        == typeof(SessionRuntime)
                    && parameters[2].ParameterType == hooksType;
            }
        );
        using var engine = Assert.IsType<SessionJournalEngine>(
            openForTest.Invoke(null, [sessionPath, runtime, hooks])
        );

        Exception exception = await Assert.ThrowsAnyAsync<Exception>(
            () => engine.SendAsync(
                GalateaUserMessageEnvelope.Wrap("fixture observation"),
                CancellationToken.None
            )
        );
        Assert.Equal(
            "Atelia.SessionJournal.SessionJournalFailpointException",
            exception.GetType().FullName
        );
        SessionExecutionBoundaryInspection boundary =
            engine.InspectExecutionBoundary();
        Assert.Equal(expectedPhase, boundary.Phase);
        Assert.NotNull(boundary.Head);
        return boundary.Head!.Value;
    }

    private static async Task<EventAddress> CreateFailedBoundaryAsync(
        string sessionPath,
        CompletionConnectionConfig connection
    ) {
        var client = new KnownFailureClient();
        using var engine = SessionJournalEngine.Open(sessionPath);
        engine.UseRuntime(CreateFixtureRuntime(connection, client));
        await Assert.ThrowsAsync<SessionJournalTurnAbortedException>(
            () => engine.SendAsync(
                GalateaUserMessageEnvelope.Wrap("failed fixture turn"),
                CancellationToken.None
            )
        );
        SessionRuntimeRecoveryRequirements
            .FailedTurnMustBeAbandoned requirement = Assert.IsType<
                SessionRuntimeRecoveryRequirements
                    .FailedTurnMustBeAbandoned
            >(engine.InspectRuntimeRecoveryRequirements());
        Assert.Equal(1, client.DispatchCallCount);
        return requirement.FailedHead;
    }

    private static SessionRuntime CreateFixtureRuntime(
        CompletionConnectionConfig connection,
        ICompletionClient client
    ) {
        CompletionDispatchIdentity dispatch =
            CompletionDispatchIdentityFactory.Create(
                connection,
                client
            );
        return new SessionRuntime(
            client,
            CompletionTarget: new SessionCompletionTargetIdentity(
                dispatch.ConnectionId,
                dispatch.Kind,
                dispatch.ConnectionFingerprint,
                dispatch.RequestAdapterFingerprint
            ),
            MaxTokens: connection.MaxTokens,
            ContextCandidateSource: new EmptyLineageCandidateSource()
        );
    }

    private static async Task<StartTurnResponseDto> ResumeAsync(
        HttpClient client,
        EventAddress expectedHead,
        string? connectionId
    ) {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/chat/turns/resume",
            new ResumeTurnRequest(
                EventAddressTextCodec.Format(expectedHead),
                connectionId
            )
        );
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        StartTurnResponseDto? started = await response.Content
            .ReadFromJsonAsync<StartTurnResponseDto>();
        return Assert.IsType<StartTurnResponseDto>(started);
    }

    private static async Task LoginAsync(HttpClient client) {
        using HttpResponseMessage response =
            await GalateaTestHost.LoginAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response
    ) => JsonDocument.Parse(
        await response.Content.ReadAsStringAsync()
    );

    private sealed class EmptyLineageCandidateSource
        : ICoherentContextCandidateSource {
        public ValueTask<SessionContextCandidateSelection> SelectAsync(
            SessionContextSelectionRequest request,
            CancellationToken cancellationToken
        ) {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new SessionContextCandidateSelection(
                    SessionContextCandidateSelectionStatus.EmptyLineage,
                    Candidate: null
                )
            );
        }

        public ValueTask<SessionContextCandidate> MaterializeAsync(
            SessionContextCandidateDescriptor descriptor,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException(
            "An EmptyLineage fixture must not materialize a candidate."
        );
    }

    private sealed class TrackingCompletionClientFactory(
        string responseText = "unused"
    ) : ICompletionClientFactory {
        private int _createCallCount;

        internal TrackingCompletionClient Client { get; } = new(
            responseText
        );

        internal int CreateCallCount => Volatile.Read(
            ref _createCallCount
        );

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            ArgumentNullException.ThrowIfNull(connection);
            Interlocked.Increment(ref _createCallCount);
            return Client;
        }
    }

    private sealed class TrackingCompletionClient(string responseText)
        : ICompletionClient {
        private int _dispatchCallCount;

        public string Name => "galatea-recovery-test";

        public string ApiSpecId => "openai-chat-v1";

        internal int DispatchCallCount => Volatile.Read(
            ref _dispatchCallCount
        );

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _dispatchCallCount);
            observer?.OnTextDelta(responseText);
            return Task.FromResult(new CompletionResult(
                new ActionMessage([
                    new ActionBlock.Text(responseText)
                ]),
                new CompletionDescriptor(
                    Name,
                    ApiSpecId,
                    request.ModelId
                )
            ));
        }
    }

    private sealed class KnownFailureClient : ICompletionClient {
        private int _dispatchCallCount;

        public string Name => "galatea-known-failure-test";

        public string ApiSpecId => "openai-chat-v1";

        internal int DispatchCallCount => Volatile.Read(
            ref _dispatchCallCount
        );

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            ArgumentNullException.ThrowIfNull(request);
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _dispatchCallCount);
            return Task.FromResult(new CompletionResult(
                new ActionMessage([
                    new ActionBlock.Text("known failed output")
                ]),
                new CompletionDescriptor(
                    Name,
                    ApiSpecId,
                    request.ModelId
                ),
                termination: CompletionTermination.Failed(
                    "known-test-failure"
                )
            ));
        }
    }

    private sealed class TrackingNormalizer
        : IGalateaUserMessageNormalizer {
        private int _normalizeCallCount;

        internal int NormalizeCallCount => Volatile.Read(
            ref _normalizeCallCount
        );

        public bool ShouldNormalize(string userMessage) {
            _ = userMessage;
            return true;
        }

        public ValueTask<string> NormalizeAsync(
            string userMessage,
            CancellationToken ct
        ) {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _normalizeCallCount);
            return ValueTask.FromResult(userMessage);
        }
    }

    private sealed class TemporaryCallLogDirectory : IDisposable {
        internal TemporaryCallLogDirectory() {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "atelia-galatea-prepared-call-log-tests",
                Guid.NewGuid().ToString("N")
            );
        }

        internal string Path { get; }

        public void Dispose() {
            if (Directory.Exists(Path)) {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
