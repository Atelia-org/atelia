using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.Testing;
using Xunit;
using Xunit.Sdk;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaCodexDelegationLiveTests {
    private const string LiveGate =
        "ATELIA_RUN_GALATEA_CODEX_DELEGATION_LIVE";
    private const string DelegatesConfigEnvironment =
        "ATELIA_GALATEA_CODEX_DELEGATES_CONFIG";
    private static readonly TimeSpan HardDeadline = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan GitDeadline = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan GitReapDeadline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CoordinatorDisposeDeadline =
        TimeSpan.FromSeconds(20);
    private const int CanaryShutdownGraceMs = 2_000;
    private const int MinimumShutdownGraceMs = 10;
    private const int MaximumShutdownGraceMs = 30_000;
    private const int SidecarDisposeMarginMs = 5_000;
    private const int MaximumGitOutputChars = 64 * 1024;
    private const UnixFileMode OwnerDirectoryMode =
        UnixFileMode.UserRead
        | UnixFileMode.UserWrite
        | UnixFileMode.UserExecute;

    [GalateaLiveFact]
    [Trait("Category", "LiveE2E")]
    public async Task TwoMails_ReuseOneRealCodexThread_AndRepliesAreOneShot() {
        if (!OperatingSystem.IsLinux()) {
            throw new XunitException(
                "The Galatea Codex live canary requires Linux."
            );
        }

        string sourceConfigPath = RequireAbsoluteDelegatesConfigPath();
        string sourceRepositoryRoot = FindContainingGitRoot(
            sourceConfigPath
        );
        await RequireIgnoredConfigAsync(
            sourceRepositoryRoot,
            sourceConfigPath
        );
        GalateaDelegateConfig source = GalateaDelegateConfigReader.Read(
            sourceConfigPath
        );
        if (!File.Exists(source.Sidecar.EntryPoint)) {
            throw new XunitException(
                "The configured Galatea Node sidecar entry is missing; run npm run build first."
            );
        }

        OwnedTemporaryRoot temporary = CreateOwnedTemporaryRoot(
            Path.GetTempPath(),
            sourceRepositoryRoot
        );
        bool gitInitialized = false;
        GalateaCodexSidecarClient? sidecar = null;
        GalateaDelegationCoordinator? coordinator = null;
        Exception? testFailure = null;
        CleanupResult cleanup = CleanupResult.Success;
        TimeSpan sidecarDisposeDeadline = CreateSidecarDisposeDeadline(
            CanaryShutdownGraceMs
        );
        try {
            await RunGitRequiredAsync(
                temporary.RootPath,
                ["-c", "init.templateDir=", "init", "--quiet"],
                "GIT_INIT_FAILED"
            );
            gitInitialized = true;

            GalateaDelegateRouteConfig sourceRoute = source.CodexRoute;
            var isolatedConfig = new GalateaDelegateConfig(
                source.Sidecar with {
                    ShutdownGraceMs = CanaryShutdownGraceMs
                },
                Array.AsReadOnly([temporary.RootPath]),
                Array.AsReadOnly([
                    sourceRoute with {
                        Recipient = GalateaDelegateConfigReader
                            .CanonicalRecipient,
                        Kind = GalateaDelegateConfigReader
                            .CodexAppServerKind,
                        Cwd = temporary.RootPath,
                        Mode = GalateaDelegateMode.Research,
                        Network = false
                    }
                ])
            );
            sidecar = new GalateaCodexSidecarClient(isolatedConfig);
            coordinator = new GalateaDelegationCoordinator(
                "live-canary",
                isolatedConfig.CodexRoute,
                sidecar
            );

            string nonce = LowerHex(RandomNumberGenerator.GetBytes(24));
            using var hardDeadline = new CancellationTokenSource(
                HardDeadline
            );
            string firstBody =
                "Remember this opaque continuity token for our next turn. "
                + "Reply only with a short confirmation that you stored it; "
                + "do not repeat or transform the token: "
                + nonce;
            Assert.True(coordinator.TryCaptureBatch(
                "live-turn-1",
                Head(1),
                [Mail(firstBody)]
            ));
            await coordinator.PumpTaskForTest.WaitAsync(
                hardDeadline.Token
            );

            GalateaDelegateCandidateSnapshot first = RequireReplyReady(
                coordinator,
                index: 0,
                expectedCount: 1
            );
            string firstThreadId = RequireBoundThread(coordinator, first);
            using (GalateaDelegationCoordinator.GalateaReadyReplyLease lease =
                   coordinator.BeginReadyReplyCutoff()) {
                _ = RequireSingleReply(lease);
                lease.Commit();
            }
            RequireEmptyCutoff(coordinator);

            const string secondBody =
                "Accurately recall the exact opaque continuity token from "
                + "my immediately preceding message. Return only that token.";
            Assert.False(
                secondBody.Contains(nonce, StringComparison.Ordinal),
                "The second mail must not repeat the continuity token."
            );
            Assert.True(coordinator.TryCaptureBatch(
                "live-turn-2",
                Head(2),
                [Mail(secondBody)]
            ));
            await coordinator.PumpTaskForTest.WaitAsync(
                hardDeadline.Token
            );

            GalateaDelegateCandidateSnapshot second = RequireReplyReady(
                coordinator,
                index: 1,
                expectedCount: 2
            );
            Assert.True(
                string.Equals(
                    firstThreadId,
                    second.ThreadId,
                    StringComparison.Ordinal
                ),
                "The second mail did not reuse the first Codex thread."
            );
            using (GalateaDelegationCoordinator.GalateaReadyReplyLease lease =
                   coordinator.BeginReadyReplyCutoff()) {
                GalateaReadyNotice.Reply reply = RequireSingleReply(lease);
                Assert.True(
                    reply.Body.Contains(nonce, StringComparison.Ordinal),
                    "The second delegate reply did not recall the prior token."
                );
                lease.Commit();
            }
            RequireEmptyCutoff(coordinator);
        }
        catch (Exception exception) {
            testFailure = exception;
        }
        finally {
            cleanup = await CleanupAsync(
                coordinator,
                sidecar,
                temporary,
                gitInitialized,
                sidecarDisposeDeadline
            );
        }
        ThrowIfFailed(testFailure, cleanup);
    }

    [Theory]
    [InlineData(10, 5_020)]
    [InlineData(CanaryShutdownGraceMs, 9_000)]
    [InlineData(30_000, 65_000)]
    public void SidecarDisposeDeadline_CoversGracefulAndKillReapPhases(
        int shutdownGraceMs,
        int expectedDeadlineMs
    ) {
        Assert.Equal(
            TimeSpan.FromMilliseconds(expectedDeadlineMs),
            CreateSidecarDisposeDeadline(shutdownGraceMs)
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateSidecarDisposeDeadline(MinimumShutdownGraceMs - 1)
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateSidecarDisposeDeadline(MaximumShutdownGraceMs + 1)
        );
    }

    [Fact]
    public void TemporaryRoot_IsDisjointNoFollowAndExactMode0700() {
        if (!OperatingSystem.IsLinux()) {
            return;
        }
        string fixture = CreateOwnedFixtureRoot();
        try {
            string source = CreateOwnedChild(fixture, "source");
            Assert.Throws<ArgumentException>(() =>
                CreateOwnedTemporaryRoot(source, source)
            );
            Assert.Empty(Directory.EnumerateFileSystemEntries(source));

            string realParent = CreateOwnedChild(fixture, "real-temp");
            string linkedParent = Path.Combine(fixture, "linked-temp");
            Directory.CreateSymbolicLink(linkedParent, realParent);
            Assert.Throws<InvalidDataException>(() =>
                CreateOwnedTemporaryRoot(linkedParent, source)
            );
            Assert.Empty(Directory.EnumerateFileSystemEntries(realParent));

            string safeParent = CreateOwnedChild(fixture, "safe-temp");
            OwnedTemporaryRoot temporary = CreateOwnedTemporaryRoot(
                safeParent,
                source
            );
            Assert.Equal(
                OwnerDirectoryMode,
                File.GetUnixFileMode(temporary.RootPath)
            );
            TestDirectorySafety.DeleteOwnedTreeNoFollow(
                temporary.RootPath
            );
        }
        finally {
            TestDirectorySafety.DeleteOwnedTreeNoFollow(fixture);
        }
    }

    [Fact]
    public async Task Cleanup_RetainsRootWhenReapIsUnproven_AndCombinesFailures() {
        if (!OperatingSystem.IsLinux()) {
            return;
        }
        string fixture = CreateOwnedFixtureRoot();
        try {
            string source = CreateOwnedChild(fixture, "source");
            string tempParent = CreateOwnedChild(fixture, "temp");
            OwnedTemporaryRoot temporary = CreateOwnedTemporaryRoot(
                tempParent,
                source
            );
            CleanupResult cleanup = await CleanupAsync(
                disposeCoordinator: null,
                disposeSidecar: static () => Task.FromException(
                    new IOException("stable fixture failure")
                ),
                temporary,
                gitInitialized: false,
                sidecarDisposeDeadline: TimeSpan.FromSeconds(1)
            );

            Assert.Equal(
                "SIDECAR_DISPOSE_OR_REAP_FAILED",
                cleanup.FailureCode
            );
            Assert.True(cleanup.TemporaryRootRetained);
            Assert.True(Directory.Exists(temporary.RootPath));

            var primary = new InvalidOperationException(
                "stable primary fixture failure"
            );
            AggregateException combined = Assert.Throws<AggregateException>(
                () => ThrowIfFailed(primary, cleanup)
            );
            Assert.Equal(2, combined.InnerExceptions.Count);
            Assert.Same(primary, combined.InnerExceptions[0]);
            Assert.Contains(
                "SIDECAR_DISPOSE_OR_REAP_FAILED",
                combined.InnerExceptions[1].Message,
                StringComparison.Ordinal
            );
        }
        finally {
            TestDirectorySafety.DeleteOwnedTreeNoFollow(fixture);
        }
    }

    [Fact]
    public async Task GitStatus_IgnoresAmbientGlobalFsmonitorAndRepoRedirects() {
        if (!OperatingSystem.IsLinux()) {
            return;
        }
        string fixture = CreateOwnedFixtureRoot();
        try {
            string repository = CreateOwnedChild(fixture, "repo");
            await RunGitRequiredAsync(
                repository,
                ["-c", "init.templateDir=", "init", "--quiet"],
                "GIT_INIT_FAILED"
            );
            File.WriteAllText(
                Path.Combine(repository, "tracked.txt"),
                "fixture"
            );
            await RunGitRequiredAsync(
                repository,
                ["add", "--", "tracked.txt"],
                "GIT_ADD_FAILED"
            );

            string witness = Path.Combine(fixture, "fsmonitor-witness");
            string hook = Path.Combine(fixture, "fsmonitor-hook.sh");
            File.WriteAllText(
                hook,
                "#!/bin/sh\n: > \"" + witness + "\"\nprintf '\\n'\n"
            );
            File.SetUnixFileMode(hook, OwnerDirectoryMode);
            string globalConfig = Path.Combine(fixture, "global.gitconfig");
            File.WriteAllText(
                globalConfig,
                "[core]\n\tfsmonitor = " + hook + "\n"
            );
            ProcessResult configured = await RunGitAsync(
                repository,
                [
                    "config", "--file", globalConfig,
                    "--get", "core.fsmonitor"
                ]
            );
            Assert.Equal(0, configured.ExitCode);
            Assert.Equal(hook, configured.StandardOutput.Trim());

            var hostileAmbient = new Dictionary<string, string?> {
                ["GIT_DIR"] = Path.Combine(fixture, "redirected-dir"),
                ["GIT_COMMON_DIR"] = Path.Combine(
                    fixture,
                    "redirected-common"
                ),
                ["GIT_TEMPLATE_DIR"] = Path.Combine(
                    fixture,
                    "redirected-template"
                ),
                ["GIT_CONFIG_GLOBAL"] = globalConfig,
                ["GIT_CONFIG_SYSTEM"] = globalConfig,
                ["GIT_CONFIG_NOSYSTEM"] = "0",
                ["GIT_CONFIG_COUNT"] = "1",
                ["GIT_CONFIG_KEY_0"] = "core.fsmonitor",
                ["GIT_CONFIG_VALUE_0"] = hook
            };
            ProcessResult status = await RunGitAsync(
                repository,
                ["status", "--porcelain=v1", "--untracked-files=all"],
                hostileAmbient
            );

            Assert.Equal(0, status.ExitCode);
            Assert.False(File.Exists(witness));
        }
        finally {
            TestDirectorySafety.DeleteOwnedTreeNoFollow(fixture);
        }
    }

    private static string RequireAbsoluteDelegatesConfigPath() {
        string? configured = Environment.GetEnvironmentVariable(
            DelegatesConfigEnvironment
        );
        if (string.IsNullOrWhiteSpace(configured)
            || !Path.IsPathFullyQualified(configured)
            || !string.Equals(
                Path.GetFileName(configured),
                "delegates.json",
                StringComparison.Ordinal
            )) {
            throw new XunitException(
                $"{DelegatesConfigEnvironment} must name an absolute ignored delegates.json."
            );
        }
        return Path.GetFullPath(configured);
    }

    private static string FindContainingGitRoot(string path) {
        DirectoryInfo? directory = new FileInfo(path).Directory;
        while (directory is not null) {
            string marker = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker)) {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new XunitException(
            "The configured delegates.json must be inside a Git worktree."
        );
    }

    private static async Task RequireIgnoredConfigAsync(
        string repositoryRoot,
        string configPath
    ) {
        string relative = Path.GetRelativePath(repositoryRoot, configPath);
        if (relative == ".."
            || relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal
            )) {
            throw new XunitException(
                "The configured delegates.json is outside its Git worktree."
            );
        }
        ProcessResult result = await RunGitAsync(
            repositoryRoot,
            ["check-ignore", "--quiet", "--", relative]
        );
        if (result.ExitCode != 0) {
            throw new XunitException(
                "The configured delegates.json is not ignored by Git."
            );
        }
    }

    private static OwnedTemporaryRoot CreateOwnedTemporaryRoot(
        string configuredTempParent,
        string sourceRepositoryRoot
    ) {
        string parent = TestDirectorySafety.Normalize(configuredTempParent);
        TestDirectorySafety.EnsureExistingPathChainHasNoReparsePoint(parent);
        FileAttributes parentAttributes = File.GetAttributes(parent);
        TestDirectorySafety.RejectReparsePoint(parent, parentAttributes);
        if ((parentAttributes & FileAttributes.Directory) == 0) {
            throw new InvalidDataException(
                "The live-canary temporary parent is not a directory."
            );
        }
        string source = TestDirectorySafety.Normalize(sourceRepositoryRoot);
        TestDirectorySafety.EnsureExistingPathChainHasNoReparsePoint(source);
        FileAttributes sourceAttributes = File.GetAttributes(source);
        TestDirectorySafety.RejectReparsePoint(source, sourceAttributes);
        if ((sourceAttributes & FileAttributes.Directory) == 0) {
            throw new InvalidDataException(
                "The live-canary source repository is not a directory."
            );
        }
        TestDirectorySafety.EnsureDisjoint(parent, source);

        string path = Path.Combine(
            parent,
            "atelia-galatea-codex-live-" + LowerHex(
                RandomNumberGenerator.GetBytes(16)
            )
        );
        if (Path.Exists(path)) {
            throw new IOException(
                "The randomized live-canary leaf already exists."
            );
        }
        TestDirectorySafety.CreateDirectoryNew(path);
        try {
            RequireOwnedTemporaryRoot(new(parent, path));
            return new(parent, path);
        }
        catch {
            TestDirectorySafety.DeleteOwnedTreeNoFollow(path);
            throw;
        }
    }

    private static void RequireOwnedTemporaryRoot(
        OwnedTemporaryRoot temporary
    ) {
        if (!OperatingSystem.IsLinux()) {
            throw new PlatformNotSupportedException(
                "Owner-only live-canary roots require Linux."
            );
        }
        string parent = TestDirectorySafety.Normalize(temporary.ParentPath);
        string root = TestDirectorySafety.Normalize(temporary.RootPath);
        if (!string.Equals(
                Path.GetDirectoryName(root),
                parent,
                StringComparison.Ordinal
            )
            || !Path.GetFileName(root).StartsWith(
                "atelia-galatea-codex-live-",
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "The live-canary root is not an exact owned child."
            );
        }
        TestDirectorySafety.EnsureExistingPathChainHasNoReparsePoint(parent);
        TestDirectorySafety.EnsureExistingPathChainHasNoReparsePoint(root);
        FileAttributes attributes = File.GetAttributes(root);
        TestDirectorySafety.RejectReparsePoint(root, attributes);
        if ((attributes & FileAttributes.Directory) == 0
            || File.GetUnixFileMode(root) != OwnerDirectoryMode) {
            throw new InvalidDataException(
                "The live-canary root must be an owner-only mode 0700 directory."
            );
        }
    }

    private static GalateaDelegateCandidateSnapshot RequireReplyReady(
        GalateaDelegationCoordinator coordinator,
        int index,
        int expectedCount
    ) {
        IReadOnlyList<GalateaDelegateCandidateSnapshot> snapshot =
            coordinator.Snapshot();
        Assert.Equal(expectedCount, snapshot.Count);
        Assert.Equal(
            GalateaDelegateCandidateState.ReplyReady,
            snapshot[index].State
        );
        return snapshot[index];
    }

    private static string RequireBoundThread(
        GalateaDelegationCoordinator coordinator,
        GalateaDelegateCandidateSnapshot first
    ) {
        string? bound = coordinator.BoundThreadIdForTest;
        Assert.True(
            !string.IsNullOrWhiteSpace(bound),
            "The first mail did not establish a Codex thread binding."
        );
        Assert.True(
            string.Equals(bound, first.ThreadId, StringComparison.Ordinal),
            "The first candidate does not match the bound Codex thread."
        );
        return bound!;
    }

    private static GalateaReadyNotice.Reply RequireSingleReply(
        GalateaDelegationCoordinator.GalateaReadyReplyLease lease
    ) {
        if (lease.Notices.Count != 1) {
            throw new XunitException(
                "The real Codex dispatch did not produce exactly one notice."
            );
        }
        if (lease.Notices[0] is not GalateaReadyNotice.Reply reply) {
            throw new XunitException(
                "The real Codex dispatch produced a delivery failure."
            );
        }
        return reply;
    }

    private static void RequireEmptyCutoff(
        GalateaDelegationCoordinator coordinator
    ) {
        using GalateaDelegationCoordinator.GalateaReadyReplyLease empty =
            coordinator.BeginReadyReplyCutoff();
        if (empty.Notices.Count != 0) {
            throw new XunitException(
                "A committed delegate reply was offered more than once."
            );
        }
    }

    private static SendMailIntent Mail(string body) => new(
        GalateaDelegateConfigReader.CanonicalRecipient,
        Subject: null,
        body,
        InReplyToMessageId: null,
        EvidenceQuote: "live canary dispatch"
    );

    private static EventAddress Head(uint value) =>
        EventAddressTextCodec.Parse(
            $"ej1:{value:x16}{value:x8}{value:x8}"
        );

    private static Task<CleanupResult> CleanupAsync(
        GalateaDelegationCoordinator? coordinator,
        GalateaCodexSidecarClient? sidecar,
        OwnedTemporaryRoot temporary,
        bool gitInitialized,
        TimeSpan sidecarDisposeDeadline
    ) => CleanupAsync(
        coordinator is null
            ? null
            : () => coordinator.DisposeAsync().AsTask(),
        sidecar is null
            ? null
            : () => sidecar.DisposeAsync().AsTask(),
        temporary,
        gitInitialized,
        sidecarDisposeDeadline
    );

    private static async Task<CleanupResult> CleanupAsync(
        Func<Task>? disposeCoordinator,
        Func<Task>? disposeSidecar,
        OwnedTemporaryRoot temporary,
        bool gitInitialized,
        TimeSpan sidecarDisposeDeadline
    ) {
        var failures = new List<string>();
        bool coordinatorStopped = await TryDisposeAsync(
            disposeCoordinator,
            CoordinatorDisposeDeadline
        );
        if (!coordinatorStopped) {
            failures.Add("COORDINATOR_DISPOSE_FAILED");
        }
        bool sidecarStopped = await TryDisposeAsync(
            disposeSidecar,
            sidecarDisposeDeadline
        );
        if (!sidecarStopped) {
            failures.Add("SIDECAR_DISPOSE_OR_REAP_FAILED");
        }

        // The sidecar process owns this cwd. Never inspect or recursively
        // delete it unless both lifecycle owners have conclusively stopped.
        if (!coordinatorStopped || !sidecarStopped) {
            return CleanupResult.Failed(failures, retained: true);
        }

        try {
            RequireOwnedTemporaryRoot(temporary);
            if (gitInitialized) {
                await VerifyTemporaryRepositoryIsUntouchedAsync(
                    temporary.RootPath
                );
            }
            else {
                TestDirectorySafety.RequireOwnedEmptyDirectory(
                    temporary.RootPath
                );
            }
        }
        catch {
            failures.Add("TEMP_REPOSITORY_NOT_SAFE_TO_DELETE");
            return CleanupResult.Failed(failures, retained: true);
        }

        try {
            TestDirectorySafety.DeleteOwnedTreeNoFollow(
                temporary.RootPath
            );
        }
        catch {
            failures.Add("TEMP_REPOSITORY_DELETE_FAILED");
            return CleanupResult.Failed(
                failures,
                retained: Path.Exists(temporary.RootPath)
            );
        }
        return CleanupResult.Success;
    }

    private static async Task<bool> TryDisposeAsync(
        Func<Task>? dispose,
        TimeSpan deadline
    ) {
        if (dispose is null) {
            return true;
        }
        try {
            await dispose().WaitAsync(deadline);
            return true;
        }
        catch {
            return false;
        }
    }

    private static TimeSpan CreateSidecarDisposeDeadline(
        int shutdownGraceMs
    ) {
        if (shutdownGraceMs is < MinimumShutdownGraceMs
            or > MaximumShutdownGraceMs) {
            throw new ArgumentOutOfRangeException(nameof(shutdownGraceMs));
        }
        long milliseconds = checked(
            2L * shutdownGraceMs + SidecarDisposeMarginMs
        );
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static void ThrowIfFailed(
        Exception? testFailure,
        CleanupResult cleanup
    ) {
        if (testFailure is not null && cleanup.FailureCode is not null) {
            throw new AggregateException(
                "The live canary and its cleanup both failed.",
                testFailure,
                new XunitException(
                    $"cleanup={cleanup.FailureCode}; "
                    + $"tempRetained={cleanup.TemporaryRootRetained}"
                )
            );
        }
        if (testFailure is not null) {
            ExceptionDispatchInfo.Capture(testFailure).Throw();
        }
        if (cleanup.FailureCode is not null) {
            throw new XunitException(
                $"Live-canary cleanup failed: {cleanup.FailureCode}; "
                + $"tempRetained={cleanup.TemporaryRootRetained}."
            );
        }
    }

    private static async Task VerifyTemporaryRepositoryIsUntouchedAsync(
        string tempRoot
    ) {
        ProcessResult status = await RunGitAsync(
            tempRoot,
            ["status", "--porcelain=v1", "--untracked-files=all"]
        );
        if (status.ExitCode != 0 || status.StandardOutput.Length != 0) {
            throw new InvalidDataException(
                "The temporary canary repository is not clean."
            );
        }
        string[] entries = Directory.EnumerateFileSystemEntries(
                tempRoot,
                "*",
                SearchOption.TopDirectoryOnly
            )
            .Select(Path.GetFileName)
            .Where(static value => value is not null)
            .Select(static value => value!)
            .ToArray();
        if (entries.Length != 1
            || !string.Equals(entries[0], ".git", StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "The temporary canary repository has unexpected top-level entries."
            );
        }
    }

    private static async Task RunGitRequiredAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string failureCode
    ) {
        ProcessResult result = await RunGitAsync(
            workingDirectory,
            arguments
        );
        if (result.ExitCode != 0) {
            throw new XunitException(
                $"Live-canary precondition failed: {failureCode} "
                + $"(stdout={result.StandardOutput.Length}, "
                + $"stderr={result.StandardError.Length})."
            );
        }
    }

    private static async Task<ProcessResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? ambientOverridesForTest = null
    ) {
        var startInfo = new ProcessStartInfo {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        if (ambientOverridesForTest is not null) {
            foreach ((string key, string? value) in ambientOverridesForTest) {
                if (value is null) {
                    startInfo.Environment.Remove(key);
                }
                else {
                    startInfo.Environment[key] = value;
                }
            }
        }
        ScrubGitEnvironment(startInfo.Environment);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.fsmonitor=false");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.hooksPath=/dev/null");
        foreach (string argument in arguments) {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) {
            throw new XunitException(
                "Unable to start git for the live canary."
            );
        }
        process.StandardInput.Close();
        using var deadline = new CancellationTokenSource(GitDeadline);
        Task<string> stdout = ReadBoundedAsync(
            process.StandardOutput,
            deadline.Token
        );
        Task<string> stderr = ReadBoundedAsync(
            process.StandardError,
            deadline.Token
        );
        try {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch {
            bool reaped = await TryKillAndReapAsync(process);
            await ObserveAsync(stdout);
            await ObserveAsync(stderr);
            if (!reaped) {
                throw new XunitException(
                    "A live-canary git command timed out and was not reaped."
                );
            }
            throw new XunitException(
                "A live-canary git command did not finish within its deadline."
            );
        }
        return new ProcessResult(
            process.ExitCode,
            await stdout,
            await stderr
        );
    }

    private static async Task ObserveAsync(Task task) {
        try {
            await task.WaitAsync(GitReapDeadline);
        }
        catch {
            // Command failure is reported only through stable outer codes.
        }
    }

    private static async Task<bool> TryKillAndReapAsync(Process process) {
        try {
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) {
            // A concurrent exit is equivalent to a successful kill.
        }
        catch {
            return false;
        }
        try {
            using var reapDeadline = new CancellationTokenSource(
                GitReapDeadline
            );
            await process.WaitForExitAsync(reapDeadline.Token);
            return process.HasExited;
        }
        catch {
            return false;
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken ct
    ) {
        var result = new StringBuilder();
        char[] buffer = new char[4 * 1024];
        while (true) {
            int read = await reader.ReadAsync(buffer, ct);
            if (read == 0) {
                return result.ToString();
            }
            if (result.Length > MaximumGitOutputChars - read) {
                throw new InvalidDataException(
                    "A live-canary git command exceeded its output bound."
                );
            }
            result.Append(buffer, 0, read);
        }
    }

    private static void ScrubGitEnvironment(
        IDictionary<string, string?> environment
    ) {
        foreach (string key in environment.Keys.Where(static key =>
                     key.StartsWith("GIT_", StringComparison.Ordinal)
                 ).ToArray()) {
            environment.Remove(key);
        }
        environment["GIT_CONFIG_NOSYSTEM"] = "1";
        environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        environment["GIT_TERMINAL_PROMPT"] = "0";
    }

    private static string CreateOwnedFixtureRoot() {
        string parent = TestDirectorySafety.Normalize(Path.GetTempPath());
        TestDirectorySafety.EnsureExistingPathChainHasNoReparsePoint(parent);
        string fixture = Path.Combine(
            parent,
            "atelia-galatea-live-test-" + LowerHex(
                RandomNumberGenerator.GetBytes(16)
            )
        );
        if (Path.Exists(fixture)) {
            throw new IOException("The randomized fixture already exists.");
        }
        TestDirectorySafety.CreateDirectoryNew(fixture);
        return fixture;
    }

    private static string CreateOwnedChild(string parent, string name) {
        string path = Path.Combine(parent, name);
        TestDirectorySafety.CreateDirectoryNew(path);
        return path;
    }

    private static string LowerHex(byte[] bytes) =>
        Convert.ToHexString(bytes).ToLowerInvariant();

    private sealed record OwnedTemporaryRoot(
        string ParentPath,
        string RootPath
    );

    private sealed record CleanupResult(
        string? FailureCode,
        bool TemporaryRootRetained
    ) {
        internal static CleanupResult Success { get; } = new(null, false);

        internal static CleanupResult Failed(
            IReadOnlyList<string> failures,
            bool retained
        ) {
            if (failures.Count == 0) {
                throw new ArgumentException(
                    "A failed cleanup requires at least one stable code.",
                    nameof(failures)
                );
            }
            return new(
                string.Join("+", failures.Distinct(StringComparer.Ordinal)),
                retained
            );
        }
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError
    );

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    private sealed class GalateaLiveFactAttribute : FactAttribute {
        public GalateaLiveFactAttribute() {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable(LiveGate),
                    "1",
                    StringComparison.Ordinal
                )) {
                Skip =
                    $"Set {LiveGate}=1 to run the isolated real Codex canary.";
            }
        }
    }
}
