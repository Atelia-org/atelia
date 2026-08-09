using Atelia.SessionJournal.DerivedRecap.Planner;

namespace Atelia.SessionJournal.Cli;

internal static class RecapPlannerConfigCommands {
    private const string ReportSchema =
        "atelia.session-journal.recap-epoch-config-operation.v3";

    internal static Task<int> RunAsync(string[] args) {
        if (args.Length == 0
            || args[0] is "-h" or "--help") {
            throw new ArgumentException(
                "recap planner-config requires init or inspect."
            );
        }
        CliOptions options = CliOptions.Parse(args[1..]);
        return args[0] switch {
            "init" => InitAsync(options),
            "inspect" => InspectAsync(options),
            _ => throw new ArgumentException(
                $"Unknown recap planner-config subcommand '{args[0]}'."
            )
        };
    }

    private static Task<int> InitAsync(CliOptions options) {
        (string input, string? report) = Parse(options);
        string path = RecapEpochConfigLoader.GetCanonicalPath(input);
        if (File.Exists(path)) {
            throw new InvalidOperationException(
                $"Recap epoch config already exists at '{path}'."
            );
        }
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        CliIo.EnsurePathChainHasNoReparsePoint(directory, "config");
        string staging = path + $".{Guid.NewGuid():N}.tmp";
        try {
            byte[] bytes = RecapEpochConfigCodec.Encode(
                BuiltInRecapPlannerConfig.Document
            );
            using (var stream = new FileStream(
                staging,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None
            )) {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(staging, path, overwrite: false);
        }
        finally {
            if (File.Exists(staging)) {
                File.Delete(staging);
            }
        }
        return Task.FromResult(Finish(
            new RecapPlannerConfigReport(
                ReportSchema,
                "init",
                path,
                RecapEpochConfigCodec.SchemaV3,
                "Initialized",
                null
            ),
            report
        ));
    }

    private static Task<int> InspectAsync(CliOptions options) {
        (string input, string? report) = Parse(options);
        string path = RecapEpochConfigLoader.GetCanonicalPath(input);
        RecapPlannerConfigReport result;
        int exitCode;
        try {
            if (!RecapEpochConfigLoader.TryLoad(
                    input,
                    out RecapEpochConfigDocument document
                )) {
                result = new RecapPlannerConfigReport(
                    ReportSchema,
                    "inspect",
                    path,
                    null,
                    "Missing",
                    null
                );
                exitCode = 2;
            }
            else {
                _ = BuiltInRecapPlannerConfig.Resolve(document);
                result = new RecapPlannerConfigReport(
                    ReportSchema,
                    "inspect",
                    path,
                    document.Schema,
                    "Valid",
                    null
                );
                exitCode = 0;
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or ArgumentException
        ) {
            result = new RecapPlannerConfigReport(
                ReportSchema,
                "inspect",
                path,
                null,
                "Invalid",
                exception.Message
            );
            exitCode = 2;
        }
        Finish(result, report);
        return Task.FromResult(exitCode);
    }

    private static (string Input, string? Report) Parse(
        CliOptions options
    ) {
        options.EnsureOnly("input", "report-json");
        string input = options.RequireSingle("input");
        string? report = options.GetOptionalSingle("report-json");
        CliIo.EnsurePathChainHasNoReparsePoint(input, "--input");
        if (report is not null) {
            CliIo.ValidateFileOutputPath(input, report, "--report-json");
        }
        return (input, report);
    }

    private static int Finish(
        RecapPlannerConfigReport report,
        string? reportPath
    ) {
        if (reportPath is not null) {
            CliIo.WriteJsonAtomically(reportPath, report);
        }
        Console.WriteLine($"operation: recap planner-config {report.Operation}");
        Console.WriteLine($"status: {report.Status}");
        Console.WriteLine($"path: {report.Path}");
        return report.Status is "Initialized" or "Valid" ? 0 : 2;
    }
}

internal sealed record RecapPlannerConfigReport(
    string Schema,
    string Operation,
    string Path,
    string? ConfigSchema,
    string Status,
    string? Detail
);
