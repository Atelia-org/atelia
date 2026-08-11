using System.Security.Cryptography;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.HistoryTimeline;
using Xunit;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class ProgramRecapHistoryLoadCommandTests : IDisposable {
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "atelia-recap-history-load-cli-tests",
        Guid.NewGuid().ToString("N")
    );

    public void Dispose() {
        try {
            if (Directory.Exists(_tempRoot)) {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch {
            // Best-effort cleanup for test-owned repositories.
        }
    }

    [Fact]
    public void InspectWritesDeterministicContentFreeReadOnlyReport() {
        string repoPath = CreateJournal(historyPairs: 12);
        IReadOnlyDictionary<string, FileSnapshot> before =
            CaptureRepositoryFiles(repoPath);
        string report1 = Path.Combine(_tempRoot, "report-1.json");
        string report2 = Path.Combine(_tempRoot, "report-2.json");
        var factory = new CountingCompletionClientFactory();

        Assert.Equal(0, Run([
            "recap-grid", "timeline", "history-load", "inspect",
            "--input", repoPath,
            "--report-json", report1
        ], factory));
        Assert.Equal(0, Run([
            "recap-grid", "timeline", "history-load", "inspect",
            "--input", repoPath,
            "--branch", "main",
            "--report-json", report2
        ], factory));

        Assert.Equal(0, factory.CreateCallCount);
        Assert.Equal(
            File.ReadAllBytes(report1),
            File.ReadAllBytes(report2)
        );
        Assert.Equal(before, CaptureRepositoryFiles(repoPath));
        Assert.False(Directory.Exists(
            Path.Combine(repoPath, "derived")
        ));
        Assert.False(Directory.Exists(
            Path.Combine(repoPath, "config")
        ));

        string json = File.ReadAllText(report1);
        AssertContentFree(json);
        using JsonDocument report = JsonDocument.Parse(json);
        JsonElement root = report.RootElement;
        Assert.Equal(
            "atelia.session-journal.recap-history-load-calibration.v1",
            root.GetProperty("schema").GetString()
        );
        Assert.Equal(
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            root.GetProperty("estimatorId").GetString()
        );
        Assert.Equal("main", root.GetProperty("branchName").GetString());
        Assert.StartsWith(
            "ej1:",
            root.GetProperty("capturedHead").GetString()
        );
        Assert.StartsWith(
            "ej1:",
            root.GetProperty("baseline").GetString()
        );

        JsonElement totals = root.GetProperty("totals");
        Assert.Equal(24, totals.GetProperty("rawEvents").GetInt32());
        Assert.Equal(
            24,
            totals.GetProperty("historyUnits").GetInt32()
        );
        Assert.Equal(
            24,
            totals.GetProperty("replaySafeBoundaries").GetInt32()
        );
        Assert.True(
            totals.GetProperty("historyLoad").GetInt64() > 0
        );
        Assert.True(
            totals.GetProperty("renderedUtf8Bytes").GetInt32() > 0
        );
        long totalLoad =
            totals.GetProperty("historyLoad").GetInt64();

        JsonElement[] byKind = root
            .GetProperty("byKind")
            .EnumerateArray()
            .ToArray();
        Assert.Collection(
            byKind,
            observation => {
                Assert.Equal(
                    "Observation",
                    observation.GetProperty("kind").GetString()
                );
                Assert.Equal(
                    12,
                    observation.GetProperty("historyUnits").GetInt32()
                );
            },
            action => {
                Assert.Equal(
                    "Action",
                    action.GetProperty("kind").GetString()
                );
                Assert.Equal(
                    12,
                    action.GetProperty("historyUnits").GetInt32()
                );
            }
        );

        JsonElement[] units = root
            .GetProperty("units")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(24, units.Length);
        Assert.Equal(0, units[0].GetProperty("ordinal").GetInt32());
        Assert.Equal(23, units[^1].GetProperty("ordinal").GetInt32());
        Assert.All(units, unit => {
            Assert.StartsWith(
                "ej1:",
                unit.GetProperty("sourceStartInclusive").GetString()
            );
            Assert.StartsWith(
                "ej1:",
                unit.GetProperty("sourceEndInclusive").GetString()
            );
            Assert.True(unit.GetProperty("load").GetInt64() > 0);
            Assert.True(
                unit.GetProperty("renderedUtf8Bytes").GetInt32() > 0
            );
        });

        JsonElement unitLoadDistribution = root
            .GetProperty("unitDistributions")
            .GetProperty("historyLoad");
        Assert.Equal(
            "nearest-rank",
            unitLoadDistribution.GetProperty("method").GetString()
        );
        Assert.Equal(
            24,
            unitLoadDistribution.GetProperty("count").GetInt32()
        );
        Assert.True(
            unitLoadDistribution.GetProperty("min").GetInt64()
            <= unitLoadDistribution.GetProperty("max").GetInt64()
        );

        JsonElement[] boundaries = root
            .GetProperty("boundaries")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(24, boundaries.Length);
        Assert.Equal(
            24,
            boundaries[^1]
                .GetProperty(
                    "completedHistoryUnitCountSinceBaseline"
                )
                .GetInt32()
        );
        Assert.Equal(
            totalLoad,
            boundaries[^1]
                .GetProperty(
                    "absorbedHistoryLoadSinceBaseline"
                )
                .GetInt64()
        );

        JsonElement[] windows = root
            .GetProperty("continuousWindowLoadDistributions")
            .EnumerateArray()
            .ToArray();
        Assert.Collection(
            windows,
            width20 => {
                Assert.Equal(
                    20,
                    width20.GetProperty("historyUnitWidth").GetInt32()
                );
                Assert.Equal(
                    5,
                    width20
                        .GetProperty("historyLoad")
                        .GetProperty("count")
                        .GetInt32()
                );
            },
            width24 => {
                Assert.Equal(
                    24,
                    width24.GetProperty("historyUnitWidth").GetInt32()
                );
                Assert.Equal(
                    1,
                    width24
                        .GetProperty("historyLoad")
                        .GetProperty("count")
                        .GetInt32()
                );
            }
        );
    }

    [Fact]
    public void InspectRejectsReportInsideRepositoryWithoutWrites() {
        string repoPath = CreateJournal(historyPairs: 1);
        IReadOnlyDictionary<string, FileSnapshot> before =
            CaptureRepositoryFiles(repoPath);
        string reportPath = Path.Combine(
            repoPath,
            "calibration.json"
        );
        var factory = new CountingCompletionClientFactory();

        Assert.Equal(1, Run([
            "recap-grid", "timeline", "history-load", "inspect",
            "--input", repoPath,
            "--report-json", reportPath
        ], factory));

        Assert.Equal(0, factory.CreateCallCount);
        Assert.False(File.Exists(reportPath));
        Assert.Equal(before, CaptureRepositoryFiles(repoPath));
    }

    [Fact]
    public void InspectRejectsUnknownOptionsAndSubcommands() {
        string repoPath = CreateJournal(historyPairs: 1);
        var factory = new CountingCompletionClientFactory();

        Assert.Equal(1, Run([
            "recap-grid", "timeline", "history-load", "inspect",
            "--input", repoPath,
            "--connections", "must-not-be-read.json"
        ], factory));
        Assert.Equal(1, Run([
            "recap-grid", "timeline", "history-load", "build",
            "--input", repoPath
        ], factory));
        Assert.Equal(0, factory.CreateCallCount);
    }

    private string CreateJournal(int historyPairs) {
        Directory.CreateDirectory(_tempRoot);
        string path = Path.Combine(
            _tempRoot,
            Guid.NewGuid().ToString("N")
        );
        using var engine = SJ.SessionJournalEngine.Create(
            path,
            new SJ.SessionCreateOptions(
                "SECRET-MODEL",
                "SECRET-PROMPT",
                "SECRET-SURFACE"
            )
        );
        for (int index = 0; index < historyPairs; index++) {
            engine.AppendObservation(
                $"SECRET-OBSERVATION-{index}"
            );
            _ = engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text(
                        $"SECRET-ACTION-{index}"
                    )
                ]),
                new CompletionDescriptor(
                    "SECRET-PROVIDER",
                    "SECRET-API",
                    "SECRET-MODEL"
                )
            );
        }
        return path;
    }

    private static int Run(
        string[] args,
        ICompletionClientFactory factory
    ) => Program.MainCore(args, factory);

    private static void AssertContentFree(string report) {
        foreach (string forbidden in new[] {
                     "SECRET-",
                     "\"content\"",
                     "\"toolName\"",
                     "\"prompt\"",
                     "\"connections\"",
                     "\"callLog\""
                 }) {
            Assert.DoesNotContain(
                forbidden,
                report,
                StringComparison.OrdinalIgnoreCase
            );
        }
    }

    private static IReadOnlyDictionary<string, FileSnapshot>
        CaptureRepositoryFiles(string path)
        => Directory.EnumerateFiles(
                path,
                "*",
                SearchOption.AllDirectories
            )
            .ToDictionary(
                file => Path.GetRelativePath(path, file),
                file => new FileSnapshot(
                    new FileInfo(file).Length,
                    Convert.ToHexStringLower(
                        SHA256.HashData(File.ReadAllBytes(file))
                    )
                ),
                StringComparer.Ordinal
            );

    private sealed record FileSnapshot(long Length, string Sha256);

    private sealed class CountingCompletionClientFactory
        : ICompletionClientFactory {
        internal int CreateCallCount { get; private set; }

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            CreateCallCount++;
            throw new InvalidOperationException(
                "History-load inspection must not create a "
                + "completion client."
            );
        }
    }
}
