using Atelia.SessionJournal.RecapGrid.Cadence;

namespace Atelia.SessionJournal.Cli;

internal static partial class RecapGridCommands {
    private static int CadenceInspect(CliOptions options) {
        options.EnsureOnly("input", "branch");
        using SessionJournalEngine engine = OpenBranch(options);
        RecapGridCadenceInspectResult result =
            RecapGridCadenceMaintenance.Inspect(
                engine.ReadView.Path,
                engine.BranchRefId);
        return result switch {
            RecapGridCadenceInspectResult.Available available => Print(
                "cadence.inspect",
                "available",
                DescribeCadenceSnapshot(available.Snapshot)),
            RecapGridCadenceInspectResult.Absent => Print(
                "cadence.inspect", "absent", exitCode: 2),
            RecapGridCadenceInspectResult.Busy => Print(
                "cadence.inspect", "busy", exitCode: 2),
            RecapGridCadenceInspectResult.UnsupportedSchema schema => Print(
                "cadence.inspect",
                "unsupported-schema",
                new { version = schema.Version },
                2),
            RecapGridCadenceInspectResult.PlatformUnsupported => Print(
                "cadence.inspect", "platform-unsupported", exitCode: 2),
            RecapGridCadenceInspectResult.Invalid invalid => Print(
                "cadence.inspect", "invalid", new {
                    code = invalid.Code,
                    detail = invalid.Detail
                }, 2),
            _ => Print(
                "cadence.inspect",
                "invalid",
                new { code = "CadenceInspectOutcomeInvalid" },
                2)
        };
    }

    private static int CadenceSetReserve(CliOptions options) {
        options.EnsureOnly(
            "input",
            "branch",
            "confirm-ref",
            "expected-generation",
            "expected-domain-digest",
            "minimum-recent-history-load");
        long generation = RequireNonNegativeLong(
            options,
            "expected-generation");
        var expectedDigest = new RecapGridCadenceDomainDigest(
            options.RequireSingle("expected-domain-digest"));
        long minimumRecentHistoryLoad = RequirePositiveLong(
            options,
            "minimum-recent-history-load");

        using SessionJournalEngine engine = OpenMutableBranch(options);
        RequireConfirmedRef(options, engine.BranchRefId);
        var expected = new RecapGridCadenceHeadRef(
            engine.BranchRefId,
            generation,
            expectedDigest);
        RecapGridCadenceOpenResult opened =
            RecapGridCadenceFactory.OpenMutable(engine);
        if (opened is not RecapGridCadenceOpenResult.Opened available) {
            return PrintCadenceSetReserve(
                MapCadenceSetReserveOpenFailure(opened));
        }
        using RecapGridCadenceHandle handle = available.Handle;
        RecapGridCadenceReadResult read = handle.Reader.ReadSnapshot();
        if (read is not RecapGridCadenceReadResult.Available current) {
            return PrintCadenceSetReserve(
                MapCadenceSetReserveReadFailure(read));
        }
        if (current.Snapshot.Head != expected) {
            return PrintCadenceSetReserve(("stale", null, 2));
        }
        RecapGridCadencePolicySpec prior = current.Snapshot.Policy;
        RecapGridCadencePolicySpec desired;
        try {
            desired = new RecapGridCadencePolicySpec(
                minimumRecentHistoryLoad,
                prior.PartitionAlgorithmId,
                prior.HistoryLoadEstimatorId,
                prior.TargetHistoryLoad,
                prior.MaxRawEvents,
                prior.MaxRenderedBytes);
        }
        catch (OverflowException) {
            return PrintCadenceSetReserve((
                "invalid",
                new { code = "CadenceReserveRangeInvalid" },
                2));
        }
        RecapGridCadenceCompareExchangeResult result =
            handle.Coordinator.CompareExchangePolicy(expected, desired);
        return PrintCadenceSetReserve(
            MapCadenceSetReserveCompareExchange(
                expected,
                minimumRecentHistoryLoad,
                result));
    }

    private static object DescribeCadenceSnapshot(
        RecapGridCadenceSnapshot snapshot
    ) => new {
        head = DescribeCadenceHead(snapshot.Head),
        policy = new {
            minimumRecentHistoryLoad =
                snapshot.Policy.MinimumRecentHistoryLoad,
            partitionAlgorithmId = snapshot.Policy.PartitionAlgorithmId,
            historyLoadEstimatorId = snapshot.Policy.HistoryLoadEstimatorId,
            targetHistoryLoad = snapshot.Policy.TargetHistoryLoad,
            maxRawEvents = snapshot.Policy.MaxRawEvents,
            maxRenderedBytes = snapshot.Policy.MaxRenderedBytes
        },
        canonicalBase64 = Convert.ToBase64String(
            snapshot.ToCanonicalBytes())
    };

    private static object DescribeCadenceHead(
        RecapGridCadenceHeadRef head
    ) => new {
        refId = head.RefId.ToHexString(),
        generation = head.Generation,
        domainDigest = head.DomainDigest.Value
    };

    internal static (string Status, object? Detail, int ExitCode)
        MapCadenceSetReserveOpenFailure(
            RecapGridCadenceOpenResult result
    ) => result switch {
        RecapGridCadenceOpenResult.Absent => ("absent", null, 2),
        RecapGridCadenceOpenResult.Busy => ("busy", null, 2),
        RecapGridCadenceOpenResult.UnsupportedSchema schema => (
            "unsupported-schema",
            new { version = schema.Version },
            2),
        RecapGridCadenceOpenResult.PlatformUnsupported
            => ("platform-unsupported", null, 2),
        RecapGridCadenceOpenResult.Invalid invalid => (
            "invalid",
            new { code = invalid.Code },
            2),
        _ => (
            "invalid",
            new { code = "CadenceOpenOutcomeInvalid" },
            2)
    };

    internal static (string Status, object? Detail, int ExitCode)
        MapCadenceSetReserveReadFailure(
            RecapGridCadenceReadResult result
    ) => result switch {
        RecapGridCadenceReadResult.Disposed => ("disposed", null, 2),
        RecapGridCadenceReadResult.Busy => ("busy", null, 2),
        RecapGridCadenceReadResult.UnsupportedSchema schema => (
            "unsupported-schema",
            new { version = schema.Version },
            2),
        RecapGridCadenceReadResult.Invalid invalid => (
            "invalid",
            new { code = invalid.Code },
            2),
        _ => (
            "invalid",
            new { code = "CadenceReadOutcomeInvalid" },
            2)
    };

    internal static (string Status, object? Detail, int ExitCode)
        MapCadenceSetReserveCompareExchange(
            RecapGridCadenceHeadRef expectedHead,
            long minimumRecentHistoryLoad,
            RecapGridCadenceCompareExchangeResult result
    ) => result switch {
        RecapGridCadenceCompareExchangeResult.Updated updated => (
            "updated",
            DescribeCadenceReserveReceipt(updated.Snapshot),
            0),
        RecapGridCadenceCompareExchangeResult.Unchanged unchanged => (
            "unchanged",
            DescribeCadenceReserveReceipt(unchanged.Snapshot),
            0),
        RecapGridCadenceCompareExchangeResult.Stale => (
            "stale",
            null,
            2),
        RecapGridCadenceCompareExchangeResult.Busy => (
            "busy",
            null,
            2),
        RecapGridCadenceCompareExchangeResult.CommitIndeterminate value => (
            "commit-indeterminate",
            new {
                expectedHead = DescribeCadenceHead(expectedHead),
                intendedHead = DescribeCadenceHead(value.Intended),
                minimumRecentHistoryLoad
            },
            2),
        RecapGridCadenceCompareExchangeResult.Disposed => (
            "disposed",
            null,
            2),
        RecapGridCadenceCompareExchangeResult.UnsupportedSchema schema => (
            "unsupported-schema",
            new { version = schema.Version },
            2),
        RecapGridCadenceCompareExchangeResult.Invalid invalid => (
            "invalid",
            new { code = invalid.Code },
            2),
        _ => (
            "invalid",
            new { code = "CadenceCompareExchangeOutcomeInvalid" },
            2)
    };

    private static object DescribeCadenceReserveReceipt(
        RecapGridCadenceSnapshot snapshot
    ) => new {
        head = DescribeCadenceHead(snapshot.Head),
        minimumRecentHistoryLoad =
            snapshot.Policy.MinimumRecentHistoryLoad
    };

    private static int PrintCadenceSetReserve(
        (string Status, object? Detail, int ExitCode) presentation
    ) => Print(
        "cadence.set-reserve",
        presentation.Status,
        presentation.Detail,
        presentation.ExitCode);
}
