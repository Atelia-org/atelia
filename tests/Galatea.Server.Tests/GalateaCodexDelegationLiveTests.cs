using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Atelia.Galatea.Server;
using Atelia.Testing;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaCodexDelegationLiveFactAttribute : FactAttribute {
    public const string RunGate =
        "ATELIA_RUN_GALATEA_CODEX_DELEGATION_LIVE";

    public GalateaCodexDelegationLiveFactAttribute() {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(RunGate),
                "1",
                StringComparison.Ordinal)) {
            Skip = $"Set {RunGate}=1 to run the real Codex V2 canary.";
        }
    }
}

public sealed class GalateaCodexDelegationLiveTests {
    private const string ConfigPathVariable =
        "ATELIA_GALATEA_CODEX_DELEGATES_CONFIG";
    private static readonly TimeSpan CanaryDeadline =
        TimeSpan.FromMinutes(3);
    private static readonly TimeSpan GitDeadline =
        TimeSpan.FromSeconds(15);

    [GalateaCodexDelegationLiveFact]
    public async Task DurableV2_EnsureStartInspectCompletesInCleanRepo() {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    GalateaCodexDelegationLiveFactAttribute.RunGate
                ),
                "1",
                StringComparison.Ordinal)) {
            return;
        }

        string configPath = Environment.GetEnvironmentVariable(
                ConfigPathVariable
            ) is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : throw new InvalidOperationException(
                $"The gated live canary requires {ConfigPathVariable}."
            );
        GalateaDelegateConfig source =
            GalateaDelegateConfigReader.Read(configPath);
        string repositoryPath = CreateIsolatedRepositoryPath();
        GalateaCodexDurableSidecarClient? client = null;
        Exception? primaryFailure = null;
        List<Exception>? cleanupFailures = null;
        bool repositoryCreated = false;
        bool sidecarConclusivelyDisposed = false;
        bool repositoryEvidenceValidated = false;
        bool repositoryDeleted = false;
        try {
            TestDirectorySafety.CreateDirectoryNew(repositoryPath);
            repositoryCreated = true;
            if (!OperatingSystem.IsWindows()) {
                File.SetUnixFileMode(
                    repositoryPath,
                    UnixFileMode.UserRead
                        | UnixFileMode.UserWrite
                        | UnixFileMode.UserExecute
                );
            }
            _ = await RunGitAsync(repositoryPath, ["init", "--quiet"]);

            GalateaDelegateRouteConfig route = source.CodexRoute with {
                Cwd = repositoryPath,
                Mode = GalateaDelegateMode.Research,
                LocalCommandNetwork = false,
                Tools = new GalateaDelegateToolConfig(
                    GalateaDelegateWebSearchMode.Disabled,
                    ImageGeneration: false,
                    ViewImage: false
                )
            };
            var isolated = new GalateaDelegateConfig(
                source.Sidecar,
                [repositoryPath],
                [route]
            );
            client = new GalateaCodexDurableSidecarClient(isolated);
            using var deadline = new CancellationTokenSource(
                CanaryDeadline
            );
            CancellationToken ct = deadline.Token;

            const string bindingOperationId =
                "galatea-live-canary-binding-v2";
            GalateaDelegateBindingEstablished binding =
                await client.EnsureBindingAsync(
                    new(bindingOperationId),
                    ct
                );
            RequireExact(
                bindingOperationId,
                binding.BindingOperationId,
                "binding operation"
            );
            RequireIdentifier(binding.ThreadId, "thread");

            string token = "GALATEA_V2_CANARY_"
                + Guid.NewGuid().ToString("N");
            const string dispatchId =
                "galatea-live-canary-dispatch-v2";
            string task = "Return exactly the requested canary token as "
                + "your entire final answer. Do not create, edit, delete, "
                + "or rename any file in the repository. Canary token: "
                + token;
            var request = new GalateaStartDelegateTurnRequest(
                dispatchId,
                binding.ThreadId,
                task
            );
            GalateaDelegateDispatchInspection beforeStart =
                await client.InspectDispatchAsync(
                    new(
                        dispatchId,
                        binding.ThreadId,
                        task
                    ),
                    ct
                );
            Assert.IsType<
                GalateaDelegateDispatchInspection.NotFound>(beforeStart);
            RequireExact(
                dispatchId,
                beforeStart.DispatchId,
                "dispatch"
            );
            RequireExact(
                binding.ThreadId,
                beforeStart.ThreadId,
                "thread"
            );
            GalateaDelegateTurnAccepted accepted =
                await client.StartTurnAsync(request, ct);
            RequireExact(dispatchId, accepted.DispatchId, "dispatch");
            RequireExact(binding.ThreadId, accepted.ThreadId, "thread");
            RequireIdentifier(accepted.TurnId, "turn");

            GalateaDurableDelegateTransportException replay =
                await Assert.ThrowsAsync<
                    GalateaDurableDelegateTransportException>(() =>
                    client.StartTurnAsync(request, ct));
            Assert.Equal("DUPLICATE_DISPATCH_ID", replay.Code);
            Assert.Equal(
                GalateaDurableDelegateFailurePolicy.DeterministicConflict,
                replay.FailurePolicy
            );

            GalateaDelegateDispatchInspection.Completed completed =
                await InspectUntilCompletedAsync(
                    client,
                    request,
                    accepted.TurnId,
                    ct
                );
            RequireExact(dispatchId, completed.DispatchId, "dispatch");
            RequireExact(binding.ThreadId, completed.ThreadId, "thread");
            RequireExact(accepted.TurnId, completed.TurnId, "turn");
            if (!string.Equals(
                    completed.Final.Trim(),
                    token,
                    StringComparison.Ordinal)) {
                throw new InvalidDataException(
                    "The real Codex final did not equal the canary token."
                );
            }
        }
        catch (Exception exception) {
            primaryFailure = exception;
        }
        finally {
            if (client is not null) {
                try {
                    await client.DisposeAsync();
                    sidecarConclusivelyDisposed = true;
                }
                catch (Exception exception) {
                    (cleanupFailures ??= []).Add(exception);
                }
            }
            else {
                sidecarConclusivelyDisposed = true;
            }

            if (repositoryCreated && sidecarConclusivelyDisposed) {
                try {
                    string status = await RunGitAsync(
                        repositoryPath,
                        ["status", "--porcelain"]
                    );
                    if (status.Length != 0) {
                        throw new InvalidDataException(
                            "The real Codex canary repository is not clean."
                        );
                    }
                    string[] entries = Directory
                        .EnumerateFileSystemEntries(repositoryPath)
                        .Select(static path => Path.GetFileName(path)
                            ?? throw new InvalidDataException(
                                "The live canary repository entry has no file name."
                            ))
                        .Order(StringComparer.Ordinal)
                        .ToArray();
                    if (!entries.SequenceEqual(
                            [".git"],
                            StringComparer.Ordinal)) {
                        throw new InvalidDataException(
                            "The real Codex canary repository has unexpected top-level entries."
                        );
                    }
                    repositoryEvidenceValidated = true;
                }
                catch (Exception exception) {
                    (cleanupFailures ??= []).Add(exception);
                }
                if (repositoryEvidenceValidated) {
                    try {
                        TestDirectorySafety.DeleteOwnedTreeNoFollow(
                            repositoryPath
                        );
                        repositoryDeleted = true;
                    }
                    catch (Exception exception) {
                        (cleanupFailures ??= []).Add(exception);
                    }
                }
            }
            if (repositoryCreated && !repositoryDeleted) {
                (cleanupFailures ??= []).Add(
                    new InvalidOperationException(
                        "The live canary repository was retained for "
                        + "diagnosis at "
                        + BoundedDiagnosticPath(repositoryPath)
                        + "."
                    )
                );
            }
        }

        ThrowCombined(primaryFailure, cleanupFailures);
    }

    private static async Task<
        GalateaDelegateDispatchInspection.Completed>
        InspectUntilCompletedAsync(
        GalateaCodexDurableSidecarClient client,
        GalateaStartDelegateTurnRequest request,
        string expectedTurnId,
        CancellationToken ct
    ) {
        var inspectionRequest = new GalateaInspectDelegateDispatchRequest(
            request.DispatchId,
            request.ThreadId,
            request.Task
        );
        try {
            while (true) {
                ct.ThrowIfCancellationRequested();
                GalateaDelegateDispatchInspection inspection =
                    await client.InspectDispatchAsync(
                        inspectionRequest,
                        ct
                    );
                RequireExact(
                    request.DispatchId,
                    inspection.DispatchId,
                    "dispatch"
                );
                RequireExact(
                    request.ThreadId,
                    inspection.ThreadId,
                    "thread"
                );
                switch (inspection) {
                    case GalateaDelegateDispatchInspection.Completed value:
                        RequireExact(expectedTurnId, value.TurnId, "turn");
                        return value;
                    case GalateaDelegateDispatchInspection.Running running:
                        RequireExact(expectedTurnId, running.TurnId, "turn");
                        break;
                    case GalateaDelegateDispatchInspection.NotFound:
                        break;
                    case GalateaDelegateDispatchInspection.Failed failed:
                        throw new InvalidOperationException(
                            "The real Codex canary reached a failed terminal "
                            + "state: " + failed.Code
                        );
                    case GalateaDelegateDispatchInspection.Ambiguous ambiguous:
                        throw new InvalidOperationException(
                            "The real Codex canary inspection was ambiguous: "
                            + ambiguous.Code
                        );
                    default:
                        throw new InvalidDataException(
                            "The real Codex canary returned an unknown inspection outcome."
                        );
                }
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw new TimeoutException(
                "The real Codex V2 canary exceeded its bounded deadline."
            );
        }
    }

    private static string CreateIsolatedRepositoryPath() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-galatea-codex-v2-live-"
                + Guid.NewGuid().ToString("N")
        );
        TestDirectorySafety.EnsureExistingPathChainHasNoReparsePoint(path);
        if (Directory.Exists(path) || File.Exists(path)) {
            throw new IOException(
                "The live canary repository candidate already exists."
            );
        }
        return path;
    }

    private static string BoundedDiagnosticPath(string path) {
        const int maximumPathCharacters = 1024;
        return path.Length <= maximumPathCharacters
            ? path
            : path[..maximumPathCharacters];
    }

    private static async Task<string> RunGitAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments
    ) {
        var startInfo = new ProcessStartInfo("git") {
            WorkingDirectory = repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string key in startInfo.Environment.Keys
                     .Where(static key => key.StartsWith(
                         "GIT_",
                         StringComparison.Ordinal
                     ))
                     .ToArray()) {
            _ = startInfo.Environment.Remove(key);
        }
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.fsmonitor=false");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.hooksPath=/dev/null");
        foreach (string argument in arguments) {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) {
            throw new InvalidOperationException(
                "The live canary Git process did not start."
            );
        }
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(GitDeadline);
        try {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) {
            try {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            using var reap = new CancellationTokenSource(
                TimeSpan.FromSeconds(5)
            );
            try {
                await process.WaitForExitAsync(reap.Token);
            }
            catch (OperationCanceledException) {
                throw new TimeoutException(
                    "The live canary Git process could not be reaped."
                );
            }
            throw new TimeoutException(
                "The live canary Git command exceeded its bounded deadline."
            );
        }
        string output = await stdout;
        string error = await stderr;
        const int maximumOutputChars = 64 * 1024;
        if (output.Length > maximumOutputChars
            || error.Length > maximumOutputChars) {
            throw new InvalidDataException(
                "The live canary Git command exceeded its output bound."
            );
        }
        if (process.ExitCode != 0) {
            throw new InvalidOperationException(
                "The live canary Git command failed with exit code "
                + process.ExitCode + "."
            );
        }
        return output;
    }

    private static void RequireIdentifier(string value, string scope) {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new InvalidDataException(
                "The real Codex canary returned a blank " + scope + "."
            );
        }
    }

    private static void RequireExact(
        string expected,
        string actual,
        string scope
    ) {
        if (!string.Equals(expected, actual, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "The real Codex canary returned a mismatched " + scope + "."
            );
        }
    }

    private static void ThrowCombined(
        Exception? primary,
        IReadOnlyList<Exception>? cleanup
    ) {
        if (primary is null && cleanup is null) {
            return;
        }
        if (primary is not null && cleanup is null) {
            ExceptionDispatchInfo.Capture(primary).Throw();
        }
        if (primary is null && cleanup is { Count: 1 }) {
            ExceptionDispatchInfo.Capture(cleanup[0]).Throw();
        }
        var failures = new List<Exception>();
        if (primary is not null) {
            failures.Add(primary);
        }
        failures.AddRange(cleanup ?? []);
        throw new AggregateException(
            "The real Codex canary and/or cleanup failed.",
            failures
        );
    }
}
