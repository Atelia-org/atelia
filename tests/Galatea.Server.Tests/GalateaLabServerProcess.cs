using System.Diagnostics;

namespace Atelia.Galatea.Server.Tests;

/// <summary>
/// Runs the unmodified production entry point in a separate process. The child
/// receives no inherited provider credentials, proxies, or ASP.NET config.
/// Output is drained, but only lifecycle metadata is retained in diagnostics.
/// </summary>
internal sealed class GalateaLabServerProcess : IAsyncDisposable {
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly Process _process;
    private readonly TaskCompletionSource<Uri> _listening = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _stdout;
    private readonly Task _stderr;
    private int _outputLines;
    private int _errorLines;

    private GalateaLabServerProcess(Process process) {
        _process = process;
        _stdout = DrainAsync(process.StandardOutput, isError: false);
        _stderr = DrainAsync(process.StandardError, isError: true);
    }

    internal Uri BaseAddress { get; private set; } = null!;

    internal static async Task<GalateaLabServerProcess> StartAsync(
        string rootDirectory,
        string configPath
    ) {
        string temporary = Path.Combine(rootDirectory, "process-temp");
        string keys = Path.Combine(rootDirectory, "data-protection");
        Directory.CreateDirectory(temporary);
        Directory.CreateDirectory(keys);
        var start = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") {
            WorkingDirectory = rootDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment.Clear();
        foreach (string name in new[] { "PATH", "DOTNET_ROOT", "SystemRoot" }) {
            if (Environment.GetEnvironmentVariable(name) is { } value) {
                start.Environment[name] = value;
            }
        }
        foreach (string name in new[] { "TEMP", "TMP", "TMPDIR" }) {
            start.Environment[name] = temporary;
        }
        start.Environment["DOTNET_CLI_HOME"] = temporary;
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["ATELIA_DEBUG_FILE_LEVEL"] = "Error";
        start.Environment["ATELIA_DEBUG_CONSOLE_LEVEL"] = "Error";
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        start.Environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information";
        foreach (string argument in new[] {
                     typeof(Program).Assembly.Location,
                     "--contentRoot", rootDirectory,
                     "--Galatea:ConfigPath", configPath,
                     "--Galatea:DataProtectionKeysDirectory", keys,
                     "--urls", "http://127.0.0.1:0"
                 }) {
            start.ArgumentList.Add(argument);
        }
        var child = new GalateaLabServerProcess(Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the lab server."));
        try {
            Task exited = child._process.WaitForExitAsync();
            Task winner = await Task.WhenAny(child._listening.Task, exited)
                .WaitAsync(Deadline);
            if (winner != child._listening.Task) {
                await Task.WhenAll(child._stdout, child._stderr);
                throw new InvalidOperationException(child.Describe("exited before listening"));
            }
            child.BaseAddress = await child._listening.Task;
            return child;
        }
        catch {
            await child.DisposeAsync();
            throw;
        }
    }

    internal HttpClient CreateClient() => new(new HttpClientHandler {
        AllowAutoRedirect = false,
        UseProxy = false
    }) {
        BaseAddress = BaseAddress,
        Timeout = Deadline
    };

    /// <summary>Hard termination deliberately does not run host shutdown or Dispose.</summary>
    internal async Task KillAsync() {
        if (!_process.HasExited) {
            _process.Kill(entireProcessTree: true);
        }
        await _process.WaitForExitAsync().WaitAsync(Deadline);
        await Task.WhenAll(_stdout, _stderr).WaitAsync(Deadline);
    }

    public async ValueTask DisposeAsync() {
        try {
            await KillAsync();
        }
        finally {
            _process.Dispose();
        }
    }

    private async Task DrainAsync(StreamReader reader, bool isError) {
        while (await reader.ReadLineAsync() is { } line) {
            if (isError) { Interlocked.Increment(ref _errorLines); }
            else { Interlocked.Increment(ref _outputLines); }
            const string marker = "Now listening on: ";
            int index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0) { continue; }
            if (Uri.TryCreate(line[(index + marker.Length)..].Trim(),
                    UriKind.Absolute, out Uri? address)
                && address.Scheme == Uri.UriSchemeHttp
                && address.Host == "127.0.0.1"
                && address.Port > 0) {
                _listening.TrySetResult(address);
            }
        }
    }

    private string Describe(string reason) =>
        $"Lab server {reason}; exitCode={_process.ExitCode}; "
        + $"stdoutLines={_outputLines}; stderrLines={_errorLines}. "
        + "Raw process output is intentionally not included.";
}
