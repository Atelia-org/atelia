using System.Security.Cryptography;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Atelia.SessionJournal.Cli;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class ProgramSessionJournalReadOnlyValidationTests
    : IDisposable {
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "atelia-session-read-only-validation-tests",
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

    [Theory]
    [InlineData("event")]
    [InlineData("ref-op")]
    [InlineData("ref-object")]
    public void ValidateSessionJournal_MalformedActiveTailFailsWithoutRepair(
        string target
    ) {
        string repoPath = CreateJournal();
        string activePath = GetActiveTailPath(repoPath, target);
        File.AppendAllBytes(activePath, new byte[] { 0, 0, 0, 0 });
        IReadOnlyDictionary<string, FileSnapshot> before =
            CaptureRepositoryFiles(repoPath);

        int exitCode = Program.MainCore(
            [
                "validate",
                "--input", repoPath
            ],
            ThrowingCompletionClientFactory.Instance
        );

        Assert.Equal(1, exitCode);
        Assert.Equal(before, CaptureRepositoryFiles(repoPath));
    }

    private static string GetActiveTailPath(
        string repoPath,
        string target
    ) => target switch {
        "event" => Path.Combine(
            repoPath,
            "events",
            "buckets",
            "000000",
            "00000001.rbf"
        ),
        "ref-op" => Path.Combine(
            repoPath,
            "refs",
            "ref-op-log.rbf"
        ),
        "ref-object" => Directory.GetFiles(
            Path.Combine(repoPath, "refs", "objects"),
            "*.rbf",
            SearchOption.AllDirectories
        ).Single(),
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    private string CreateJournal() {
        string repoPath = Path.Combine(
            _tempRoot,
            Guid.NewGuid().ToString("N")
        );
        using var engine = SessionJournalEngine.Create(
            repoPath,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );
        engine.AppendObservation("observation");
        return repoPath;
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

    private sealed class ThrowingCompletionClientFactory
        : ICompletionClientFactory {
        public static ThrowingCompletionClientFactory Instance { get; } = new();

        public ICompletionClient Create(CompletionConnectionConfig connection)
            => throw new InvalidOperationException(
                $"Offline command must not create completion client '{connection.Id}'."
            );
    }
}
