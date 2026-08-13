using Atelia.EventJournal;
using Atelia.SessionJournal;
using System.Text.Json;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaRecapGridReadinessTests : IDisposable {
    private readonly string _root = Path.Combine(
        Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(),
        "atelia-galatea-recap-grid-readiness-tests",
        Guid.NewGuid().ToString("N")
    );

    [Fact]
    public void UnprovisionedReadinessIsProviderFreeAndExact() {
        using SessionJournalEngine engine = SessionJournalEngine.Create(
            _root,
            new SessionCreateOptions("model", "surface", "readiness")
        );
        EventAddress head = Assert.IsType<EventAddress>(
            engine.ReadCurrentHead()
        );

        RecapGridReadinessSnapshotDto result =
            GalateaRecapGridReadiness.Inspect(
                engine.ReadView,
                head,
                CancellationToken.None
            );

        Assert.Equal("exact", result.Freshness);
        Assert.Equal("unprovisioned", result.State);
        Assert.Equal(EventAddressTextCodec.Format(head), result.ObservedRawHead);
        Assert.Equal("timeline-absent", result.Code);
        Assert.Null(result.Authority);
        Assert.False(Directory.Exists(Path.Combine(
            _root,
            "derived",
            "recap-grid"
        )));
    }

    [Fact]
    public void RawDriftMakesEvenUnprovisionedTerminalStale() {
        using SessionJournalEngine engine = SessionJournalEngine.Create(
            _root,
            new SessionCreateOptions("model", "surface", "readiness")
        );
        EventAddress head = Assert.IsType<EventAddress>(
            engine.ReadCurrentHead()
        );
        GalateaRecapGridReadiness.BeforeFinalRawFenceForTest.Value = () =>
            engine.AppendObservation("drift during readiness");
        try {
            RecapGridReadinessSnapshotDto result =
                GalateaRecapGridReadiness.Inspect(
                    engine.ReadView,
                    head,
                    CancellationToken.None
                );
            Assert.Equal("stale", result.Freshness);
            Assert.Equal("stale", result.State);
            Assert.Equal("authority-changed", result.Code);
        }
        finally {
            GalateaRecapGridReadiness.BeforeFinalRawFenceForTest.Value = null;
        }
    }

    [Fact]
    public void ReserveBootstrapDtoSerializesLongRowCountsWithoutNarrowing() {
        const long beyondInt32 = (long)int.MaxValue + 17;
        var evidence = new RecapGridReserveBootstrapEvidenceDto(
            "ref",
            "timeline",
            beyondInt32,
            "row",
            beyondInt32 + 1,
            "cadence",
            beyondInt32 + 2,
            "control",
            "store",
            2,
            24_000,
            60_000,
            beyondInt32 + 3,
            new RecapGridReserveBootstrapMetricsDto(
                beyondInt32 + 4,
                1,
                2,
                3));

        JsonElement json = JsonSerializer.SerializeToElement(
            evidence,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(
            beyondInt32,
            json.GetProperty("timelineGeneration").GetInt64());
        Assert.Equal(
            beyondInt32 + 3,
            json.GetProperty("verifiedRows").GetInt64());
        Assert.Equal(
            beyondInt32 + 4,
            json.GetProperty("metrics")
                .GetProperty("examinedTimelineRows")
                .GetInt64());
    }

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }
}
