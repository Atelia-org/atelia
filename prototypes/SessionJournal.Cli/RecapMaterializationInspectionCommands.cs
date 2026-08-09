using Atelia.SessionJournal.DerivedRecap.Store;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli;

internal static class RecapMaterializationInspectionCommands {
    private const string ReportSchema =
        "atelia.session-journal.derived-recap-materialization-inspection.v8";

    internal static async Task<int> RunAsync(CliOptions options) {
        ArgumentNullException.ThrowIfNull(options);
        options.EnsureOnly(
            "input",
            "branch",
            "nth-previous",
            "report-json"
        );
        string input = options.RequireSingle("input");
        string branch = options.RequireSingle("branch");
        int nth = options.GetInt("nth-previous", 0);
        string? reportPath = options.GetOptionalSingle("report-json");
        CliIo.EnsurePathChainHasNoReparsePoint(input, "--input");
        if (reportPath is not null) {
            CliIo.ValidateFileOutputPath(input, reportPath, "--report-json");
        }

        RecapMaterializationInspectionReport report;
        using (SJ.SessionJournalEngine engine =
               SJ.SessionJournalEngine.OpenReadOnly(input, branch)) {
            SJ.SessionCurrentLineageSnapshot lineage =
                engine.ReadCurrentLineageHeaders();
            try {
                var source = new DerivedRecapContextCandidateSource(
                    DerivedRecapEpochStore.Open(
                        input,
                        engine.BranchRefId,
                        BuiltInRecapPlannerConfig.Composition.StoreLimits
                    ),
                    engine.ReadView
                );
                SJ.SessionContextCandidateSelection selection =
                    await source.SelectAsync(
                            new SJ.SessionContextSelectionRequest(
                                lineage.CapturedHead,
                                nth
                            ),
                            CancellationToken.None
                        )
                        .ConfigureAwait(false);
                if (selection.Candidate is not { } descriptor) {
                    report = Failure(
                        engine,
                        lineage.CapturedHead,
                        nth,
                        selection.Status.ToString(),
                        selection.Detail
                    );
                }
                else {
                    SJ.SessionContextCandidate candidate =
                        await source.MaterializeAsync(
                                descriptor,
                                CancellationToken.None
                            )
                            .ConfigureAwait(false);
                    report = new RecapMaterializationInspectionReport(
                        ReportSchema,
                        engine.BranchName,
                        engine.BranchRefId.ToHexString(),
                        lineage.CapturedHead.ToString(),
                        nth,
                        "Selected",
                        null,
                        candidate.SetAdmissionAnchor.ToString(),
                        [
                            .. candidate.Contributions.Select(
                                static contribution =>
                                    new RecapContributionInspection(
                                        contribution.Target.Carrier.ToString(),
                                        contribution.Target.BlockKey,
                                        contribution.ExactText,
                                        contribution.ContentSha256
                                    )
                            )
                        ]
                    );
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or InvalidOperationException
            ) {
                report = Failure(
                    engine,
                    lineage.CapturedHead,
                    nth,
                    "StoreUnavailable",
                    exception.Message
                );
            }
        }
        if (reportPath is not null) {
            CliIo.WriteJsonAtomically(reportPath, report);
        }
        Console.WriteLine("operation: recap materialize-inspect");
        Console.WriteLine($"status: {report.Status}");
        Console.WriteLine($"capturedHead: {report.CapturedHead}");
        return report.Status == "Selected" ? 0 : 2;
    }

    private static RecapMaterializationInspectionReport Failure(
        SJ.SessionJournalEngine engine,
        Atelia.EventJournal.EventAddress capturedHead,
        int nth,
        string status,
        string? detail
    ) => new(
        ReportSchema,
        engine.BranchName,
        engine.BranchRefId.ToHexString(),
        capturedHead.ToString(),
        nth,
        status,
        detail,
        null,
        []
    );
}

internal sealed record RecapContributionInspection(
    string Carrier,
    string BlockKey,
    string ExactText,
    string ContentSha256
);

internal sealed record RecapMaterializationInspectionReport(
    string Schema,
    string Branch,
    string RefId,
    string CapturedHead,
    int NthPrevious,
    string Status,
    string? Detail,
    string? SetAdmissionAnchor,
    IReadOnlyList<RecapContributionInspection> Contributions
);
