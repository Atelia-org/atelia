using System.Security.Cryptography;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

public sealed class DerivedRecapReadOnlySurfaceTests {
    [Fact]
    public async Task MissingStoreReadSurfaceIsZeroTouch() {
        string path = NewPath();
        using SessionJournalEngine engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-a",
                "system-a",
                "surface-a"
            )
        );
        try {
            DerivedRecapStore store = DerivedRecapStore.Open(
                path,
                engine.BranchRefId
            );
            string derivedRoot = Path.Combine(path, "derived");
            Assert.False(Directory.Exists(derivedRoot));

            await AssertUnavailableAcrossReadSurfaceAsync(
                store,
                engine.ReadCurrentLineageHeaders()
            );

            Assert.False(Directory.Exists(derivedRoot));
        }
        finally {
            engine.Dispose();
            TryDelete(path);
        }
    }

    [Theory]
    [InlineData("locks-root")]
    [InlineData("store-header")]
    [InlineData("building-root")]
    public async Task DamagedScaffoldingIsNotRepairedByReads(
        string damage
    ) {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        string v4Root = Path.Combine(
            fixture.Path,
            "derived",
            "recap",
            "v4"
        );
        switch (damage) {
            case "locks-root":
                Directory.Delete(
                    Path.Combine(v4Root, "locks"),
                    recursive: true
                );
                break;
            case "store-header":
                File.Delete(Path.Combine(
                    fixture.Store.StoreRootPathForTest,
                    "store.json"
                ));
                break;
            case "building-root":
                Directory.Delete(Path.Combine(
                    fixture.Store.StoreRootPathForTest,
                    "building"
                ));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(damage));
        }
        string[] before = SnapshotTree(v4Root);
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();

        Assert.IsType<DerivedRecapSelection.StoreUnavailable>(
            await fixture.Store.SelectNthPreviousAsync(lineage, 0)
        );
        Assert.IsType<PublishedMembershipInspectionResult.StoreUnavailable>(
            await fixture.Store.InspectPublishedMembershipAsync(
                lineage.CapturedHead
            )
        );
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Store.ReadBuildingAsync(
                lineage.CapturedHead
            )
        );

        Assert.Equal(before, SnapshotTree(v4Root));
    }

    [Fact]
    public async Task MissingLockIsUnavailableAndIsNotRecreated() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        string v4Root = Path.Combine(
            fixture.Path,
            "derived",
            "recap",
            "v4"
        );
        string lockPath = GetLockPath(fixture);
        File.Delete(lockPath);
        string[] before = SnapshotTree(v4Root);
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();

        Assert.IsType<DerivedRecapSelection.StoreUnavailable>(
            await fixture.Store.SelectNthPreviousAsync(lineage, 0)
        );
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Store.ReadBuildingAsync(
                lineage.CapturedHead
            )
        );

        Assert.False(File.Exists(lockPath));
        Assert.Equal(before, SnapshotTree(v4Root));
    }

    [Fact]
    public async Task HealthyReadSurfaceCreatesNoFilesOrByteChanges() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        string v4Root = Path.Combine(
            fixture.Path,
            "derived",
            "recap",
            "v4"
        );
        string[] before = SnapshotTree(v4Root);

        await ExerciseHealthyReadSurfaceAsync(
            fixture.Store,
            fixture.Lineage()
        );

        Assert.Equal(before, SnapshotTree(v4Root));
    }

    [Fact]
    public async Task ExistingReadLockSerializesWithWriterLock() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        string lockPath = GetLockPath(fixture);
        using var heldWriter = new FileStream(
            lockPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None
        );

        Task<DerivedRecapSelection> read = fixture.Store
            .SelectNthPreviousAsync(fixture.Lineage(), 0)
            .AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        Assert.False(read.IsCompleted);

        heldWriter.Dispose();
        Assert.IsType<DerivedRecapSelection.EmptyLineage>(
            await read.WaitAsync(TimeSpan.FromSeconds(3))
        );
    }

    private static async ValueTask AssertUnavailableAcrossReadSurfaceAsync(
        DerivedRecapStore store,
        SessionCurrentLineageSnapshot lineage
    ) {
        PublishedRecapDescriptor published = Descriptor(store, lineage);
        BuildingDescriptor building = Building(store, lineage);
        var blockId = new RecapBlockId("roleplay.self");

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await store.ReadPublishedSourceAsync(
                published,
                [blockId]
            )
        );
        Assert.IsType<PublishedPlanReadResult.Unavailable>(
            await store.ReadPublishedPlanAsync(published)
        );
        Assert.IsType<PublishedPlanAtAnchorReadResult.Unavailable>(
            await store.ReadPublishedPlanAtAnchorAsync(
                lineage.CapturedHead
            )
        );
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await store.ReadBuildingAsync(
                lineage.CapturedHead
            )
        );
        Assert.IsType<CurrentLineageBuildingSelection.StoreUnavailable>(
            await store.SelectCurrentLineageBuildingAsync(lineage)
        );
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await store.InspectBuildingBlockAsync(
                building,
                blockId
            )
        );
        RecapPublishability publishability =
            await store.DiagnosePublishabilityAsync(
                lineage.CapturedHead,
                lineage
            );
        Assert.False(publishability.IsPublishable);
        Assert.Contains(
            publishability.Defects,
            static defect => defect.Code == "StoreUnavailable"
        );
        Assert.IsType<DerivedRecapSelection.StoreUnavailable>(
            await store.SelectNthPreviousAsync(lineage, 0)
        );
        Assert.IsType<PublishedMembershipInspectionResult.StoreUnavailable>(
            await store.InspectPublishedMembershipAsync(
                lineage.CapturedHead
            )
        );
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await store.MaterializeAsync(published)
        );
        PublishedRestoreInspectionResult.Unavailable restore =
            Assert.IsType<PublishedRestoreInspectionResult.Unavailable>(
                await store.InspectPublishedForRestoreAsync(
                    lineage.CapturedHead,
                    lineage
                )
            );
        Assert.Contains(
            restore.Defects,
            static defect => defect.Code == "StoreUnavailable"
        );
    }

    private static async ValueTask ExerciseHealthyReadSurfaceAsync(
        DerivedRecapStore store,
        SessionCurrentLineageSnapshot lineage
    ) {
        PublishedRecapDescriptor published = Descriptor(store, lineage);
        BuildingDescriptor building = Building(store, lineage);
        var blockId = new RecapBlockId("roleplay.self");

        Assert.IsType<PublishedRecapSourceReadResult.Missing>(
            await store.ReadPublishedSourceAsync(published, [blockId])
        );
        Assert.IsType<PublishedPlanReadResult.Unavailable>(
            await store.ReadPublishedPlanAsync(published)
        );
        Assert.IsType<PublishedPlanAtAnchorReadResult.Missing>(
            await store.ReadPublishedPlanAtAnchorAsync(
                lineage.CapturedHead
            )
        );
        Assert.IsType<BuildingReadResult.Missing>(
            await store.ReadBuildingAsync(lineage.CapturedHead)
        );
        Assert.IsType<CurrentLineageBuildingSelection.None>(
            await store.SelectCurrentLineageBuildingAsync(lineage)
        );
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await store.InspectBuildingBlockAsync(
                building,
                blockId
            )
        );
        Assert.False((await store.DiagnosePublishabilityAsync(
            lineage.CapturedHead,
            lineage
        )).IsPublishable);
        Assert.IsType<DerivedRecapSelection.EmptyLineage>(
            await store.SelectNthPreviousAsync(lineage, 0)
        );
        Assert.IsType<PublishedMembershipInspectionResult.Absent>(
            await store.InspectPublishedMembershipAsync(
                lineage.CapturedHead
            )
        );
        await Assert.ThrowsAnyAsync<IOException>(
            async () => await store.MaterializeAsync(published)
        );
        Assert.IsType<PublishedRestoreInspectionResult.Unavailable>(
            await store.InspectPublishedForRestoreAsync(
                lineage.CapturedHead,
                lineage
            )
        );
    }

    private static PublishedRecapDescriptor Descriptor(
        DerivedRecapStore store,
        SessionCurrentLineageSnapshot lineage
    ) => new(
        store.RefId,
        lineage.CapturedHead,
        new string('a', 64)
    );

    private static BuildingDescriptor Building(
        DerivedRecapStore store,
        SessionCurrentLineageSnapshot lineage
    ) => new(
        store.RefId,
        lineage.CapturedHead,
        new string('b', 64)
    );

    private static string GetLockPath(RecapStoreFixture fixture)
        => Path.Combine(
            fixture.Path,
            "derived",
            "recap",
            "v4",
            "locks",
            $"{fixture.Engine.BranchRefId.ToHexString()}.lock"
        );

    private static string[] SnapshotTree(string root) {
        if (!Directory.Exists(root)) {
            return [];
        }
        return [
            .. Directory.EnumerateFileSystemEntries(
                    root,
                    "*",
                    SearchOption.AllDirectories
                )
                .Select(path => Directory.Exists(path)
                    ? $"D:{Path.GetRelativePath(root, path)}"
                    : "F:"
                      + Path.GetRelativePath(root, path)
                      + ":"
                      + Convert.ToHexStringLower(
                          SHA256.HashData(File.ReadAllBytes(path))
                      ))
                .Order(StringComparer.Ordinal)
        ];
    }

    private static string NewPath()
        => Path.Combine(
            Path.GetTempPath(),
            "atelia-derived-recap-read-tests",
            Guid.NewGuid().ToString("N")
        );

    private static void TryDelete(string path) {
        try {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
        catch {
        }
    }
}
