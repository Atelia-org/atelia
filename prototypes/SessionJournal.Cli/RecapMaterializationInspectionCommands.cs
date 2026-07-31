using System.Text;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli;

internal static class RecapMaterializationInspectionCommands {
    private const string ReportSchema =
        "atelia.session-journal.derived-recap-materialization-inspection.v1";

    internal static async Task<int> RunAsync(CliOptions options) {
        ArgumentNullException.ThrowIfNull(options);
        options.EnsureOnly(
            "input",
            "branch",
            "nth-previous",
            "report-json"
        );
        string inputPath = options.RequireSingle("input");
        string branchName = options.RequireSingle("branch");
        int nthPrevious = ParseNthPrevious(options);
        string? reportPath =
            options.GetOptionalSingle("report-json");
        CliIo.EnsurePathChainHasNoReparsePoint(inputPath, "--input");
        if (reportPath is not null) {
            CliIo.ValidateFileOutputPath(
                inputPath,
                reportPath,
                "--report-json"
            );
        }

        RecapMaterializationInspectionReport report;
        int exitCode;
        using (SJ.SessionJournalEngine engine =
               SJ.SessionJournalEngine.OpenReadOnly(
                   inputPath,
                   branchName
               )) {
            SJ.SessionCurrentLineageSnapshot lineage =
                engine.ReadCurrentLineageHeaders();
            string branchRefId = engine.BranchRefId.ToHexString();
            if (!HasExistingStoreScaffolding(
                    inputPath,
                    branchRefId
                )) {
                report = FailureReport(
                    engine,
                    lineage.CapturedHead,
                    nthPrevious,
                    "StoreUnavailable",
                    "StoreUnavailable",
                    "Recap Store scaffolding is missing."
                );
                exitCode = 2;
            }
            else {
                (report, exitCode) = await InspectAsync(
                        engine,
                        lineage,
                        nthPrevious
                    )
                    .ConfigureAwait(false);
            }
        }

        if (reportPath is not null) {
            CliIo.WriteJsonAtomically(reportPath, report);
        }
        Console.WriteLine($"operation: recap materialize-inspect");
        Console.WriteLine($"capturedHead: {report.CapturedHead}");
        Console.WriteLine($"nthPrevious: {report.NthPrevious}");
        Console.WriteLine($"status: {report.Status}");
        if (report.SetAdmissionAnchor is not null) {
            Console.WriteLine(
                $"setAdmissionAnchor: {report.SetAdmissionAnchor}"
            );
        }
        return exitCode;
    }

    private static async ValueTask<(
        RecapMaterializationInspectionReport Report,
        int ExitCode
    )> InspectAsync(
        SJ.SessionJournalEngine engine,
        SJ.SessionCurrentLineageSnapshot lineage,
        int nthPrevious
    ) {
        try {
            DerivedRecapStore store = DerivedRecapStore.Open(
                engine.Path,
                engine.BranchRefId
            );
            var source = new DerivedRecapContextCandidateSource(
                store,
                engine
            );
            SJ.SessionContextCandidateSelection selection =
                await source.SelectAsync(
                        new SJ.SessionContextSelectionRequest(
                            lineage.CapturedHead,
                            nthPrevious
                        ),
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);
            if (selection.Status
                    != SJ.SessionContextCandidateSelectionStatus.Selected) {
                return (
                    FailureReport(
                        engine,
                        lineage.CapturedHead,
                        nthPrevious,
                        selection.Status.ToString(),
                        selection.Status.ToString(),
                        selection.Detail
                    ),
                    2
                );
            }

            SJ.SessionContextCandidateDescriptor descriptor =
                selection.Candidate
                ?? throw new InvalidDataException(
                    "Selected recap candidate has no descriptor."
                );
            SJ.SessionHistoryPlanningSeed seed =
                engine.CreateHistoryPlanningSeed(
                    descriptor.SetAdmissionAnchor,
                    descriptor.AnchorSetups
                );
            SJ.SessionHistoryPlanningWindow window =
                engine.ReadHistoryPlanningWindowAt(
                    lineage.CapturedHead,
                    seed
                );
            IReadOnlyDictionary<
                SJ.ContextHeaderBlockPath,
                string
            > blockIds = await ReadExactBlockIdsAsync(
                    store,
                    descriptor
                )
                .ConfigureAwait(false);
            SJ.SessionContextCandidate candidate =
                await source.MaterializeAsync(
                        descriptor,
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);
            ValidateRawFacingFacts(descriptor, candidate, window);

            return (
                new RecapMaterializationInspectionReport(
                    ReportSchema,
                    engine.BranchName,
                    engine.BranchRefId.ToHexString(),
                    SJ.EventAddressTextCodec.Format(
                        lineage.CapturedHead
                    ),
                    nthPrevious,
                    "Selected",
                    SJ.EventAddressTextCodec.Format(
                        descriptor.SetAdmissionAnchor
                    ),
                    CreateRecentRange(window),
                    Array.AsReadOnly([
                        .. candidate.Contributions.Select(
                            contribution =>
                                new RecapMaterializedContributionReport(
                                    blockIds.TryGetValue(
                                        contribution.Target,
                                        out string? blockId
                                    )
                                        ? blockId
                                        : throw new InvalidDataException(
                                            "Materialized contribution "
                                            + "target is absent from the "
                                            + "exact frozen plan."
                                        ),
                                    SJ.ContextHeaderCarrierTokens
                                        .ToStorageToken(
                                            contribution.Target.Carrier
                                        ),
                                    contribution.Target.BlockKey,
                                    new UTF8Encoding(
                                        encoderShouldEmitUTF8Identifier:
                                            false,
                                        throwOnInvalidBytes: true
                                    ).GetByteCount(
                                        contribution.ExactText
                                    ),
                                    contribution.ContentCodecId,
                                    contribution.ContentSha256,
                                    SJ.EventAddressTextCodec.Format(
                                        contribution.AbsorbedThrough
                                    )
                                )
                        )
                    ]),
                    []
                ),
                0
            );
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or ArgumentException
                  or NotSupportedException
                  or IOException
                  or UnauthorizedAccessException) {
            return (
                FailureReport(
                    engine,
                    lineage.CapturedHead,
                    nthPrevious,
                    exception is InvalidDataException
                        ? "Invalid"
                        : "Unavailable",
                    exception is InvalidDataException
                        ? "MaterializationInvalid"
                        : "MaterializationUnavailable",
                    exception.Message
                ),
                2
            );
        }
    }

    private static async ValueTask<IReadOnlyDictionary<
        SJ.ContextHeaderBlockPath,
        string
    >> ReadExactBlockIdsAsync(
        DerivedRecapStore store,
        SJ.SessionContextCandidateDescriptor descriptor
    ) {
        PublishedPlanAtAnchorReadResult read =
            await store.ReadPublishedPlanAtAnchorAsync(
                    descriptor.SetAdmissionAnchor,
                    CancellationToken.None
                )
                .ConfigureAwait(false);
        if (read is not PublishedPlanAtAnchorReadResult.Available
                available
            || !string.Equals(
                available.Snapshot.Descriptor.EnvelopeSha256,
                descriptor.SnapshotToken,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Exact Published plan changed or became unavailable "
                + "during materialization inspection."
            );
        }
        var result = new Dictionary<
            SJ.ContextHeaderBlockPath,
            string
        >();
        foreach (RecapBlockPlan block
                 in available.Snapshot.FrozenPlan.Blocks) {
            if (!result.TryAdd(
                    block.Target,
                    block.RecapBlockId.Value
                )) {
                throw new InvalidDataException(
                    "Exact Published plan contains duplicate targets."
                );
            }
        }
        return result;
    }

    private static void ValidateRawFacingFacts(
        SJ.SessionContextCandidateDescriptor descriptor,
        SJ.SessionContextCandidate candidate,
        SJ.SessionHistoryPlanningWindow window
    ) {
        if (candidate.SetAdmissionAnchor
                != descriptor.SetAdmissionAnchor
            || candidate.AnchorSetups != descriptor.AnchorSetups
            || window.StartExclusive
                != descriptor.SetAdmissionAnchor) {
            throw new InvalidDataException(
                "Materialized recap candidate does not match its exact "
                + "descriptor or recent-history boundary."
            );
        }
        _ = SJ.SessionContextContributionContract
            .ValidateAndNormalize(candidate.Contributions);
        var allowedSourceHeads = new HashSet<EventAddress> {
            descriptor.SetAdmissionAnchor
        };
        allowedSourceHeads.UnionWith(window.RawAddresses);
        foreach (SJ.SessionContextContribution contribution
                 in candidate.Contributions) {
            if (!allowedSourceHeads.Contains(
                    contribution.AbsorbedThrough
                )) {
                throw new InvalidDataException(
                    "A materialized contribution absorbedThrough is "
                    + "outside its authoritative raw interval."
                );
            }
        }
    }

    private static RecapRecentHistoryRangeReport CreateRecentRange(
        SJ.SessionHistoryPlanningWindow window
    ) => new(
        SJ.EventAddressTextCodec.Format(window.StartExclusive),
        SJ.EventAddressTextCodec.Format(window.ObservedRawHead),
        window.RawAddresses.Count == 0
            ? null
            : SJ.EventAddressTextCodec.Format(
                window.RawAddresses[0]
            ),
        window.RawAddresses.Count == 0
            ? null
            : SJ.EventAddressTextCodec.Format(
                window.RawAddresses[^1]
            ),
        window.RawAddresses.Count,
        window.Units.Count == 0
            ? null
            : SJ.EventAddressTextCodec.Format(
                window.Units[0].SourceStartInclusive
            ),
        window.Units.Count == 0
            ? null
            : SJ.EventAddressTextCodec.Format(
                window.Units[^1].SourceEndInclusive
            ),
        window.Units.Count
    );

    private static RecapMaterializationInspectionReport FailureReport(
        SJ.SessionJournalEngine engine,
        EventAddress capturedHead,
        int nthPrevious,
        string status,
        string code,
        string? detail
    ) => new(
        ReportSchema,
        engine.BranchName,
        engine.BranchRefId.ToHexString(),
        SJ.EventAddressTextCodec.Format(capturedHead),
        nthPrevious,
        status,
        SetAdmissionAnchor: null,
        RecentHistory: null,
        Contributions: [],
        Defects: [
            new RecapMaterializationInspectionDefect(
                code,
                detail ?? code
            )
        ]
    );

    private static bool HasExistingStoreScaffolding(
        string inputPath,
        string branchRefId
    ) {
        string v4 = Path.Combine(
            Path.GetFullPath(inputPath),
            "derived",
            "recap",
            "v4"
        );
        string root = Path.Combine(v4, "refs", branchRefId);
        return Directory.Exists(Path.Combine(v4, "locks"))
            && Directory.Exists(Path.Combine(v4, "refs"))
            && File.Exists(
                Path.Combine(v4, "locks", $"{branchRefId}.lock")
            )
            && Directory.Exists(root)
            && Directory.Exists(Path.Combine(root, "building"))
            && Directory.Exists(Path.Combine(root, "published"))
            && File.Exists(Path.Combine(root, "store.json"));
    }

    private static int ParseNthPrevious(CliOptions options) {
        string? value = options.GetOptionalSingle("nth-previous");
        if (value is null) {
            return 0;
        }
        return int.TryParse(value, out int parsed) && parsed >= 0
            ? parsed
            : throw new ArgumentException(
                "--nth-previous must be a non-negative integer."
            );
    }
}

internal sealed record RecapMaterializationInspectionReport(
    string Schema,
    string BranchName,
    string BranchRefId,
    string CapturedHead,
    int NthPrevious,
    string Status,
    string? SetAdmissionAnchor,
    RecapRecentHistoryRangeReport? RecentHistory,
    IReadOnlyList<RecapMaterializedContributionReport> Contributions,
    IReadOnlyList<RecapMaterializationInspectionDefect> Defects
);

internal sealed record RecapRecentHistoryRangeReport(
    string StartExclusive,
    string EndInclusive,
    string? RawStartInclusive,
    string? RawEndInclusive,
    int RawEventCount,
    string? HistoryUnitStartInclusive,
    string? HistoryUnitEndInclusive,
    int HistoryUnitCount
);

internal sealed record RecapMaterializedContributionReport(
    string RecapBlockId,
    string TargetCarrier,
    string TargetBlockKey,
    int Utf8Bytes,
    string ContentCodecId,
    string ContentSha256,
    string AbsorbedThrough
);

internal sealed record RecapMaterializationInspectionDefect(
    string Code,
    string Detail
);
