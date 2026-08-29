using System.Diagnostics;
using Atelia.MemoPod;

namespace Atelia.MemoPod.Tests.Store;

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
    [InlineData("correction", "before-publish")]
    [InlineData("correction", "after-install-before-fsync")]
    [InlineData("correction", "after-fsync")]
    public void ProcessDeathLeavesOnlyAllowedCompleteAuthority(
        string operation,
        string failpoint
    ) {
        if (operation is "replace" or "correction") {
            MemoPodPublishResult setup = MemoPodDocumentPublisher.Publish(
                _root,
                operation == "correction"
                    ? CorrectionOldDocument()
                    : Document("old"),
                MemoPodPublishMode.CreateNew
            );
            Assert.Equal(
                MemoPodPublishSettlement.Published,
                setup.Settlement
            );
        }

        RunCrashHarness(operation, failpoint);

        MemoPodDocument? reopened = TryStrictReopen();
        switch (operation, failpoint) {
            case ("create", "before-publish"):
                Assert.Null(reopened);
                break;
            case ("replace", "before-publish"):
                Assert.Equal("old", ReadOnlyMemoText(reopened));
                break;
            case ("correction", "before-publish"):
                AssertCorrectionDocument(
                    reopened,
                    MemoId.FromOrdinal(1),
                    "old",
                    expectedNextMemoOrdinal: 2
                );
                break;
            case ("create", "after-install-before-fsync"):
                Assert.True(
                    reopened is null || ReadOnlyMemoText(reopened) == "new"
                );
                break;
            case ("replace", "after-install-before-fsync"):
                Assert.Contains(
                    ReadOnlyMemoText(reopened),
                    new[] { "old", "new" }
                );
                break;
            case ("correction", "after-install-before-fsync"):
                AssertCorrectionOldOrNew(reopened);
                break;
            case ("correction", "after-fsync"):
                AssertCorrectionDocument(
                    reopened,
                    MemoId.FromOrdinal(2),
                    "new",
                    expectedNextMemoOrdinal: 3
                );
                break;
            case (_, "after-fsync"):
                Assert.Equal("new", ReadOnlyMemoText(reopened));
                break;
            default:
                throw new InvalidOperationException(
                    $"Unexpected crash case {operation}/{failpoint}."
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
            "MemoPod.CrashHarness",
            "bin",
            configuration,
            "net10.0",
            "Atelia.MemoPod.CrashHarness.dll"
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

    private static MemoPodDocument CorrectionOldDocument()
        => new(
            PodId,
            "crash fixture",
            2,
            [new Memo(MemoId.FromOrdinal(1), "old")]
        );

    private static void AssertCorrectionOldOrNew(
        MemoPodDocument? document
    ) {
        Assert.NotNull(document);
        bool isOld = IsCorrectionDocument(
            document,
            MemoId.FromOrdinal(1),
            "old",
            expectedNextMemoOrdinal: 2
        );
        bool isNew = IsCorrectionDocument(
            document,
            MemoId.FromOrdinal(2),
            "new",
            expectedNextMemoOrdinal: 3
        );
        Assert.True(
            isOld || isNew,
            "Correction authority must be the exact old or exact new document."
        );
    }

    private static void AssertCorrectionDocument(
        MemoPodDocument? document,
        MemoId expectedId,
        string expectedExactText,
        ulong expectedNextMemoOrdinal
    ) {
        Assert.NotNull(document);
        Assert.True(IsCorrectionDocument(
            document,
            expectedId,
            expectedExactText,
            expectedNextMemoOrdinal
        ));
    }

    private static bool IsCorrectionDocument(
        MemoPodDocument document,
        MemoId expectedId,
        string expectedExactText,
        ulong expectedNextMemoOrdinal
    ) => document.NextMemoOrdinal == expectedNextMemoOrdinal
        && document.Memos is [{ } memo]
        && memo.Id == expectedId
        && string.Equals(
            memo.ExactText,
            expectedExactText,
            StringComparison.Ordinal
        );

    private static string ReadOnlyMemoText(MemoPodDocument? document) {
        Assert.NotNull(document);
        return Assert.Single(document.Memos).ExactText;
    }
}
