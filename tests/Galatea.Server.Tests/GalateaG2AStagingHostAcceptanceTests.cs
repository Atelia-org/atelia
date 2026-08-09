using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedRecap.Store;
using Atelia.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

internal sealed class GalateaG2AStagingFactAttribute : FactAttribute {
    internal const string StagingRepositoryEnvironment =
        "ATELIA_GALATEA_G2A_STAGING_REPO";

    public GalateaG2AStagingFactAttribute() {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(
                    StagingRepositoryEnvironment
                )
            )) {
            Skip = $"{StagingRepositoryEnvironment} must point to a "
                + "provisioned, Published G2A staging repository.";
        }
    }
}

[CollectionDefinition(
    CollectionName,
    DisableParallelization = true
)]
public sealed class GalateaG2AStagingCollection {
    public const string CollectionName =
        "Galatea G2A staging acceptance";
}

/// <summary>
/// Opt-in Host acceptance over an operator-provisioned staging base. The base
/// is never opened for mutation; every test uses a private full-tree clone.
/// Import, Recap publication, and real-provider calls remain production CLI
/// responsibilities and are deliberately not reproduced here.
/// </summary>
[Collection(GalateaG2AStagingCollection.CollectionName)]
public sealed class GalateaG2AStagingHostAcceptanceTests {
    private static readonly TimeSpan OperationDeadline =
        TimeSpan.FromSeconds(15);

    [GalateaG2AStagingFact]
    public async Task PublishedStaging_ShowsExactRecentSixAndMaterializationIsInvisible() {
        using StagingClone clone = StagingClone.Create();
        var factory = new RecordingCompletionClientFactory();
        await using var host = OpenHost(
            clone,
            factory,
            [ConnectionA]
        );
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);
        UserSessionHost session = await GetSessionAsync(host);

        SessionCompletedTurnsSnapshot all =
            session.Engine.ReadRecentCompletedTurns(100);
        Assert.Equal(71, all.Turns.Count);
        SessionCompletedTurnsSnapshot rawSix =
            session.Engine.ReadRecentCompletedTurns(
                GalateaHostService.RecentTurnLimit
            );
        RecentTurnsResponseDto before = await GetRecentAsync(client);
        Assert.Equal(6, before.Turns.Count);
        AssertRecentEqual(
            new RecentTurnsResponseDto(
                [
                    .. rawSix.Turns.Select(
                        GalateaRecentTurnDisplayAdapter.Project
                    )
                ],
                before.RewindLatestToken
            ),
            before
        );
        EventAddress beforeHead = session.Engine.ReadCurrentHead()
            ?? throw new InvalidDataException(
                "Published staging has no raw head."
            );
        Assert.Equal(
            EventAddressTextCodec.Format(beforeHead),
            before.RewindLatestToken
        );

        var source = new DerivedRecapContextCandidateSource(
            DerivedRecapEpochStore.Open(
                clone.SessionDirectory,
                session.Engine.BranchRefId
            ),
            session.Engine.ReadView
        );
        SessionContextCandidateSelection selected =
            await source.SelectAsync(
                new SessionContextSelectionRequest(beforeHead, 0),
                CancellationToken.None
            );
        Assert.Equal(
            SessionContextCandidateSelectionStatus.Selected,
            selected.Status
        );
        SessionContextCandidate materialized =
            await source.MaterializeAsync(
                Assert.IsType<SessionContextCandidateDescriptor>(
                    selected.Candidate
                ),
                CancellationToken.None
            );
        Assert.Equal(2, materialized.Contributions.Count);

        RecentTurnsResponseDto after = await GetRecentAsync(client);
        AssertRecentEqual(before, after);
        Assert.Equal(beforeHead, session.Engine.ReadCurrentHead());
        Assert.Equal(0, factory.DispatchCallCount);
    }

    [GalateaG2AStagingFact]
    public async Task ScriptedFreshTurn_ReopensThenExactUndoRestoresPriorRecent() {
        using StagingClone clone = StagingClone.Create();
        RecentTurnsResponseDto before;
        RecentTurnsResponseDto completed;
        string completedToken;
        var firstFactory = new RecordingCompletionClientFactory();
        await using (var first = OpenHost(
                         clone,
                         firstFactory,
                         [ConnectionA],
                         callLogDirectory: clone.CallLogDirectory
                     )) {
            using HttpClient client = first.CreateClient();
            await LoginAsync(client);
            GalateaHostService service = first.Factory.Services
                .GetRequiredService<GalateaHostService>();
            UserSessionHost session = await GetSessionAsync(first);
            before = await GetRecentAsync(client);

            await CompleteTurnAsync(
                client,
                service,
                session,
                "g2a scripted fresh observation",
                ConnectionA.Id
            );
            completed = await GetRecentAsync(client);
            completedToken = Assert.IsType<string>(
                completed.RewindLatestToken
            );
            Assert.Equal(
                "g2a scripted fresh observation",
                completed.Turns[0].UserText
            );
            Assert.Equal(1, firstFactory.DispatchCallCount);
        }

        var reopenedFactory = new RecordingCompletionClientFactory();
        await using var reopened = OpenHost(
            clone,
            reopenedFactory,
            [ConnectionA],
            callLogDirectory: clone.CallLogDirectory
        );
        using HttpClient reopenedClient = reopened.CreateClient();
        await LoginAsync(reopenedClient);
        AssertRecentEqual(
            completed,
            await GetRecentAsync(reopenedClient)
        );

        using HttpResponseMessage undo =
            await reopenedClient.PostAsJsonAsync(
                "/api/chat/turns/pop-latest",
                new PopLatestTurnRequestDto(completedToken)
            );
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        PopLatestTurnResponseDto? moved = await undo.Content
            .ReadFromJsonAsync<PopLatestTurnResponseDto>();
        Assert.NotNull(moved);
        Assert.Equal(
            "g2a scripted fresh observation",
            moved!.Turn.UserText
        );
        AssertTurnsEqual(before.Turns, moved.Recent.Turns);
        Assert.Null(moved.Recent.RewindLatestToken);
        Assert.Equal(0, reopenedFactory.DispatchCallCount);
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(clone.CallLogDirectory, "agent"),
            "*.json"
        ));
        AssertNoCallLogsInsideRepo(clone.SessionDirectory);
    }

    [GalateaG2AStagingFact]
    public async Task SetupOnlySuffix_KeepsVisibleTurnsButSuppressesRewindToken() {
        using StagingClone clone = StagingClone.Create();
        await using var host = OpenHost(
            clone,
            new RecordingCompletionClientFactory(),
            [ConnectionA]
        );
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);
        UserSessionHost session = await GetSessionAsync(host);
        RecentTurnsResponseDto before = await GetRecentAsync(client);
        string oldToken = Assert.IsType<string>(
            before.RewindLatestToken
        );
        EventAddress oldHead = session.Engine.ReadCurrentHead()
            ?? throw new InvalidDataException(
                "Staging repo has no raw head."
            );

        SessionDesiredSetupReconciliationResult.Ready ready =
            Assert.IsType<
                SessionDesiredSetupReconciliationResult.Ready
            >(session.Engine.ReconcileDesiredSetup(
                oldHead,
                new SessionDesiredSetup(
                    "setup-only-model",
                    "setup-only-surface",
                    "setup-only prompt"
                )
            ));
        Assert.True(
            ready.RuntimeConfigChanged
                || ready.SystemPromptChanged
        );
        EventAddress setupHead = session.Engine.ReadCurrentHead()
            ?? throw new InvalidDataException(
                "Setup reconciliation removed the raw head."
            );
        Assert.NotEqual(oldHead, setupHead);

        RecentTurnsResponseDto after = await GetRecentAsync(client);
        AssertTurnsEqual(before.Turns, after.Turns);
        Assert.Null(after.RewindLatestToken);
        using HttpResponseMessage staleUndo =
            await client.PostAsJsonAsync(
                "/api/chat/turns/pop-latest",
                new PopLatestTurnRequestDto(oldToken)
            );
        Assert.Equal(HttpStatusCode.Conflict, staleUndo.StatusCode);
        Assert.Equal(setupHead, session.Engine.ReadCurrentHead());
    }

    [GalateaG2AStagingFact]
    public async Task ConnectionSwitch_AffectsOnlyItsNewTurnAndGoverningSetup() {
        using StagingClone clone = StagingClone.Create();
        var factory = new RecordingCompletionClientFactory();
        await using var host = OpenHost(
            clone,
            factory,
            [ConnectionA, ConnectionB],
            defaultConnectionId: ConnectionA.Id
        );
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await GetSessionAsync(host);

        await CompleteTurnAsync(
            client,
            service,
            session,
            "g2a connection a observation",
            ConnectionA.Id
        );
        await CompleteTurnAsync(
            client,
            service,
            session,
            "g2a connection b observation",
            ConnectionB.Id
        );

        CompletionRequest requestA = factory.AgentRequestFor(
            ConnectionA.Id,
            "g2a connection a observation"
        );
        CompletionRequest requestB = factory.AgentRequestFor(
            ConnectionB.Id,
            "g2a connection b observation"
        );
        Assert.Equal(ConnectionA.ModelId, requestA.ModelId);
        Assert.Equal(ConnectionB.ModelId, requestB.ModelId);
        RecentTurnsResponseDto recent = await GetRecentAsync(client);
        Assert.Equal(
            [
                "g2a connection b observation",
                "g2a connection a observation"
            ],
            recent.Turns.Take(2).Select(
                static turn => turn.UserText
            )
        );
        EventAddress head = session.Engine.ReadCurrentHead()
            ?? throw new InvalidDataException(
                "Connection switch removed the raw head."
            );
        SessionGoverningSetup governing =
            session.Engine.ResolveGoverningSetup(head);
        Assert.Equal(
            ConnectionB.ModelId,
            governing.RuntimeConfig.ModelId
        );
        Assert.Equal(
            ConnectionB.CompletionSurfaceId,
            governing.RuntimeConfig.CompletionSurfaceId
        );
    }

    private static readonly CompletionConnectionConfig ConnectionA =
        Connection("scripted-a", "g2a-model-a", "g2a-surface-a");

    private static readonly CompletionConnectionConfig ConnectionB =
        Connection("scripted-b", "g2a-model-b", "g2a-surface-b");

    private static CompletionConnectionConfig Connection(
        string id,
        string modelId,
        string surfaceId
    ) => new(
        id,
        "openai-chat",
        modelId,
        surfaceId,
        "http://localhost:8000/",
        ApiKey: "test-key"
    );

    private static GalateaTestHost OpenHost(
        StagingClone clone,
        ICompletionClientFactory factory,
        IReadOnlyList<CompletionConnectionConfig> connections,
        string? defaultConnectionId = null,
        string? callLogDirectory = null
    ) => GalateaTestHost.OpenExisting(
        clone.SessionDirectory,
        connections,
        defaultConnectionId ?? connections[0].Id,
        factory,
        DisabledGalateaUserMessageNormalizer.Instance,
        systemPrompt: "G2A scripted host prompt",
        callLogDirectory
    );

    private static async Task LoginAsync(HttpClient client) {
        using HttpResponseMessage response =
            await GalateaTestHost.LoginAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<UserSessionHost> GetSessionAsync(
        GalateaTestHost host
    ) => await host.Factory.Services
        .GetRequiredService<GalateaHostService>()
        .GetSessionAsync("alice", CancellationToken.None);

    private static async Task<RecentTurnsResponseDto> GetRecentAsync(
        HttpClient client
    ) {
        RecentTurnsResponseDto? recent = await client
            .GetFromJsonAsync<RecentTurnsResponseDto>(
                "/api/recent-turns"
            );
        return Assert.IsType<RecentTurnsResponseDto>(recent);
    }

    private static async Task CompleteTurnAsync(
        HttpClient client,
        GalateaHostService service,
        UserSessionHost session,
        string message,
        string connectionId
    ) {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/chat/turns",
            new ChatStreamRequest(message, connectionId)
        );
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        StartTurnResponseDto? started = await response.Content
            .ReadFromJsonAsync<StartTurnResponseDto>();
        Assert.NotNull(started);
        GalateaLiveTurn turn = Assert.IsType<GalateaLiveTurn>(
            service.FindTurn(session, started!.TurnId)
        );
        await Assert.IsAssignableFrom<Task>(turn.RunTask)
            .WaitAsync(OperationDeadline);
        Assert.Equal("completed", turn.Status);
    }

    private static void AssertRecentEqual(
        RecentTurnsResponseDto expected,
        RecentTurnsResponseDto actual
    ) {
        Assert.Equal(
            expected.RewindLatestToken,
            actual.RewindLatestToken
        );
        AssertTurnsEqual(expected.Turns, actual.Turns);
    }

    private static void AssertTurnsEqual(
        IReadOnlyList<RecentTurnDto> expected,
        IReadOnlyList<RecentTurnDto> actual
    ) {
        Assert.Equal(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++) {
            Assert.Equal(expected[index], actual[index]);
        }
    }

    private static void AssertNoCallLogsInsideRepo(
        string sessionDirectory
    ) => Assert.DoesNotContain(
        Directory.EnumerateFiles(
            sessionDirectory,
            "*.json",
            SearchOption.AllDirectories
        ),
        static path => File.ReadAllText(path).Contains(
            "atelia.completion.call-log.",
            StringComparison.Ordinal
        )
    );

    private sealed class RecordingCompletionClientFactory
        : ICompletionClientFactory {
        private readonly ConcurrentDictionary<
            string,
            RecordingCompletionClient
        > _clients = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<RecordedRequest> _requests = new();

        internal int DispatchCallCount => _requests.Count;

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            ArgumentNullException.ThrowIfNull(connection);
            return _clients.GetOrAdd(
                connection.Id,
                id => new RecordingCompletionClient(
                    id,
                    request => _requests.Enqueue(
                        new RecordedRequest(id, request)
                    )
                )
            );
        }

        internal CompletionRequest AgentRequestFor(
            string connectionId,
            string userText
        ) =>
            Assert.Single(
                _requests,
                entry => string.Equals(
                        entry.ConnectionId,
                        connectionId,
                        StringComparison.Ordinal
                    )
                    && entry.Request.PromptPrefix.SharedContextMessages
                        .OfType<ObservationMessage>()
                        .Any(observation =>
                            observation.Content?.Contains(
                                userText,
                                StringComparison.Ordinal
                            ) == true
                        )
            ).Request;
    }

    private sealed class RecordingCompletionClient(
        string connectionId,
        Action<CompletionRequest> record
    ) : ICompletionClient {
        public string Name => $"galatea-g2a-{connectionId}";

        public string ApiSpecId => "openai-chat-v1";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            record(request);
            string reply = $"reply from {connectionId}";
            observer?.OnTextDelta(reply);
            return Task.FromResult(new CompletionResult(
                new ActionMessage([new ActionBlock.Text(reply)]),
                CompletionDescriptor.From(this, request)
            ));
        }
    }

    private sealed record RecordedRequest(
        string ConnectionId,
        CompletionRequest Request
    );

    internal sealed class StagingClone : IDisposable {
        private StagingClone(
            string rootDirectory,
            string sessionDirectory
        ) {
            RootDirectory = rootDirectory;
            SessionDirectory = sessionDirectory;
            CallLogDirectory = Path.Combine(
                rootDirectory,
                "call-logs"
            );
        }

        internal string RootDirectory { get; }

        internal string SessionDirectory { get; }

        internal string CallLogDirectory { get; }

        internal static StagingClone Create() {
            string? configured = Environment.GetEnvironmentVariable(
                GalateaG2AStagingFactAttribute
                    .StagingRepositoryEnvironment
            );
            return CreateFrom(configured!);
        }

        internal static StagingClone CreateFrom(
            string configuredSource,
            Func<string>? rootNameFactory = null,
            Action<string>? beforeRootCreate = null,
            string? cloneParentOverride = null
        ) {
            string source = TestDirectorySafety.Normalize(
                configuredSource
            );
            if (!Directory.Exists(source)) {
                throw new DirectoryNotFoundException(source);
            }
            if (cloneParentOverride is not null
                && !Path.IsPathFullyQualified(cloneParentOverride)) {
                throw new ArgumentException(
                    "The staging clone parent override must be an absolute "
                    + "path.",
                    nameof(cloneParentOverride)
                );
            }
            string cloneParent = TestDirectorySafety.Normalize(
                cloneParentOverride ?? Path.Combine(
                    Path.GetTempPath(),
                    "atelia-galatea-g2a-host-acceptance"
                )
            );
            string rootName = rootNameFactory?.Invoke()
                ?? Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(rootName)
                || !string.Equals(
                    rootName,
                    Path.GetFileName(rootName),
                    StringComparison.Ordinal
                )) {
                throw new ArgumentException(
                    "The staging clone root-name factory must return one "
                    + "file name."
                );
            }
            string root = Path.Combine(cloneParent, rootName);
            string destination = Path.Combine(root, "session-clone");
            TestDirectorySafety.EnsureDisjoint(source, destination);
            TestDirectorySafety
                .EnsureExistingPathChainHasNoReparsePoint(source);
            TestDirectorySafety
                .EnsureExistingPathChainHasNoReparsePoint(cloneParent);
            TestDirectorySafety
                .EnsureExistingPathChainHasNoReparsePoint(destination);
            if (Path.Exists(cloneParent)
                && !Directory.Exists(cloneParent)) {
                throw new InvalidDataException(
                    "The staging clone parent is not a directory: "
                    + cloneParent
                );
            }
            Directory.CreateDirectory(cloneParent);
            TestDirectorySafety
                .EnsureExistingPathChainHasNoReparsePoint(cloneParent);
            bool ownsRoot = false;
            try {
                beforeRootCreate?.Invoke(root);
                TestDirectorySafety.CreateDirectoryNew(root);
                ownsRoot = true;
                TestDirectorySafety.CreateDirectoryNew(destination);
                TestDirectorySafety.CopyTreeIntoOwnedEmptyDirectory(
                    source,
                    destination
                );
                return new StagingClone(root, destination);
            }
            catch {
                if (ownsRoot) {
                    TestDirectorySafety.DeleteOwnedTreeNoFollow(root);
                }
                throw;
            }
        }

        public void Dispose() {
            TestDirectorySafety.DeleteOwnedTreeNoFollow(RootDirectory);
        }
    }
}
