using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.HistoryTimeline;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli;

internal static class RecapHistoryLoadCommands {
    private const string ReportSchema =
        "atelia.session-journal.recap-history-load-calibration.v2";

    internal static Task<int> RunAsync(string[] args) {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0
            || args[0] is "-h" or "--help"
            || args[0].StartsWith("--", StringComparison.Ordinal)) {
            throw new ArgumentException(
                "recap-grid timeline history-load requires the inspect subcommand."
            );
        }
        if (!string.Equals(
                args[0],
                "inspect",
                StringComparison.Ordinal
            )) {
            throw new ArgumentException(
                $"Unknown recap-grid timeline history-load subcommand '{args[0]}'."
            );
        }

        CliOptions options = CliOptions.Parse(args.Skip(1).ToArray());
        options.EnsureOnly("input", "branch", "report-json");
        string inputPath = options.RequireSingle("input");
        string branchName = options.GetOptionalSingle("branch")
            ?? SJ.SessionJournalDefaults.MainBranchName;
        string? reportPath =
            options.GetOptionalSingle("report-json");

        CliIo.EnsurePathChainHasNoReparsePoint(
            inputPath,
            "--input"
        );
        if (reportPath is not null) {
            CliIo.ValidateFileOutputPath(
                inputPath,
                reportPath,
                "--report-json"
            );
        }

        RecapHistoryLoadCalibrationReport report;
        using (SJ.SessionJournalEngine engine =
               SJ.SessionJournalEngine.OpenReadOnly(
                   inputPath,
                   branchName
               )) {
            SJ.SessionHistoryPlanningWindow window =
                engine.ReadHistoryPlanningWindow();
            var estimator = new RecordingHistoryUnitLoadEstimator(
                new O200kBaseHistoryUnitLoadEstimator()
            );
            HistoryLoadProjection measurement;
            try {
                measurement = HistoryLoadProjector.Measure(
                    window,
                    window.StartExclusive,
                    estimator
                );
            }
            catch (HistoryLoadMeasurementException exception) {
                throw new InvalidDataException(
                    "History-load calibration failed "
                    + $"({exception.Code}): {exception.Message}",
                    exception
                );
            }

            IReadOnlyList<RecapHistoryLoadUnitReport> units =
                MapUnits(window, estimator);
            report = new RecapHistoryLoadCalibrationReport(
                ReportSchema,
                measurement.EstimatorId,
                engine.BranchName,
                engine.BranchRefId.ToHexString(),
                SJ.EventAddressTextCodec.Format(
                    window.ObservedRawHead
                ),
                SJ.EventAddressTextCodec.Format(
                    measurement.BaselineAddress
                ),
                new RecapHistoryLoadTotalsReport(
                    window.RawAddresses.Count,
                    units.Count,
                    measurement.ReplaySafeBoundaries.Count,
                    measurement.Growth.Value,
                    measurement.RenderedUtf8Bytes
                ),
                MapByKind(units),
                new RecapHistoryLoadUnitDistributionsReport(
                    NearestRankDistribution.Create(
                        units.Select(static unit => unit.Load)
                    ),
                    NearestRankDistribution.Create(
                        units.Select(
                            static unit =>
                                (long)unit.RenderedUtf8Bytes
                        )
                    )
                ),
                units,
                MapBoundaries(measurement)
            );
        }

        if (reportPath is not null) {
            CliIo.WriteJsonAtomically(reportPath, report);
        }
        PrintReport(report, reportPath);
        return Task.FromResult(0);
    }

    private static IReadOnlyList<RecapHistoryLoadUnitReport> MapUnits(
        SJ.SessionHistoryPlanningWindow window,
        RecordingHistoryUnitLoadEstimator estimator
    ) {
        var result = new List<RecapHistoryLoadUnitReport>(
            window.Units.Count
        );
        for (int index = 0; index < window.Units.Count; index++) {
            SJ.SessionHistoryPlanningUnit unit = window.Units[index];
            HistoryUnitLoadMeasurement measured =
                estimator.GetRecordedMeasurement(unit);
            result.Add(new RecapHistoryLoadUnitReport(
                index,
                unit.Message.Kind.ToString(),
                SJ.EventAddressTextCodec.Format(
                    unit.SourceStartInclusive
                ),
                SJ.EventAddressTextCodec.Format(
                    unit.SourceEndInclusive
                ),
                measured.Load.Value,
                measured.RenderedUtf8Bytes
            ));
        }
        return result.AsReadOnly();
    }

    private static IReadOnlyList<RecapHistoryLoadByKindReport>
        MapByKind(
        IReadOnlyList<RecapHistoryLoadUnitReport> units
    ) => Array.AsReadOnly([
        .. units
            .GroupBy(static unit => unit.Kind, StringComparer.Ordinal)
            .OrderBy(
                static group => ParseHistoryKind(group.Key)
            )
            .Select(static group =>
                new RecapHistoryLoadByKindReport(
                    group.Key,
                    group.Count(),
                    group.Sum(static unit => unit.Load),
                    group.Sum(
                        static unit =>
                            (long)unit.RenderedUtf8Bytes
                    )
                )
            )
    ]);

    private static HistoryMessageKind ParseHistoryKind(string value)
        => Enum.Parse<HistoryMessageKind>(value);

    private static IReadOnlyList<HistoryLoadBoundaryProjectionReport>
        MapBoundaries(
        HistoryLoadProjection measurement
    ) => Array.AsReadOnly([
        .. measurement.ReplaySafeBoundaries.Select(
            static boundary =>
                new HistoryLoadBoundaryProjectionReport(
                    SJ.EventAddressTextCodec.Format(
                        boundary.Address
                    ),
                    boundary.HistoryUnitCountSinceBaseline,
                    boundary.AbsorbedSinceBaseline.Value
                )
        )
    ]);

    private static void PrintReport(
        RecapHistoryLoadCalibrationReport report,
        string? reportPath
    ) {
        Console.WriteLine($"schema: {report.Schema}");
        Console.WriteLine($"estimatorId: {report.EstimatorId}");
        Console.WriteLine($"branchName: {report.BranchName}");
        Console.WriteLine($"branchRefId: {report.BranchRefId}");
        Console.WriteLine($"capturedHead: {report.CapturedHead}");
        Console.WriteLine($"baseline: {report.Baseline}");
        Console.WriteLine($"rawEvents: {report.Totals.RawEvents}");
        Console.WriteLine($"historyUnits: {report.Totals.HistoryUnits}");
        Console.WriteLine(
            $"replaySafeBoundaries: "
            + report.Totals.ReplaySafeBoundaries
        );
        Console.WriteLine($"historyLoad: {report.Totals.HistoryLoad}");
        Console.WriteLine(
            $"renderedUtf8Bytes: {report.Totals.RenderedUtf8Bytes}"
        );
        if (reportPath is not null) {
            Console.WriteLine(
                $"report: {Path.GetFullPath(reportPath)}"
            );
        }
    }

    private sealed class RecordingHistoryUnitLoadEstimator(
        IHistoryUnitLoadEstimator inner
    ) : IHistoryUnitLoadEstimator {
        private readonly Dictionary<
            SJ.SessionHistoryPlanningUnit,
            HistoryUnitLoadMeasurement
        > _measurements = new(ReferenceEqualityComparer.Instance);

        public string Id => inner.Id;

        public HistoryUnitLoadMeasurement Measure(
            SJ.SessionHistoryPlanningUnit unit,
            int maxRenderedUtf8Bytes
        ) {
            if (_measurements.TryGetValue(
                    unit,
                    out HistoryUnitLoadMeasurement? measured
                )) {
                return measured;
            }
            measured = inner.Measure(unit, maxRenderedUtf8Bytes);
            _measurements.Add(unit, measured);
            return measured;
        }

        internal HistoryUnitLoadMeasurement GetRecordedMeasurement(
            SJ.SessionHistoryPlanningUnit unit
        ) => _measurements.TryGetValue(
                unit,
                out HistoryUnitLoadMeasurement? measured
            )
            ? measured
            : throw new InvalidDataException(
                "History-load projector did not measure an expected "
                + "HistoryUnit."
            );
    }

    private static class NearestRankDistribution {
        internal static RecapNearestRankDistributionReport Create(
            IEnumerable<long> source
        ) {
            long[] ordered = source
                .OrderBy(static value => value)
                .ToArray();
            if (ordered.Length == 0) {
                return new RecapNearestRankDistributionReport(
                    "nearest-rank",
                    0,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null
                );
            }
            return new RecapNearestRankDistributionReport(
                "nearest-rank",
                ordered.Length,
                ordered[0],
                Select(ordered, 50),
                Select(ordered, 75),
                Select(ordered, 90),
                Select(ordered, 95),
                Select(ordered, 99),
                ordered[^1]
            );
        }

        private static long Select(long[] ordered, int percentile) {
            int rank = checked(
                (int)(
                    ((long)percentile * ordered.Length + 99) / 100
                )
            );
            return ordered[rank - 1];
        }
    }
}

internal sealed record RecapHistoryLoadCalibrationReport(
    string Schema,
    string EstimatorId,
    string BranchName,
    string BranchRefId,
    string CapturedHead,
    string Baseline,
    RecapHistoryLoadTotalsReport Totals,
    IReadOnlyList<RecapHistoryLoadByKindReport> ByKind,
    RecapHistoryLoadUnitDistributionsReport UnitDistributions,
    IReadOnlyList<RecapHistoryLoadUnitReport> Units,
    IReadOnlyList<HistoryLoadBoundaryProjectionReport> Boundaries
);

internal sealed record RecapHistoryLoadTotalsReport(
    int RawEvents,
    int HistoryUnits,
    int ReplaySafeBoundaries,
    long HistoryLoad,
    int RenderedUtf8Bytes
);

internal sealed record RecapHistoryLoadByKindReport(
    string Kind,
    int HistoryUnits,
    long HistoryLoad,
    long RenderedUtf8Bytes
);

internal sealed record RecapHistoryLoadUnitDistributionsReport(
    RecapNearestRankDistributionReport HistoryLoad,
    RecapNearestRankDistributionReport RenderedUtf8Bytes
);

internal sealed record RecapNearestRankDistributionReport(
    string Method,
    int Count,
    long? Min,
    long? P50,
    long? P75,
    long? P90,
    long? P95,
    long? P99,
    long? Max
);

internal sealed record RecapHistoryLoadUnitReport(
    int Ordinal,
    string Kind,
    string SourceStartInclusive,
    string SourceEndInclusive,
    long Load,
    int RenderedUtf8Bytes
);

internal sealed record HistoryLoadBoundaryProjectionReport(
    string Address,
    int CompletedHistoryUnitCountSinceBaseline,
    long AbsorbedHistoryLoadSinceBaseline
);
