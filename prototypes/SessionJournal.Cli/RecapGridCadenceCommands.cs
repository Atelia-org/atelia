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
            return Print(
                "cadence.set-reserve",
                CadenceOpenStatus(opened),
                DescribeCadenceOpen(opened),
                2);
        }
        using RecapGridCadenceHandle handle = available.Handle;
        RecapGridCadenceReadResult read = handle.Reader.ReadSnapshot();
        if (read is not RecapGridCadenceReadResult.Available current) {
            return Print(
                "cadence.set-reserve",
                CadenceReadStatus(read),
                DescribeCadenceRead(read),
                2);
        }
        if (current.Snapshot.Head != expected) {
            return Print(
                "cadence.set-reserve",
                "stale",
                new {
                    expected = DescribeCadenceHead(expected),
                    actual = DescribeCadenceHead(current.Snapshot.Head)
                },
                2);
        }
        RecapGridCadencePolicySpec prior = current.Snapshot.Policy;
        var desired = new RecapGridCadencePolicySpec(
            minimumRecentHistoryLoad,
            prior.PartitionAlgorithmId,
            prior.HistoryLoadEstimatorId,
            prior.TargetHistoryLoad,
            prior.MaxRawEvents,
            prior.MaxRenderedBytes);
        RecapGridCadenceCompareExchangeResult result =
            handle.Coordinator.CompareExchangePolicy(expected, desired);
        return result switch {
            RecapGridCadenceCompareExchangeResult.Updated updated => Print(
                "cadence.set-reserve",
                "updated",
                DescribeCadenceSnapshot(updated.Snapshot)),
            RecapGridCadenceCompareExchangeResult.Unchanged unchanged => Print(
                "cadence.set-reserve",
                "unchanged",
                DescribeCadenceSnapshot(unchanged.Snapshot)),
            RecapGridCadenceCompareExchangeResult.Stale stale => Print(
                "cadence.set-reserve",
                "stale",
                new {
                    expected = DescribeCadenceHead(expected),
                    actual = DescribeCadenceHead(stale.Actual)
                },
                2),
            RecapGridCadenceCompareExchangeResult.Busy => Print(
                "cadence.set-reserve", "busy", exitCode: 2),
            RecapGridCadenceCompareExchangeResult.CommitIndeterminate value
                => Print(
                    "cadence.set-reserve",
                    "commit-indeterminate",
                    new {
                        intended = DescribeCadenceHead(value.Intended),
                        observed = value.Observed is null
                            ? null
                            : DescribeCadenceHead(value.Observed),
                        nextAction = "inspect"
                    },
                    2),
            RecapGridCadenceCompareExchangeResult.Disposed => Print(
                "cadence.set-reserve", "disposed", exitCode: 2),
            RecapGridCadenceCompareExchangeResult.UnsupportedSchema schema
                => Print(
                    "cadence.set-reserve",
                    "unsupported-schema",
                    new { version = schema.Version },
                    2),
            RecapGridCadenceCompareExchangeResult.Invalid invalid => Print(
                "cadence.set-reserve",
                "invalid",
                new { code = invalid.Code, detail = invalid.Detail },
                2),
            _ => Print(
                "cadence.set-reserve",
                "invalid",
                new { code = "CadenceCompareExchangeOutcomeInvalid" },
                2)
        };
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

    private static string CadenceOpenStatus(
        RecapGridCadenceOpenResult result
    ) => result switch {
        RecapGridCadenceOpenResult.Absent => "absent",
        RecapGridCadenceOpenResult.Busy => "busy",
        RecapGridCadenceOpenResult.UnsupportedSchema => "unsupported-schema",
        RecapGridCadenceOpenResult.PlatformUnsupported
            => "platform-unsupported",
        RecapGridCadenceOpenResult.Invalid => "invalid",
        _ => "invalid"
    };

    private static object DescribeCadenceOpen(
        RecapGridCadenceOpenResult result
    ) => result switch {
        RecapGridCadenceOpenResult.UnsupportedSchema schema
            => new { version = schema.Version },
        RecapGridCadenceOpenResult.Invalid invalid
            => new { code = invalid.Code, detail = invalid.Detail },
        _ => new { }
    };

    private static string CadenceReadStatus(
        RecapGridCadenceReadResult result
    ) => result switch {
        RecapGridCadenceReadResult.Disposed => "disposed",
        RecapGridCadenceReadResult.Busy => "busy",
        RecapGridCadenceReadResult.UnsupportedSchema => "unsupported-schema",
        RecapGridCadenceReadResult.Invalid => "invalid",
        _ => "invalid"
    };

    private static object DescribeCadenceRead(
        RecapGridCadenceReadResult result
    ) => result switch {
        RecapGridCadenceReadResult.UnsupportedSchema schema
            => new { version = schema.Version },
        RecapGridCadenceReadResult.Invalid invalid
            => new { code = invalid.Code, detail = invalid.Detail },
        _ => new { }
    };
}
