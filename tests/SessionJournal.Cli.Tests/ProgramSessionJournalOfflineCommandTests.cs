using System.Security.Cryptography;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedMemory;
using Atelia.SessionJournal.Cli;
using SJ = Atelia.SessionJournal;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class ProgramSessionJournalOfflineCommandTests : IDisposable {
    private static readonly JsonSerializerOptions WebJsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "atelia-session-offline-cli-tests",
        Guid.NewGuid().ToString("N")
    );

    public void Dispose() {
        try {
            if (Directory.Exists(_tempRoot)) {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch {
            // Best-effort cleanup for temp test directories.
        }
    }

    [Fact]
    public void ValidateSessionJournal_IsReadOnlyAndReportsRawState() {
        string repoPath = CreateJournal();
        string reportPath = Path.Combine(_tempRoot, "validation.json");
        IReadOnlyDictionary<string, string> before =
            CaptureRepositoryFileHashes(repoPath);

        int exitCode = Program.MainCore(
            [
                "validate",
                "--input", repoPath,
                "--report-json", reportPath
            ],
            ThrowingCompletionClientFactory.Instance
        );

        Assert.Equal(0, exitCode);
        Assert.Equal(before, CaptureRepositoryFileHashes(repoPath));
        Assert.True(File.Exists(reportPath));
        string reportJson = File.ReadAllText(reportPath);
        Assert.Contains(
            "\"preparedRequestCount\"",
            reportJson,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "\"preparedPolicyCounts\"",
            reportJson,
            StringComparison.Ordinal
        );
        SessionJournalOfflineValidationReport report =
            JsonSerializer.Deserialize<SessionJournalOfflineValidationReport>(
                reportJson,
                WebJsonOptions
            ) ?? throw new Xunit.Sdk.XunitException(
                "Validation report did not deserialize."
            );
        Assert.Equal(SessionExecutionPhase.Idle, report.ExecutionPhase);
        Assert.True(report.EventCount >= 5);
        Assert.True(report.LogicalPayloadBytes > 0);
        Assert.Equal(0, report.PreparedRequestCount);
    }

    private string CreateJournal() {
        string repoPath = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        using var engine = SessionJournalEngine.Create(
            repoPath,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );
        engine.AppendObservation("old observation");
        EventAddress anchor = engine.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("old action")]),
            new CompletionDescriptor("import", "import-v1", "model-A")
        );
        _ = anchor;
        return repoPath;
    }

    private static IReadOnlyDictionary<string, string> CaptureRepositoryFileHashes(
        string path
    ) {
        if (!Directory.Exists(path)) {
            return new SortedDictionary<string, string>(
                StringComparer.Ordinal
            );
        }
        return Directory.EnumerateFiles(
                path,
                "*",
                SearchOption.AllDirectories
            )
            .ToDictionary(
                file => Path.GetRelativePath(path, file),
                file => Convert.ToHexStringLower(
                    SHA256.HashData(File.ReadAllBytes(file))
                ),
                StringComparer.Ordinal
            );
    }

    private sealed class ThrowingCompletionClientFactory
        : ICompletionClientFactory {
        public static ThrowingCompletionClientFactory Instance { get; } = new();

        public ICompletionClient Create(CompletionConnectionConfig connection)
            => throw new InvalidOperationException(
                $"Offline command must not create completion client '{connection.Id}'."
            );
    }
}
