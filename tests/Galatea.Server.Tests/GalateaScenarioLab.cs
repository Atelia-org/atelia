using System.Text.Json;
using Atelia.Completion;
using Atelia.Diagnostics;
using Atelia.Testing;

namespace Atelia.Galatea.Server.Tests;

/// <summary>
/// A disposable synthetic instance, not a snapshot importer or a network sandbox.
/// Call CompleteAsync only after all scenario assertions pass. Otherwise disposal
/// retains the private durable root, and reports its path without copying payloads
/// into diagnostic output. Callers must supply a controlled completion factory.
/// </summary>
internal sealed class GalateaScenarioLab : IAsyncDisposable {
    private const string OwnershipFile = "scenario-owner.txt";
    private const string ReportFile = "scenario-result.json";
    private readonly string _ownershipToken = Guid.NewGuid().ToString("N");
    private readonly string _name;
    private readonly IGalateaUserMessageNormalizer _normalizer;
    private readonly Action<string> _reportArtifact;
    private GalateaTestHost _host;
    private bool _stopped;
    private bool _completed;
    private bool _disposed;

    private GalateaScenarioLab(string name, GalateaTestHost host,
        IGalateaUserMessageNormalizer normalizer, Action<string>? reportArtifact) {
        _name = name;
        _host = host;
        _normalizer = normalizer;
        _reportArtifact = reportArtifact ?? (path => DebugUtil.Warning(
            "Galatea.ScenarioLab", "Retained synthetic scenario: " + path));
        RootDirectory = host.RootDirectory;
        SessionDirectory = host.SessionDirectory;
        using var marker = new FileStream(Path.Combine(RootDirectory, OwnershipFile),
            FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(marker);
        writer.Write(_ownershipToken);
    }

    internal static GalateaScenarioLab Create(string name,
        ICompletionClientFactory completionClientFactory,
        IGalateaUserMessageNormalizer? normalizer = null,
        IReadOnlyList<CompletionConnectionConfig>? connections = null,
        TimeProvider? timeProvider = null,
        Action<string>? reportArtifact = null) {
        ArgumentNullException.ThrowIfNull(completionClientFactory);
        if (string.IsNullOrEmpty(name) || name.Length > 80
            || name.Any(static c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) {
            throw new ArgumentException("Scenario name must be a short ASCII identifier.", nameof(name));
        }
        // Reject environment credential/address sources before creating any files.
        if (connections?.Any(static c => !string.IsNullOrWhiteSpace(c.ApiKeyEnv)
            || !string.IsNullOrWhiteSpace(c.BaseAddressEnv)) == true) {
            throw new ArgumentException("Scenario connections require explicit synthetic sources.", nameof(connections));
        }
        normalizer ??= DisabledGalateaUserMessageNormalizer.Instance;
        GalateaTestHost host = GalateaTestHost.Create(completionClientFactory,
            normalizer, deleteFilesOnDispose: false, connections: connections,
            delegateTransport: new RejectingDelegateTransport(), timeProvider: timeProvider);
        return new GalateaScenarioLab(name, host, normalizer, reportArtifact);
    }

    internal GalateaTestHost Host => !_stopped && !_disposed && !_completed
        ? _host : throw new InvalidOperationException("The scenario host is stopped or completed.");
    internal string RootDirectory { get; }
    internal string SessionDirectory { get; }

    internal async Task StopAsync() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stopped) { return; }
        await _host.DisposeAsync().ConfigureAwait(false);
        _stopped = true;
    }

    internal Task ReopenAsync(ICompletionClientFactory nextFactory) {
        ArgumentNullException.ThrowIfNull(nextFactory);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_stopped || _completed) {
            throw new InvalidOperationException("Only a stopped unfinished scenario can reopen.");
        }
        ValidateOwnership();
        _host = _host.CreateRestarted(nextFactory, _normalizer,
            new RejectingDelegateTransport(), deleteFilesOnDispose: false);
        _stopped = false;
        return Task.CompletedTask;
    }

    internal async Task CompleteAsync() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await StopAsync().ConfigureAwait(false);
        ValidateOwnership();
        _completed = true;
    }

    public async ValueTask DisposeAsync() {
        if (_disposed) { return; }
        try {
            await StopAsync().ConfigureAwait(false);
            if (_completed) {
                ValidateOwnership();
                Directory.Delete(RootDirectory, recursive: true);
            }
        }
        finally {
            _disposed = true;
            if (Directory.Exists(RootDirectory)) {
                // Reporting must never hide the scenario's original assertion or
                // shutdown failure. Metadata deliberately excludes exceptions.
                try {
                    ValidateOwnership();
                    File.WriteAllText(Path.Combine(RootDirectory, ReportFile),
                        JsonSerializer.Serialize(new {
                            v = 1, scenario = _name, outcome = "retained", stopped = _stopped
                        }));
                }
                catch (InvalidDataException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                try { _reportArtifact(RootDirectory); }
                catch { /* A diagnostic sink cannot replace the primary failure. */ }
            }
        }
    }

    private void ValidateOwnership() {
        TestDirectorySafety.EnsureExistingPathChainHasNoReparsePoint(RootDirectory);
        var pending = new Stack<string>();
        pending.Push(RootDirectory);
        while (pending.TryPop(out string? directory)) {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory)) {
                FileAttributes attributes = File.GetAttributes(entry);
                TestDirectorySafety.RejectReparsePoint(entry, attributes);
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(entry); }
            }
        }
        string marker = Path.Combine(RootDirectory, OwnershipFile);
        if (new FileInfo(marker).Length != _ownershipToken.Length
            || File.ReadAllText(marker) != _ownershipToken) {
            throw new InvalidDataException("Scenario root ownership marker has changed; retaining files.");
        }
    }

    internal sealed class RejectingDelegateTransport : IGalateaDurableDelegateTransport {
        public Task<GalateaDelegateBindingEstablished> EnsureBindingAsync(
            GalateaEnsureDelegateBindingRequest request, CancellationToken ct) =>
            throw Unplanned();
        public Task<GalateaDelegateTurnAccepted> StartTurnAsync(
            GalateaStartDelegateTurnRequest request, CancellationToken ct) =>
            throw Unplanned();
        public Task<GalateaDelegateDispatchInspection> InspectDispatchAsync(
            GalateaInspectDelegateDispatchRequest request, CancellationToken ct) =>
            throw Unplanned();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private static InvalidOperationException Unplanned() => new(
            "Unplanned delegation is forbidden in the synthetic scenario lab.");
    }
}
