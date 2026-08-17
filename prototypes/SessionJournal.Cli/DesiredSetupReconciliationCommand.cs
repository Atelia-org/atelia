using System.Security.Cryptography;
using System.Text;
using Atelia.Completion;
using Atelia.EventJournal;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli;

internal sealed record DesiredSetupReconciliationReport(
    string Schema,
    string BranchName,
    string ConnectionId,
    string BeforeHead,
    string AfterHead,
    bool RuntimeConfigChanged,
    bool SystemPromptChanged,
    string ModelId,
    string CompletionSurfaceId,
    string SystemPromptUtf8Sha256
);

internal static class DesiredSetupReconciliationCommand {
    private const string ReportSchema =
        "atelia.session-journal.desired-setup-reconciliation.v2";

    internal static int Run(CliOptions options) {
        ArgumentNullException.ThrowIfNull(options);
        options.EnsureOnly(
            "input",
            "branch",
            "expected-head",
            "connections",
            "connection",
            "system-prompt-file",
            "report-json"
        );

        string inputPath = options.RequireSingle("input");
        string branchName = options.RequireSingle("branch");
        string expectedHeadText = options.RequireSingle("expected-head");
        string connectionsPath = options.RequireSingle("connections");
        string connectionId = options.RequireSingle("connection");
        string systemPromptPath =
            options.RequireSingle("system-prompt-file");
        string? reportPath = options.GetOptionalSingle("report-json");
        EventAddress expectedHead = ParseExpectedHead(expectedHeadText);

        ValidatePaths(
            inputPath,
            connectionsPath,
            systemPromptPath,
            reportPath
        );

        // Match GalateaConfigLoader's systemPromptFile semantics exactly.
        string desiredSystemPrompt = File.ReadAllText(systemPromptPath).Trim();
        CompletionConnectionsFileConfig connections =
            CompletionConnectionConfigLoader.LoadFile(connectionsPath);
        CompletionConnectionConfig connection = connections.Connections
            .SingleOrDefault(candidate => string.Equals(
                candidate.Id,
                connectionId,
                StringComparison.Ordinal
            )) ?? throw new ArgumentException(
                $"Unknown completion connection '{connectionId}'."
            );

        using SJ.SessionJournalEngine engine =
            SJ.SessionJournalEngine.Open(inputPath, branchName);
        EventAddress? observedHead = engine.ReadCurrentHead();
        if (observedHead != expectedHead) {
            throw new InvalidOperationException(
                "SessionJournal head does not match --expected-head. "
                + $"Expected '{expectedHeadText}', observed "
                + $"'{SJ.EventAddressTextCodec.FormatNullable(observedHead)}'."
            );
        }

        SJ.SessionExecutionBoundaryInspection before =
            engine.InspectExecutionBoundary();
        if (before.Head != expectedHead
            || before.Phase != SJ.SessionExecutionPhase.Idle) {
            throw new InvalidOperationException(
                "reconcile-desired-setup requires the exact expected head "
                + $"to be Idle; observed phase '{before.Phase}'."
            );
        }

        SJ.SessionDesiredSetupReconciliationResult reconciliation =
            engine.ReconcileDesiredSetup(
                expectedHead,
                new SJ.SessionDesiredSetup(
                    connection.ModelId,
                    connection.CompletionSurfaceId,
                    desiredSystemPrompt
                )
            );
        SJ.SessionDesiredSetupReconciliationResult.Ready ready =
            reconciliation switch {
                SJ.SessionDesiredSetupReconciliationResult.Ready result =>
                    result,
                SJ.SessionDesiredSetupReconciliationResult.Unavailable result =>
                    throw new InvalidOperationException(
                        "Desired setup reconciliation is unavailable "
                        + $"({result.Reason}) at phase '{result.Phase}'."
                    ),
                SJ.SessionDesiredSetupReconciliationResult.Retryable result =>
                    throw new InvalidOperationException(
                        "SessionJournal head changed during desired setup "
                        + $"reconciliation. Expected "
                        + $"'{SJ.EventAddressTextCodec.FormatNullable(result.ExpectedHead)}', "
                        + $"observed "
                        + $"'{SJ.EventAddressTextCodec.FormatNullable(result.ObservedHead)}'."
                    ),
                _ => throw new InvalidDataException(
                    "Unknown desired setup reconciliation result."
                )
            };

        DesiredSetupReconciliationReport report = VerifyAndCreateReport(
            engine,
            branchName,
            connection,
            desiredSystemPrompt,
            expectedHead,
            ready
        );
        if (reportPath is not null) {
            CliIo.WriteJsonAtomically(reportPath, report);
        }

        Console.WriteLine($"beforeHead: {report.BeforeHead}");
        Console.WriteLine($"afterHead: {report.AfterHead}");
        Console.WriteLine(
            $"runtimeConfigChanged: {report.RuntimeConfigChanged}"
        );
        Console.WriteLine(
            $"systemPromptChanged: {report.SystemPromptChanged}"
        );
        Console.WriteLine($"modelId: {report.ModelId}");
        Console.WriteLine(
            $"completionSurfaceId: {report.CompletionSurfaceId}"
        );
        if (reportPath is not null) {
            Console.WriteLine(
                $"jsonReport: {Path.GetFullPath(reportPath)}"
            );
        }
        return 0;
    }

    private static DesiredSetupReconciliationReport VerifyAndCreateReport(
        SJ.SessionJournalEngine engine,
        string branchName,
        CompletionConnectionConfig connection,
        string desiredSystemPrompt,
        EventAddress beforeHead,
        SJ.SessionDesiredSetupReconciliationResult.Ready ready
    ) {
        EventAddress? afterHead = engine.ReadCurrentHead();
        if (afterHead is not { } exactAfter
            || ready.GoverningSetup.Head != exactAfter) {
            throw new InvalidDataException(
                "Desired setup reconciliation did not retain exact final-head authority."
            );
        }
        SJ.SessionExecutionBoundaryInspection after =
            engine.InspectExecutionBoundary();
        if (after.Head != exactAfter
            || after.Phase != SJ.SessionExecutionPhase.Idle) {
            throw new InvalidDataException(
                "Desired setup reconciliation did not finish at an Idle exact head."
            );
        }

        SJ.SessionGoverningSetup governing =
            engine.ResolveGoverningSetup(exactAfter);
        if (governing.Head != exactAfter
            || !string.Equals(
                governing.RuntimeConfig.ModelId,
                connection.ModelId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                governing.RuntimeConfig.CompletionSurfaceId,
                connection.CompletionSurfaceId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                governing.SystemPrompt,
                desiredSystemPrompt,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Final governing setup does not match the requested connection and system prompt."
            );
        }

        return new DesiredSetupReconciliationReport(
            ReportSchema,
            branchName,
            connection.Id,
            SJ.EventAddressTextCodec.Format(beforeHead),
            SJ.EventAddressTextCodec.Format(exactAfter),
            ready.RuntimeConfigChanged,
            ready.SystemPromptChanged,
            governing.RuntimeConfig.ModelId,
            governing.RuntimeConfig.CompletionSurfaceId,
            ComputeUtf8Sha256(governing.SystemPrompt)
        );
    }

    private static EventAddress ParseExpectedHead(string value) =>
        SJ.EventAddressTextCodec.TryParse(value, out EventAddress address)
            ? address
            : throw new ArgumentException(
                $"--expected-head is not a valid EventAddress: '{value}'."
            );

    private static void ValidatePaths(
        string inputPath,
        string connectionsPath,
        string systemPromptPath,
        string? reportPath
    ) {
        var writablePaths = new List<(string Path, string Option)> {
            (inputPath, "--input")
        };
        if (reportPath is not null) {
            CliIo.ValidateFileOutputPath(
                inputPath,
                reportPath,
                "--report-json"
            );
            writablePaths.Add((reportPath, "--report-json"));
        }
        CliIo.ValidateReadOnlyWritablePaths(
            [
                (connectionsPath, "--connections"),
                (systemPromptPath, "--system-prompt-file")
            ],
            writablePaths
        );
    }

    private static string ComputeUtf8Sha256(string value) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value))
        );
}
