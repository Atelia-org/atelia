using System.Diagnostics;
using Atelia.SessionJournal.MemoPod;

namespace Atelia.SessionJournal.MemoPod.Tests.Store;

public sealed class MemoPodCrashRecoveryTests : IDisposable {
    private static readonly MemoPodId PodId = MemoPodId.Parse(
        "77777777777777777777777777777777"
    );

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "atelia-memo-pod-crash-tests",
        Guid.NewGuid().ToString("N")
    );

    public MemoPodCrashRecoveryTests() {
        Directory.CreateDirectory(_root);
    }

    [Theory]
    [InlineData("create", "before-publish")]
    [InlineData("create", "after-install-before-fsync")]
    [InlineData("create", "after-fsync")]
    [InlineData("replace", "before-publish")]
    [InlineData("replace", "after-install-before-fsync")]
    [InlineData("replace", "after-fsync")]
    public void ProcessDeathLeavesOnlyAllowedCompleteAuthority(
        string operation,
        string failpoint
    ) {
        if (operation == "replace") {
            MemoPodPublishResult setup = MemoPodDocumentPublisher.Publish(
                _root,
                Document("old"),
                MemoPodPublishMode.CreateNew
            );
            Assert.Equal(
                MemoPodPublishSettlement.Published,
                setup.Settlement
            );
        }

        RunCrashHarness(operation, failpoint);

        MemoPodDocument? reopened = TryStrictReopen();
        if (operation == "create") {
            Assert.True(
                reopened is null
                    || Assert.Single(reopened.Memos).ExactText == "new"
            );
        }
        else {
            Assert.NotNull(reopened);
            Assert.Contains(
                Assert.Single(reopened.Memos).ExactText,
                new[] { "old", "new" }
            );
        }
    }

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }

    private MemoPodDocument? TryStrictReopen() {
        try {
            return MemoPodDocumentStore.Read(_root, PodId);
        }
        catch (MemoPodStoreException exception)
            when (exception.Code is MemoPodStoreErrorCode.DocumentAbsent) {
            return null;
        }
    }

    private void RunCrashHarness(string operation, string failpoint) {
        string configuration = Directory.GetParent(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)
        )?.Name ?? "Debug";
        string harness = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "SessionJournal.MemoPod.CrashHarness",
            "bin",
            configuration,
            "net10.0",
            "Atelia.SessionJournal.MemoPod.CrashHarness.dll"
        ));
        Assert.True(File.Exists(harness), harness);

        var start = new ProcessStartInfo("dotnet") {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _root
        };
        start.ArgumentList.Add(harness);
        start.ArgumentList.Add(operation);
        start.ArgumentList.Add(failpoint);
        start.ArgumentList.Add(_root);
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException(
                "MemoPod crash harness could not be started."
            );
        Assert.True(
            process.WaitForExit(milliseconds: 30_000),
            "MemoPod crash harness did not terminate."
        );
        string standardError = process.StandardError.ReadToEnd();
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains(
            $"{operation}/{failpoint}",
            standardError,
            StringComparison.Ordinal
        );
    }

    private static MemoPodDocument Document(string exactText)
        => new(
            PodId,
            "crash fixture",
            2,
            [new Memo(MemoId.FromOrdinal(1), exactText)]
        );
}
