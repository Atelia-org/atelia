using System.Diagnostics;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

public sealed class DerivedRecapEpochCrashRecoveryTests : IDisposable {
    private readonly List<string> _paths = [];

    [Fact]
    public async Task ResetCrashAfterQuarantineRenameIsRecoveredOnReopen() {
        if (!OperatingSystem.IsLinux()) {
            return;
        }
        string path = NewPath();
        RefId refId;
        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-a", "system-a", "surface-a")
        )) {
            refId = engine.BranchRefId;
            DerivedRecapEpochStore store = DerivedRecapEpochStore.Open(
                path,
                refId
            );
            await store.CreateAsync();
        }

        await RunCrashHarnessAsync(
            path,
            "reset",
            "reset-quarantine-renamed"
        );

        DerivedRecapEpochStore reopened = DerivedRecapEpochStore.Open(
            path,
            refId
        );
        await reopened.EnsureCreatedAsync();
        Assert.IsType<RecapEpochBuildingSelectionResult.Empty>(
            await reopened.SelectBuildingAsync()
        );
        string quarantineRoot = Path.Combine(
            path,
            "derived",
            "recap",
            "v8",
            "quarantine"
        );
        Assert.Empty(Directory.Exists(quarantineRoot)
            ? Directory.EnumerateFileSystemEntries(quarantineRoot)
            : []);
    }

    public void Dispose() {
        foreach (string path in _paths) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch {
                // Best-effort cleanup for test-owned repositories.
            }
        }
    }

    private async Task RunCrashHarnessAsync(
        string repositoryPath,
        string operation,
        string failpoint
    ) {
        string harnessPath = GetCrashHarnessPath();
        Assert.True(File.Exists(harnessPath));
        var startInfo = new ProcessStartInfo("dotnet") {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = repositoryPath
        };
        startInfo.ArgumentList.Add(harnessPath);
        startInfo.ArgumentList.Add(operation);
        startInfo.ArgumentList.Add(failpoint);
        startInfo.ArgumentList.Add(repositoryPath);
        startInfo.Environment["COMPlus_DbgEnableMiniDump"] = "0";
        startInfo.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Failed to start DerivedRecap crash harness."
            );
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync()
            .WaitAsync(TimeSpan.FromSeconds(30));
        string output = await stdout;
        string error = await stderr;
        Assert.NotEqual(0, process.ExitCode);
        Assert.NotEqual(3, process.ExitCode);
        Assert.Contains(
            $"Intentional DerivedRecap v8 crash at '{failpoint}'",
            output + error,
            StringComparison.Ordinal
        );
    }

    private string NewPath() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-recap-v8-crash-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }

    private static string GetCrashHarnessPath() {
        string repositoryRoot = FindRepositoryRoot();
        string configuration = Directory.GetParent(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)
        )?.Name ?? "Debug";
        return Path.Combine(
            repositoryRoot,
            "tests",
            "SessionJournal.DerivedRecap.Store.CrashHarness",
            "bin",
            configuration,
            "net10.0",
            "Atelia.SessionJournal.DerivedRecap.Store.CrashHarness.dll"
        );
    }

    private static string FindRepositoryRoot() {
        DirectoryInfo? cursor = new(AppContext.BaseDirectory);
        while (cursor is not null) {
            if (File.Exists(Path.Combine(cursor.FullName, "Atelia.sln"))) {
                return cursor.FullName;
            }
            cursor = cursor.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate Atelia.sln from test output."
        );
    }
}
