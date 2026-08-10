using System.Diagnostics;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.RecapGrid.Control;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Control.Tests;

public sealed partial class ControlVerticalTests {
    [Theory]
    [InlineData("before-state-publish", false)]
    [InlineData("after-state-publish", true)]
    public async Task StatePublishCrashReopensAtExactOldOrNewState(
        string failpoint,
        bool installed
    ) {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        _ = Assert.IsType<RecapGridControlCreateResult.Created>(
            RecapGridControlFactory.Create(
                path,
                journal.BranchRefId,
                values.Admission
            )
        );
        RefId refId = journal.BranchRefId;
        journal.Dispose();

        string harness = CrashHarnessPath();
        Assert.True(File.Exists(harness), harness);
        var start = new ProcessStartInfo("dotnet") {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = path
        };
        start.ArgumentList.Add(harness);
        start.ArgumentList.Add(failpoint);
        start.ArgumentList.Add(path);
        start.Environment["COMPlus_DbgEnableMiniDump"] = "0";
        start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException(
                "Failed to start the Control crash harness."
            );
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(
            TimeSpan.FromSeconds(30)
        );
        string output = await stdout + await stderr;
        Assert.NotEqual(0, process.ExitCode);
        Assert.NotEqual(3, process.ExitCode);
        Assert.Contains(
            $"Intentional RecapGrid Control crash at '{failpoint}'",
            output,
            StringComparison.Ordinal
        );

        RecapGridControlInspectResult.Available inspected = Assert.IsType<
            RecapGridControlInspectResult.Available
        >(RecapGridControlMaintenance.Verify(
            path,
            refId
        ));
        Assert.Equal(installed ? 1 : 0, inspected.Snapshot.Families.Count);
    }

    private static string CrashHarnessPath() {
        DirectoryInfo? cursor = new(AppContext.BaseDirectory);
        while (cursor is not null
            && !File.Exists(Path.Combine(cursor.FullName, "Atelia.sln"))) {
            cursor = cursor.Parent;
        }
        if (cursor is null) {
            throw new DirectoryNotFoundException(
                "Could not locate Atelia.sln from test output."
            );
        }
        string configuration = Directory.GetParent(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)
        )?.Name ?? "Debug";
        return Path.Combine(
            cursor.FullName,
            "tests",
            "SessionJournal.RecapGrid.Control.CrashHarness",
            "bin",
            configuration,
            "net10.0",
            "Atelia.SessionJournal.RecapGrid.Control.CrashHarness.dll"
        );
    }
}
