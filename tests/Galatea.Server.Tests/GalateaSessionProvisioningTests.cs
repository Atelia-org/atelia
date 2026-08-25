using System.Net;
using System.Net.Http.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaSessionProvisioningTests {
    [Fact]
    public async Task MissingCreateIfMissing_ConcurrentlyCreatesOneRawIdleRepositoryFromExactDefault() {
        var factory = new CountingCompletionClientFactory();
        CompletionConnectionConfig decoy = Connection(
            "first",
            "model-decoy",
            "surface-decoy"
        );
        CompletionConnectionConfig selected = Connection(
            "selected",
            "model-selected",
            "surface-selected"
        );
        await using var host = GalateaTestHost.CreateMissingSession(
            factory,
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [decoy, selected],
            defaultConnectionId: selected.Id,
            systemPrompt: "resolved prompt"
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();

        Task<UserSessionHost>[] requests = [
            .. Enumerable.Range(0, 16).Select(_ =>
                service.GetSessionAsync("alice", CancellationToken.None))
        ];
        UserSessionHost[] sessions = await Task.WhenAll(requests);

        UserSessionHost session = sessions[0];
        Assert.All(sessions, value => Assert.Same(session, value));
        Assert.True(Directory.Exists(host.SessionDirectory));
        Assert.False(Directory.Exists(Path.Combine(
            host.SessionDirectory,
            "derived"
        )));
        SessionExecutionBoundaryInspection boundary =
            session.Engine.InspectExecutionBoundary();
        Assert.Equal(SessionExecutionPhase.Idle, boundary.Phase);
        var head = Assert.IsType<Atelia.EventJournal.EventAddress>(
            boundary.Head
        );
        SessionGoverningSetup governing =
            session.Engine.ResolveGoverningSetup(head);
        Assert.Equal(selected.ModelId, governing.RuntimeConfig.ModelId);
        Assert.Equal(
            selected.CompletionSurfaceId,
            governing.RuntimeConfig.CompletionSurfaceId
        );
        Assert.Equal("resolved prompt", governing.SystemPrompt);

        SessionCurrentLineageSnapshot lineage =
            session.Engine.ReadCurrentLineageHeaders();
        Assert.Equal(3, lineage.HeadToRoot.Count);
        Assert.Equal(
            [
                SessionEventKind.SessionCreated,
                SessionEventKind.SystemPromptSetup,
                SessionEventKind.RuntimeConfigSetup
            ],
            lineage.HeadToRoot.Select(static value => value.Kind)
        );

        RecentTurnsResponseDto recent = await service.GetRecentTurnsAsync(
            session,
            CancellationToken.None
        );
        Assert.Empty(recent.Turns);
        RecapGridReadinessSnapshotDto readiness = Assert.IsType<
            RecapGridReadinessSnapshotDto
        >(recent.RecapGridReadiness);
        Assert.Equal("exact", readiness.Freshness);
        Assert.Equal("unprovisioned", readiness.State);
        Assert.Equal(0, factory.CreateCallCount);
    }

    [Fact]
    public async Task MissingExistingOnly_FailsWithoutCreatingPath() {
        await using var host = GalateaTestHost.CreateMissingSession(
            new CountingCompletionClientFactory(),
            DisabledGalateaUserMessageNormalizer.Instance,
            GalateaSessionProvisioning.ExistingOnly
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();

        GalateaSessionUnavailableException failure =
            await Assert.ThrowsAsync<GalateaSessionUnavailableException>(
                () => service.GetSessionAsync(
                    "alice",
                    CancellationToken.None
                )
            );

        Assert.Equal("session-unprovisioned", failure.Code);
        Assert.False(File.Exists(host.SessionDirectory));
        Assert.False(Directory.Exists(host.SessionDirectory));
    }

    [Fact]
    public async Task MissingCreateIfMissing_InMaintenanceModeFailsWithoutCreatingPath() {
        await using var host = GalateaTestHost.CreateMissingSession(
            new CountingCompletionClientFactory(),
            DisabledGalateaUserMessageNormalizer.Instance,
            GalateaSessionProvisioning.CreateIfMissing,
            maintenanceMode: true
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();

        GalateaSessionUnavailableException failure =
            await Assert.ThrowsAsync<GalateaSessionUnavailableException>(
                () => service.GetSessionAsync(
                    "alice",
                    CancellationToken.None
                )
            );

        Assert.Equal("session-unprovisioned", failure.Code);
        Assert.False(File.Exists(host.SessionDirectory));
        Assert.False(Directory.Exists(host.SessionDirectory));
    }

    [Theory]
    [InlineData("empty-directory")]
    [InlineData("incomplete-directory")]
    [InlineData("file")]
    public async Task ExistingNonRepositoryPath_IsNeverAdoptedOrOverwritten(
        string shape
    ) {
        await using var host = GalateaTestHost.CreateMissingSession(
            new CountingCompletionClientFactory(),
            DisabledGalateaUserMessageNormalizer.Instance
        );
        const string Sentinel = "operator-owned sentinel";
        switch (shape) {
            case "empty-directory":
                Directory.CreateDirectory(host.SessionDirectory);
                break;
            case "incomplete-directory":
                Directory.CreateDirectory(host.SessionDirectory);
                File.WriteAllText(
                    Path.Combine(host.SessionDirectory, "sentinel.txt"),
                    Sentinel
                );
                break;
            case "file":
                File.WriteAllText(host.SessionDirectory, Sentinel);
                break;
            default:
                throw new InvalidOperationException(shape);
        }
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();

        GalateaSessionUnavailableException failure =
            await Assert.ThrowsAsync<GalateaSessionUnavailableException>(
                () => service.GetSessionAsync(
                    "alice",
                    CancellationToken.None
                )
            );
        Assert.Equal("session-unprovisioned", failure.Code);

        if (shape == "file") {
            Assert.Equal(Sentinel, File.ReadAllText(host.SessionDirectory));
        }
        else if (shape == "incomplete-directory") {
            Assert.Equal(
                Sentinel,
                File.ReadAllText(Path.Combine(
                    host.SessionDirectory,
                    "sentinel.txt"
                ))
            );
            Assert.Single(Directory.EnumerateFileSystemEntries(
                host.SessionDirectory
            ));
        }
        else {
            Assert.Empty(Directory.EnumerateFileSystemEntries(
                host.SessionDirectory
            ));
        }
    }

    [Fact]
    public async Task ExistingValidCreateIfMissing_OnlyOpensAndPreservesSetup() {
        await using var host = GalateaTestHost.CreateMissingSession(
            new CountingCompletionClientFactory(),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [Connection(
                "test",
                "current-model",
                "current-surface"
            )],
            systemPrompt: "current prompt"
        );
        Atelia.EventJournal.EventAddress originalHead;
        using (SessionJournalEngine created = SessionJournalEngine.Create(
                   host.SessionDirectory,
                   new SessionCreateOptions(
                       "original-model",
                       "original prompt",
                       "original-surface"
                   ))) {
            originalHead = Assert.IsType<
                Atelia.EventJournal.EventAddress
            >(created.ReadCurrentHead());
        }
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();

        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );

        Assert.Equal(originalHead, session.Engine.ReadCurrentHead());
        SessionGoverningSetup setup =
            session.Engine.ResolveGoverningSetup(originalHead);
        Assert.Equal("original-model", setup.RuntimeConfig.ModelId);
        Assert.Equal(
            "original-surface",
            setup.RuntimeConfig.CompletionSurfaceId
        );
        Assert.Equal("original prompt", setup.SystemPrompt);
        Assert.Equal(
            3,
            session.Engine.ReadCurrentLineageHeaders().HeadToRoot.Count
        );
    }

    [Fact]
    public async Task FailedLazy_IsEvictedSoExternalProvisionCanRetryInSameService() {
        await using var host = GalateaTestHost.CreateMissingSession(
            new CountingCompletionClientFactory(),
            DisabledGalateaUserMessageNormalizer.Instance,
            GalateaSessionProvisioning.ExistingOnly
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        _ = await Assert.ThrowsAsync<GalateaSessionUnavailableException>(
            () => service.GetSessionAsync(
                "alice",
                CancellationToken.None
            )
        );
        using (SessionJournalEngine provisioned =
               SessionJournalEngine.Create(
                   host.SessionDirectory,
                   new SessionCreateOptions(
                       "model-a",
                       "test system prompt",
                       "openai-chat/strict"
                   ))) {
        }

        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );

        Assert.Equal(
            SessionExecutionPhase.Idle,
            session.Engine.InspectExecutionBoundary().Phase
        );
    }

    [Fact]
    public async Task HttpRecentTurns_FirstAuthenticatedSessionUseCreatesRawRepository() {
        var factory = new CountingCompletionClientFactory();
        await using var host = GalateaTestHost.CreateMissingSession(
            factory,
            DisabledGalateaUserMessageNormalizer.Instance
        );
        using HttpClient client = host.CreateClient();

        using HttpResponseMessage login =
            await GalateaTestHost.LoginAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.False(Directory.Exists(host.SessionDirectory));

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/recent-turns"
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        RecentTurnsResponseDto? recent = await response.Content
            .ReadFromJsonAsync<RecentTurnsResponseDto>();
        Assert.NotNull(recent);
        Assert.Empty(recent!.Turns);
        RecapGridReadinessSnapshotDto readiness = Assert.IsType<
            RecapGridReadinessSnapshotDto
        >(recent.RecapGridReadiness);
        Assert.Equal("unprovisioned", readiness.State);
        Assert.True(Directory.Exists(host.SessionDirectory));
        Assert.Equal(0, factory.CreateCallCount);
    }

    private static CompletionConnectionConfig Connection(
        string id,
        string modelId,
        string completionSurfaceId
    ) => new(
        id,
        "openai-chat",
        modelId,
        completionSurfaceId,
        "http://localhost:8000/",
        ApiKey: "test-key"
    );

    private sealed class CountingCompletionClientFactory
        : ICompletionClientFactory {
        private int _createCallCount;

        internal int CreateCallCount => Volatile.Read(
            ref _createCallCount
        );

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            _ = connection;
            Interlocked.Increment(ref _createCallCount);
            throw new InvalidOperationException(
                "Session provisioning must not create a Completion client."
            );
        }
    }
}
