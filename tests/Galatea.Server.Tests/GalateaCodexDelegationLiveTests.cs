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
    private static readonly TimeSpan CoordinatorDisposeDeadline =
        TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SidecarDisposeDeadline =
        TimeSpan.FromSeconds(45);
    private const int MaximumGitOutputChars = 64 * 1024;

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

        string tempRoot = CreateOwnedTemporaryRoot();
        bool gitInitialized = false;
        GalateaCodexSidecarClient? sidecar = null;
        GalateaDelegationCoordinator? coordinator = null;
        Exception? testFailure = null;
        string? cleanupFailureCode = null;
        try {
            await RunGitRequiredAsync(
                tempRoot,
                ["-c", "init.templateDir=", "init", "--quiet"],
                "GIT_INIT_FAILED"
            );
            gitInitialized = true;

            GalateaDelegateRouteConfig sourceRoute = source.CodexRoute;
            var isolatedConfig = new GalateaDelegateConfig(
                source.Sidecar with { },
                Array.AsReadOnly([tempRoot]),
                Array.AsReadOnly([
                    sourceRoute with {
                        Recipient = GalateaDelegateConfigReader
                            .CanonicalRecipient,
                        Kind = GalateaDelegateConfigReader
                            .CodexAppServerKind,
                        Cwd = tempRoot,
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
            cleanupFailureCode = await CleanupAsync(
                coordinator,
                sidecar,
                tempRoot,
                gitInitialized
            );
        }

        if (testFailure is not null) {
            ExceptionDispatchInfo.Capture(testFailure).Throw();
        }
        if (cleanupFailureCode is not null) {
            throw new XunitException(
                "The live canary cleanup failed: " + cleanupFailureCode + "."
            );
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

    private static string CreateOwnedTemporaryRoot() {
        string parent = Path.GetFullPath(Path.GetTempPath());
        string path = Path.Combine(
            parent,
            "atelia-galatea-codex-live-" + LowerHex(
                RandomNumberGenerator.GetBytes(16)
            )
        );
        TestDirectorySafety.CreateDirectoryNew(path);
        return path;
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

    private static async Task<string?> CleanupAsync(
        GalateaDelegationCoordinator? coordinator,
        GalateaCodexSidecarClient? sidecar,
        string tempRoot,
        bool gitInitialized
    ) {
        string? failure = null;
        if (coordinator is not null) {
            try {
                await coordinator.DisposeAsync().AsTask().WaitAsync(
                    CoordinatorDisposeDeadline
                );
            }
            catch {
                failure ??= "COORDINATOR_DISPOSE_FAILED";
            }
        }
        if (sidecar is not null) {
            try {
                await sidecar.DisposeAsync().AsTask().WaitAsync(
                    SidecarDisposeDeadline
                );
            }
            catch {
                failure ??= "SIDECAR_DISPOSE_FAILED";
            }
        }
        if (gitInitialized) {
            try {
                await VerifyTemporaryRepositoryIsUntouchedAsync(tempRoot);
            }
            catch {
                failure ??= "TEMP_REPOSITORY_CHANGED";
            }
        }
        try {
            DeleteValidatedTemporaryRoot(tempRoot);
        }
        catch {
            failure ??= "TEMP_REPOSITORY_CLEANUP_FAILED";
        }
        return failure;
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

    private static void DeleteValidatedTemporaryRoot(string tempRoot) {
        string normalized = TestDirectorySafety.Normalize(tempRoot);
        string parent = TestDirectorySafety.Normalize(Path.GetTempPath());
        string expectedPrefix = "atelia-galatea-codex-live-";
        if (!string.Equals(
                Path.GetDirectoryName(normalized),
                parent,
                StringComparison.Ordinal
            )
            || !Path.GetFileName(normalized).StartsWith(
                expectedPrefix,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Refusing to delete an unrecognized live-canary path."
            );
        }
        TestDirectorySafety.DeleteOwnedTreeNoFollow(normalized);
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
        IReadOnlyList<string> arguments
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
        foreach (string argument in arguments) {
            startInfo.ArgumentList.Add(argument);
        }
        ScrubGitEnvironment(startInfo.Environment);
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
            try {
                process.Kill(entireProcessTree: true);
            }
            catch {
                // Preserve the stable command failure below.
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
                     key is "GIT_DIR" or "GIT_WORK_TREE"
                     or "GIT_INDEX_FILE" or "GIT_OBJECT_DIRECTORY"
                     or "GIT_ALTERNATE_OBJECT_DIRECTORIES"
                     or "GIT_CONFIG_COUNT" or "GIT_CONFIG_PARAMETERS"
                     || key.StartsWith("GIT_CONFIG_KEY_", StringComparison.Ordinal)
                     || key.StartsWith("GIT_CONFIG_VALUE_", StringComparison.Ordinal)
                 ).ToArray()) {
            environment.Remove(key);
        }
        environment["GIT_TERMINAL_PROMPT"] = "0";
    }

    private static string LowerHex(byte[] bytes) =>
        Convert.ToHexString(bytes).ToLowerInvariant();

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
