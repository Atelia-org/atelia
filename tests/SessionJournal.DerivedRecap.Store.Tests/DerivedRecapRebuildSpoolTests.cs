using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

public sealed class DerivedRecapRebuildSpoolTests : IDisposable {
    private readonly List<string> _paths = [];

    [Fact]
    public async Task SealedSpool_SurvivesTruthStoreReset_ThenDeletes()
    {
        RawFixture raw = CreateRawFixture(extraSetupCount: 20);
        using var engine = SessionJournalEngine.OpenReadOnly(raw.Path);
        DerivedRecapRebuildSpoolStore spool =
            DerivedRecapRebuildSpoolStore.Open(
                raw.Path,
                raw.RefId
            );
        string campaignId = await BuildSealedCampaignAsync(
            engine,
            spool,
            pageEventCount: 4
        );
        string campaignRoot = CampaignRoot(raw, campaignId);
        Assert.True(Directory.Exists(campaignRoot));
        Assert.DoesNotContain(
            "system-19",
            string.Join(
                "\n",
                Directory.EnumerateFiles(
                        campaignRoot,
                        "*.json",
                        SearchOption.AllDirectories
                    )
                    .Select(File.ReadAllText)
            ),
            StringComparison.Ordinal
        );

        DerivedRecapStore truthStore = DerivedRecapStore.Open(
            raw.Path,
            raw.RefId
        );
        await truthStore.CreateAsync();
        await truthStore.ResetAsync();
        Assert.True(Directory.Exists(campaignRoot));

        ISessionSelectedLineageAuditPageSnapshot snapshot =
            await spool.OpenSealedSnapshotAsync(campaignId);
        using SessionSelectedLineageForwardCursor cursor =
            engine.OpenSelectedLineageForwardCursor(snapshot);
        SessionSelectedLineageForwardRange range =
            Assert.IsType<SessionSelectedLineageForwardRange>(
                cursor.ReadNextRange(64)
            );
        Assert.True(range.IsFinal);
        SessionHistoryPlanningWindow window = cursor.Materialize(range);
        Assert.Equal(raw.Head, window.ObservedRawHead);

        EventAddress headBeforeDelete = engine.ReadCurrentHead()!.Value;
        cursor.Dispose();
        await spool.DeleteCampaignAsync(campaignId);
        Assert.False(Directory.Exists(campaignRoot));
        Assert.Equal(headBeforeDelete, engine.ReadCurrentHead());
    }

    [Fact]
    public async Task CrashAfterPageInstall_ResumesExactOrphanPage()
    {
        RawFixture raw = CreateRawFixture(extraSetupCount: 8);
        bool failOnce = true;
        var hooks = new RecapStoreTestHooks(
            AfterRebuildPageInstalledBeforeCheckpoint: () => {
                if (failOnce) {
                    failOnce = false;
                    throw new RebuildSpoolCrashException();
                }
            }
        );
        DerivedRecapRebuildSpoolStore spool =
            DerivedRecapRebuildSpoolStore.OpenForTest(
                raw.Path,
                raw.RefId,
                hooks
            );
        using var engine = SessionJournalEngine.OpenReadOnly(raw.Path);
        SessionSelectedLineageAuditSession initial =
            engine.BeginSelectedLineageAudit();
        DerivedRecapRebuildSpoolDescriptor descriptor =
            await spool.CreateCampaignAsync(
                NewCampaignId(),
                initial.Capture,
                Limits(pageEventCount: 3)
            );
        await using (DerivedRecapRebuildSpoolWriter writer =
                     await spool.OpenWriterAsync(
                         descriptor.CampaignId
                     )) {
            SessionSelectedLineageAuditPage page =
                initial.ReadNextPage(3);
            await Assert.ThrowsAsync<RebuildSpoolCrashException>(
                () => writer.AppendPageAsync(page).AsTask()
            );
            Assert.Equal(0, writer.Checkpoint.CommittedPageCount);
        }

        await using (DerivedRecapRebuildSpoolWriter resumedWriter =
                     await spool.OpenWriterAsync(
                         descriptor.CampaignId
                     )) {
            SessionSelectedLineageAuditSession resumed =
                engine.ResumeSelectedLineageAudit(
                    resumedWriter.Checkpoint.Descriptor.Capture,
                    resumedWriter.ReadCommittedPages()
                );
            while (!resumed.IsCaptureComplete) {
                SessionSelectedLineageAuditPage page =
                    resumed.ReadNextPage(
                        resumedWriter.Checkpoint.Descriptor.Limits
                            .PageEventCount
                    );
                await resumedWriter.AppendPageAsync(page);
            }
            await resumedWriter.SealAsync(resumed.Complete());
        }

        ISessionSelectedLineageAuditPageSnapshot snapshot =
            await spool.OpenSealedSnapshotAsync(
                descriptor.CampaignId
            );
        using SessionSelectedLineageForwardCursor cursor =
            engine.OpenSelectedLineageForwardCursor(snapshot);
        Assert.Equal(raw.Head, cursor.Authority.Capture.CapturedHead);
    }

    [Fact]
    public async Task PartialAndTamperedSpools_AreRejected()
    {
        RawFixture partialRaw = CreateRawFixture(extraSetupCount: 2);
        using (var partialEngine =
               SessionJournalEngine.OpenReadOnly(partialRaw.Path)) {
            DerivedRecapRebuildSpoolStore partialStore =
                DerivedRecapRebuildSpoolStore.Open(
                    partialRaw.Path,
                    partialRaw.RefId
                );
            SessionSelectedLineageAuditSession capture =
                partialEngine.BeginSelectedLineageAudit();
            DerivedRecapRebuildSpoolDescriptor descriptor =
                await partialStore.CreateCampaignAsync(
                    NewCampaignId(),
                    capture.Capture,
                    Limits(pageEventCount: 2)
                );
            await Assert.ThrowsAsync<InvalidDataException>(
                () => partialStore.OpenSealedSnapshotAsync(
                    descriptor.CampaignId
                ).AsTask()
            );
        }

        RawFixture corruptRaw = CreateRawFixture(extraSetupCount: 4);
        using var corruptEngine =
            SessionJournalEngine.OpenReadOnly(corruptRaw.Path);
        DerivedRecapRebuildSpoolStore corruptStore =
            DerivedRecapRebuildSpoolStore.Open(
                corruptRaw.Path,
                corruptRaw.RefId
            );
        string campaignId = await BuildSealedCampaignAsync(
            corruptEngine,
            corruptStore,
            pageEventCount: 2
        );
        string firstPage = Path.Combine(
            CampaignRoot(corruptRaw, campaignId),
            "pages",
            "00000000000000000000.json"
        );
        File.AppendAllText(firstPage, " ");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => corruptStore.OpenSealedSnapshotAsync(
                campaignId
            ).AsTask()
        );
    }

    [Fact]
    public async Task CanonicalAndCampaignBudgets_GateBeforeCommit()
    {
        RawFixture raw = CreateRawFixture(extraSetupCount: 20);
        using var engine = SessionJournalEngine.OpenReadOnly(raw.Path);
        DerivedRecapRebuildSpoolStore spool =
            DerivedRecapRebuildSpoolStore.Open(
                raw.Path,
                raw.RefId
            );
        SessionSelectedLineageAuditSession capture =
            engine.BeginSelectedLineageAudit();
        DerivedRecapRebuildSpoolDescriptor descriptor =
            await spool.CreateCampaignAsync(
                NewCampaignId(),
                capture.Capture,
                new DerivedRecapRebuildSpoolLimits(
                    PageEventCount: 1,
                    MaximumPageBytes: 4096,
                    MaximumEventCount: 100,
                    MaximumTotalEncodedBytes: 4096
                )
            );
        await using DerivedRecapRebuildSpoolWriter writer =
            await spool.OpenWriterAsync(descriptor.CampaignId);
        InvalidDataException? budgetError = null;
        while (!capture.IsCaptureComplete) {
            SessionSelectedLineageAuditPage page =
                capture.ReadNextPage(1);
            try {
                await writer.AppendPageAsync(page);
            }
            catch (InvalidDataException exception) {
                budgetError = exception;
                break;
            }
        }
        Assert.NotNull(budgetError);
        Assert.True(
            writer.Checkpoint.EncodedPageBytes
            <= descriptor.Limits.MaximumTotalEncodedBytes
        );
        Assert.False(File.Exists(Path.Combine(
            CampaignRoot(raw, descriptor.CampaignId),
            "pages",
            writer.Checkpoint.CommittedPageCount.ToString("D20")
                + ".json"
        )));
    }

    [Fact]
    public async Task DuplicateCheckpointProperty_IsRejectedStrictly()
    {
        RawFixture raw = CreateRawFixture(extraSetupCount: 2);
        using var engine = SessionJournalEngine.OpenReadOnly(raw.Path);
        DerivedRecapRebuildSpoolStore spool =
            DerivedRecapRebuildSpoolStore.Open(
                raw.Path,
                raw.RefId
            );
        SessionSelectedLineageAuditSession capture =
            engine.BeginSelectedLineageAudit();
        DerivedRecapRebuildSpoolDescriptor descriptor =
            await spool.CreateCampaignAsync(
                NewCampaignId(),
                capture.Capture,
                Limits(pageEventCount: 2)
            );
        string checkpointPath = Path.Combine(
            CampaignRoot(raw, descriptor.CampaignId),
            "checkpoint.json"
        );
        string canonical = File.ReadAllText(checkpointPath);
        File.WriteAllText(
            checkpointPath,
            "{\"schema\":\"duplicate\"," + canonical[1..]
        );

        await Assert.ThrowsAsync<InvalidDataException>(
            () => spool.OpenWriterAsync(
                descriptor.CampaignId
            ).AsTask()
        );
    }

    [Fact]
    public async Task SealedSnapshot_IsOnlyEvidence_WhenRawHeadChanges()
    {
        RawFixture raw = CreateRawFixture(extraSetupCount: 4);
        string campaignId;
        DerivedRecapRebuildSpoolStore spool =
            DerivedRecapRebuildSpoolStore.Open(
                raw.Path,
                raw.RefId
            );
        using (var captured =
               SessionJournalEngine.OpenReadOnly(raw.Path)) {
            campaignId = await BuildSealedCampaignAsync(
                captured,
                spool,
                pageEventCount: 2
            );
        }
        using (var writer = SessionJournalEngine.Open(raw.Path)) {
            _ = writer.AppendSystemPromptSetup("later-head");
        }

        using var reopened = SessionJournalEngine.OpenReadOnly(raw.Path);
        ISessionSelectedLineageAuditPageSnapshot snapshot =
            await spool.OpenSealedSnapshotAsync(campaignId);
        SessionSelectedLineageAuditChangedException error =
            Assert.Throws<SessionSelectedLineageAuditChangedException>(
                () => reopened.OpenSelectedLineageForwardCursor(
                    snapshot
                )
            );
        Assert.Equal(
            SessionSelectedLineageAuditChangeKind.RawHeadChanged,
            error.Kind
        );
    }

    [Fact]
    public async Task PerPageCanonicalByteLimit_GatesBeforeWrite()
    {
        RawFixture raw = CreateRawFixture(extraSetupCount: 1);
        using var engine = SessionJournalEngine.OpenReadOnly(raw.Path);
        DerivedRecapRebuildSpoolStore spool =
            DerivedRecapRebuildSpoolStore.Open(
                raw.Path,
                raw.RefId
            );
        SessionSelectedLineageAuditSession capture =
            engine.BeginSelectedLineageAudit();
        DerivedRecapRebuildSpoolDescriptor descriptor =
            await spool.CreateCampaignAsync(
                NewCampaignId(),
                capture.Capture,
                new DerivedRecapRebuildSpoolLimits(
                    PageEventCount: 1,
                    MaximumPageBytes: 64,
                    MaximumEventCount: 100,
                    MaximumTotalEncodedBytes: 64
                )
            );
        await using DerivedRecapRebuildSpoolWriter writer =
            await spool.OpenWriterAsync(descriptor.CampaignId);
        SessionSelectedLineageAuditPage page =
            capture.ReadNextPage(1);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => writer.AppendPageAsync(page).AsTask()
        );
        Assert.Equal(0, writer.Checkpoint.CommittedPageCount);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(
            CampaignRoot(raw, descriptor.CampaignId),
            "pages"
        )));
    }

    [Fact]
    public void Open_MissingOrBlankRepository_DoesNotProvisionPaths()
    {
        string missing = Path.Combine(
            Path.GetTempPath(),
            "atelia-missing-rebuild-spool",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(missing);

        Assert.Throws<DirectoryNotFoundException>(
            () => DerivedRecapRebuildSpoolStore.Open(
                missing,
                new RefId(1)
            )
        );
        Assert.False(Directory.Exists(missing));
        Assert.Throws<ArgumentException>(
            () => DerivedRecapRebuildSpoolStore.Open(
                " ",
                new RefId(1)
            )
        );
    }

    [Fact]
    public async Task DeleteAfterQuarantineCrash_IsRetryable()
    {
        RawFixture raw = CreateRawFixture(extraSetupCount: 2);
        bool failOnce = true;
        var hooks = new RecapStoreTestHooks(
            AfterRebuildDeleteQuarantineRename: () => {
                if (failOnce) {
                    failOnce = false;
                    throw new RebuildSpoolCrashException();
                }
            }
        );
        DerivedRecapRebuildSpoolStore spool =
            DerivedRecapRebuildSpoolStore.OpenForTest(
                raw.Path,
                raw.RefId,
                hooks
            );
        string campaignId;
        using (var engine =
               SessionJournalEngine.OpenReadOnly(raw.Path)) {
            campaignId = await BuildSealedCampaignAsync(
                engine,
                spool,
                pageEventCount: 2
            );
        }

        await Assert.ThrowsAsync<RebuildSpoolCrashException>(
            () => spool.DeleteCampaignAsync(campaignId).AsTask()
        );
        Assert.False(Directory.Exists(CampaignRoot(raw, campaignId)));

        await spool.DeleteCampaignAsync(campaignId);
        string refQuarantine = Path.Combine(
            raw.Path,
            "derived",
            "recap",
            "rebuild",
            "v1",
            "quarantine",
            raw.RefId.ToHexString()
        );
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            refQuarantine
        ));
    }

    [Fact]
    public async Task TemporaryFilesAreSwept_AndFuturePageIsRejected()
    {
        RawFixture tempRaw = CreateRawFixture(extraSetupCount: 1);
        using (var tempEngine =
               SessionJournalEngine.OpenReadOnly(tempRaw.Path)) {
            DerivedRecapRebuildSpoolStore tempStore =
                DerivedRecapRebuildSpoolStore.Open(
                    tempRaw.Path,
                    tempRaw.RefId
                );
            SessionSelectedLineageAuditSession capture =
                tempEngine.BeginSelectedLineageAudit();
            DerivedRecapRebuildSpoolDescriptor descriptor =
                await tempStore.CreateCampaignAsync(
                    NewCampaignId(),
                    capture.Capture,
                    Limits(pageEventCount: 2)
                );
            string temporary = Path.Combine(
                CampaignRoot(tempRaw, descriptor.CampaignId),
                ".checkpoint.json.crash.tmp"
            );
            File.WriteAllBytes(temporary, new byte[1024 * 1024]);
            await using DerivedRecapRebuildSpoolWriter writer =
                await tempStore.OpenWriterAsync(
                    descriptor.CampaignId
                );
            Assert.False(File.Exists(temporary));
        }

        RawFixture futureRaw = CreateRawFixture(extraSetupCount: 1);
        using var futureEngine =
            SessionJournalEngine.OpenReadOnly(futureRaw.Path);
        DerivedRecapRebuildSpoolStore futureStore =
            DerivedRecapRebuildSpoolStore.Open(
                futureRaw.Path,
                futureRaw.RefId
            );
        SessionSelectedLineageAuditSession futureCapture =
            futureEngine.BeginSelectedLineageAudit();
        DerivedRecapRebuildSpoolDescriptor futureDescriptor =
            await futureStore.CreateCampaignAsync(
                NewCampaignId(),
                futureCapture.Capture,
                Limits(pageEventCount: 2)
            );
        string pagesRoot = Path.Combine(
            CampaignRoot(futureRaw, futureDescriptor.CampaignId),
            "pages"
        );
        File.WriteAllText(
            Path.Combine(pagesRoot, "00000000000000000005.json"),
            "{}"
        );

        await Assert.ThrowsAsync<InvalidDataException>(
            () => futureStore.OpenWriterAsync(
                futureDescriptor.CampaignId
            ).AsTask()
        );
    }

    public void Dispose() {
        foreach (string path in _paths) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch {
                // Best-effort cleanup.
            }
        }
    }

    private async ValueTask<string> BuildSealedCampaignAsync(
        SessionJournalEngine engine,
        DerivedRecapRebuildSpoolStore spool,
        int pageEventCount
    ) {
        SessionSelectedLineageAuditSession capture =
            engine.BeginSelectedLineageAudit();
        DerivedRecapRebuildSpoolDescriptor descriptor =
            await spool.CreateCampaignAsync(
                NewCampaignId(),
                capture.Capture,
                Limits(pageEventCount)
            );
        await using DerivedRecapRebuildSpoolWriter writer =
            await spool.OpenWriterAsync(descriptor.CampaignId);
        while (!capture.IsCaptureComplete) {
            SessionSelectedLineageAuditPage page =
                capture.ReadNextPage(pageEventCount);
            await writer.AppendPageAsync(page);
        }
        await writer.SealAsync(capture.Complete());
        return descriptor.CampaignId;
    }

    private RawFixture CreateRawFixture(int extraSetupCount) {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-rebuild-spool-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        RefId refId;
        EventAddress head;
        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        )) {
            refId = engine.BranchRefId;
            for (int index = 0;
                 index < extraSetupCount;
                 index++) {
                _ = engine.AppendSystemPromptSetup($"system-{index}");
            }
            head = engine.ReadCurrentHead()!.Value;
        }
        return new RawFixture(path, refId, head);
    }

    private static DerivedRecapRebuildSpoolLimits Limits(
        int pageEventCount
    ) => new(
        pageEventCount,
        MaximumPageBytes: 512 * 1024,
        MaximumEventCount: 100_000,
        MaximumTotalEncodedBytes: 32 * 1024 * 1024
    );

    private static string NewCampaignId() =>
        Guid.NewGuid().ToString("N");

    private static string CampaignRoot(
        RawFixture raw,
        string campaignId
    ) => Path.Combine(
        raw.Path,
        "derived",
        "recap",
        "rebuild",
        "v1",
        "campaigns",
        raw.RefId.ToHexString(),
        campaignId
    );

    private sealed record RawFixture(
        string Path,
        RefId RefId,
        EventAddress Head
    );

    private sealed class RebuildSpoolCrashException : Exception;
}
