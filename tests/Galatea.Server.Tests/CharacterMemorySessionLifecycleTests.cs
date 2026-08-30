using System.Text;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.SessionJournal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class CharacterMemorySessionLifecycleTests {
    [Fact]
    public async Task NullBindingLeavesCorruptSentinelPathUntouched() {
        byte[] sentinel = Encoding.UTF8.GetBytes(
            "not-a-character-memory-store\n"
        );
        await using GalateaTestHost host = GalateaTestHost.Create(
            new NeverCalledCompletionClientFactory(),
            DisabledGalateaUserMessageNormalizer.Instance
        );
        Directory.CreateDirectory(Path.GetDirectoryName(
            host.CharacterMemoryStateDirectory
        )!);
        File.WriteAllBytes(host.CharacterMemoryStateDirectory, sentinel);

        UserSessionHost session = await GetSessionAsync(host);

        Assert.Null(session.CharacterMemoryReconciler);
        Assert.True(File.Exists(host.CharacterMemoryStateDirectory));
        Assert.False(Directory.Exists(host.CharacterMemoryStateDirectory));
        Assert.Equal(
            sentinel,
            File.ReadAllBytes(host.CharacterMemoryStateDirectory)
        );
    }

    [Fact]
    public async Task MaintenanceModeLeavesEnabledCorruptSentinelUntouched() {
        byte[] sentinel = Encoding.UTF8.GetBytes(
            "maintenance-must-not-open-this-store\n"
        );
        await using GalateaTestHost host = GalateaTestHost.Create(
            new NeverCalledCompletionClientFactory(),
            DisabledGalateaUserMessageNormalizer.Instance,
            maintenanceMode: true,
            characterNoteExtractorConnectionId: "test"
        );
        Directory.CreateDirectory(Path.GetDirectoryName(
            host.CharacterMemoryStateDirectory
        )!);
        File.WriteAllBytes(host.CharacterMemoryStateDirectory, sentinel);

        UserSessionHost session = await GetSessionAsync(host);

        Assert.Null(session.CharacterMemoryReconciler);
        Assert.True(File.Exists(host.CharacterMemoryStateDirectory));
        Assert.False(Directory.Exists(host.CharacterMemoryStateDirectory));
        Assert.Equal(
            sentinel,
            File.ReadAllBytes(host.CharacterMemoryStateDirectory)
        );
    }

    [Fact]
    public async Task EnabledMissingStoreCreatesFromExactAttachBaseline() {
        await using GalateaTestHost host = GalateaTestHost.Create(
            new NeverCalledCompletionClientFactory(),
            DisabledGalateaUserMessageNormalizer.Instance,
            characterNoteExtractorConnectionId: "test"
        );
        CharacterMemoryStoreBaseline expectedBaseline = ReadBaseline(
            host.SessionDirectory
        );
        Assert.False(Path.Exists(host.CharacterMemoryStateDirectory));

        UserSessionHost session = await GetSessionAsync(host);

        CharacterNoteDefaultPodReconciler reconciler = Assert.IsType<
            CharacterNoteDefaultPodReconciler
        >(session.CharacterMemoryReconciler);
        CharacterMemoryStatusSnapshot status =
            reconciler.ReadStatusSnapshot();
        Assert.Equal(CharacterMemoryStoreState.Ready, status.StoreState);
        Assert.Equal("alice", status.Owner.UserId);
        Assert.Equal(
            CharacterMemorySessionComposition.CreateSessionRepositoryId(
                host.SessionDirectory
            ),
            status.Owner.SessionRepositoryId
        );
        Assert.StartsWith("cmsr1-", status.Owner.SessionRepositoryId);
        Assert.False(status.Owner.SessionRepositoryId.StartsWith(
            "gdsr1-",
            StringComparison.Ordinal
        ));
        Assert.Equal(expectedBaseline, status.Baseline);
        Assert.True(File.Exists(Path.Combine(
            host.CharacterMemoryStateDirectory,
            CharacterMemorySqliteStore.DatabaseFileName
        )));
        Assert.True(File.Exists(Path.Combine(
            host.CharacterMemoryStateDirectory,
            CharacterMemorySqliteStore.LockFileName
        )));
    }

    [Fact]
    public async Task ExistingStoreIsStrictlyOpenedWithStableOwner() {
        await using GalateaTestHost host = GalateaTestHost.Create(
            new NeverCalledCompletionClientFactory(),
            DisabledGalateaUserMessageNormalizer.Instance,
            characterNoteExtractorConnectionId: "test"
        );
        CharacterMemoryStoreOwner owner = Owner(host);
        CharacterMemoryStoreBaseline baseline = ReadBaseline(
            host.SessionDirectory
        );
        await CreateStoreAsync(host, owner, baseline);

        UserSessionHost session = await GetSessionAsync(host);

        CharacterMemoryStatusSnapshot status = Assert.IsType<
            CharacterNoteDefaultPodReconciler
        >(session.CharacterMemoryReconciler).ReadStatusSnapshot();
        Assert.Equal(owner, status.Owner);
        Assert.Equal(baseline, status.Baseline);
        Assert.Equal(CharacterMemoryStoreState.Ready, status.StoreState);
    }

    [Fact]
    public async Task ExistingOwnerMismatchFailsClosed() {
        await using GalateaTestHost host = GalateaTestHost.Create(
            new NeverCalledCompletionClientFactory(),
            DisabledGalateaUserMessageNormalizer.Instance,
            characterNoteExtractorConnectionId: "test"
        );
        CharacterMemoryStoreOwner expected = Owner(host);
        var wrongOwner = expected with {
            SessionRepositoryId = expected.SessionRepositoryId[..^1]
                + (expected.SessionRepositoryId[^1] == '0' ? "1" : "0")
        };
        Assert.NotEqual(expected, wrongOwner);
        await CreateStoreAsync(
            host,
            wrongOwner,
            ReadBaseline(host.SessionDirectory)
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.GetSessionAsync("alice", CancellationToken.None));
    }

    [Fact]
    public async Task SessionDisposalReleasesCharacterMemoryLifetimeLock() {
        GalateaTestHost host = GalateaTestHost.Create(
            new NeverCalledCompletionClientFactory(),
            DisabledGalateaUserMessageNormalizer.Instance,
            deleteFilesOnDispose: false,
            characterNoteExtractorConnectionId: "test"
        );
        bool disposed = false;
        try {
            UserSessionHost session = await GetSessionAsync(host);
            CharacterMemoryStatusSnapshot expected = Assert.IsType<
                CharacterNoteDefaultPodReconciler
            >(session.CharacterMemoryReconciler).ReadStatusSnapshot();
            Assert.ThrowsAny<IOException>(() =>
                CharacterMemorySqliteStore.OpenExisting(
                    host.CharacterMemoryStateDirectory,
                    expected.Owner
                ));

            await host.DisposeAsync();
            disposed = true;

            using CharacterMemorySqliteStore reopened =
                CharacterMemorySqliteStore.OpenExisting(
                    host.CharacterMemoryStateDirectory,
                    expected.Owner
                );
            Assert.Equal(expected, reopened.ReadStatusSnapshot());
        }
        finally {
            if (!disposed) {
                await host.DisposeAsync();
            }
            if (Directory.Exists(host.RootDirectory)) {
                Directory.Delete(host.RootDirectory, recursive: true);
            }
        }
    }

    private static async Task<UserSessionHost> GetSessionAsync(
        GalateaTestHost host
    ) {
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        return await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
    }

    private static CharacterMemoryStoreOwner Owner(
        GalateaTestHost host
    ) => new(
        "alice",
        CharacterMemorySessionComposition.CreateSessionRepositoryId(
            host.SessionDirectory
        )
    );

    private static CharacterMemoryStoreBaseline ReadBaseline(
        string sessionDirectory
    ) {
        using SessionJournalEngine engine =
            SessionJournalEngine.OpenReadOnly(sessionDirectory);
        return new CharacterMemoryStoreBaseline(
            engine.ReadView.ReadPhysicalAppendFrontier(),
            engine.ReadView.ReadCurrentHead() is { } head
                ? EventAddressTextCodec.Format(head)
                : null
        );
    }

    private static async Task CreateStoreAsync(
        GalateaTestHost host,
        CharacterMemoryStoreOwner owner,
        CharacterMemoryStoreBaseline baseline
    ) {
        Directory.CreateDirectory(Path.GetDirectoryName(
            host.CharacterMemoryStateDirectory
        )!);
        using CharacterNoteDefaultPodReconciler reconciler =
            await CharacterNoteDefaultPodReconciler.CreateNewAsync(
                host.CharacterMemoryStateDirectory,
                owner,
                baseline,
                DisabledCharacterNoteExtractor.Instance
            );
    }

    private sealed class NeverCalledCompletionClientFactory
        : ICompletionClientFactory {
        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) => throw new Xunit.Sdk.XunitException(
            $"Lifecycle composition must not create Completion client '{connection.Id}'."
        );
    }
}
