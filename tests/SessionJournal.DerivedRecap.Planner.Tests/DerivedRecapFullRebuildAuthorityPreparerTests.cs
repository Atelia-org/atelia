using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

public sealed class DerivedRecapFullRebuildAuthorityPreparerTests
    : IDisposable {
    private readonly List<string> _paths = [];

    [Fact]
    public async Task ExplicitCampaign_BeginsWithoutScanThenResumesAndReadsForward()
    {
        string path = NewPath();
        var expected = new List<EventAddress>();
        RefId refId;
        using (var writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-a",
                "system-a",
                "surface-a"
            )
        )) {
            refId = writer.BranchRefId;
            for (int index = 0; index < 520; index++) {
                expected.Add(writer.AppendSystemPromptSetup(
                    $"system-{index}"
                ));
            }
        }

        using var engine = SessionJournalEngine.OpenReadOnly(path);
        DerivedRecapRebuildSpoolStore spool =
            DerivedRecapRebuildSpoolStore.Open(path, refId);
        SessionJournalReadDiagnostics before =
            engine.CaptureReadDiagnostics();
        DerivedRecapRebuildSpoolDescriptor descriptor =
            await DerivedRecapFullRebuildAuthorityPreparer.BeginAsync(
                engine,
                spool,
                new DerivedRecapRebuildSpoolLimits(
                    PageEventCount: 64,
                    MaximumPageBytes: 512 * 1024,
                    MaximumEventCount: 10_000,
                    MaximumTotalEncodedBytes: 16 * 1024 * 1024
                )
            );
        SessionJournalReadDiagnostics beginReads =
            engine.CaptureReadDiagnostics() - before;

        Assert.Equal(0, beginReads.HeaderPreviewReadCount);
        Assert.Equal(0, beginReads.PayloadReadCount);
        await using (DerivedRecapRebuildSpoolWriter unstarted =
                     await spool.OpenWriterAsync(
                         descriptor.CampaignId
                     )) {
            Assert.Equal(0, unstarted.Checkpoint.CommittedPageCount);
            Assert.Equal(0, unstarted.Checkpoint.EventCount);
        }

        DerivedRecapFullRebuildAuthorityPreparation prepared =
            await DerivedRecapFullRebuildAuthorityPreparer.ResumeAsync(
                engine,
                spool,
                descriptor.CampaignId
            );
        Assert.Equal(523, prepared.RawAuthority.EventCount);
        Assert.Equal(64, prepared.RawAuthority.MaximumResidentEntryCount);

        using SessionSelectedLineageForwardCursor cursor =
            await DerivedRecapFullRebuildAuthorityPreparer
                .OpenForwardCursorAsync(
                    engine,
                    spool,
                    descriptor.CampaignId
                );
        var observed = new List<EventAddress>();
        while (cursor.ReadNextRange(73) is { } range) {
            SessionHistoryPlanningWindow window =
                cursor.Materialize(range);
            observed.AddRange(window.RawAddresses);
        }

        Assert.Equal(expected, observed);
        Assert.Equal(
            prepared.RawAuthority.Capture.CapturedHead,
            observed[^1]
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
                // Best-effort test cleanup.
            }
        }
    }

    private string NewPath() {
        string root = Directory.Exists("/dev/shm")
            ? "/dev/shm"
            : Path.GetTempPath();
        string path = Path.Combine(
            root,
            "atelia-recap-full-rebuild-authority-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }
}
