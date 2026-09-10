using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaScenarioLabTests {
    [Fact]
    public async Task CompletedScenarioCleansOnlyItsOwnedRoot() {
        var lab = GalateaScenarioLab.Create("success-cleanup", new RejectingFactory());
        string root = lab.RootDirectory;
        Assert.True(Directory.Exists(root));
        if (!OperatingSystem.IsWindows()) {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(root));
        }
        await lab.CompleteAsync();
        await lab.DisposeAsync();
        await lab.DisposeAsync();
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task UncompletedScenarioRetainsPrivateStateAndBoundedMetadata() {
        string? artifact = null;
        var lab = GalateaScenarioLab.Create("retained-failure", new RejectingFactory(),
            reportArtifact: path => artifact = path);
        string root = lab.RootDirectory;
        try {
            File.WriteAllText(Path.Combine(root, "synthetic-state.txt"), "synthetic-sensitive-canary");
            await lab.DisposeAsync();
            Assert.Equal(root, artifact);
            Assert.True(Directory.Exists(lab.SessionDirectory));
            string report = File.ReadAllText(Path.Combine(root, "scenario-result.json"));
            Assert.DoesNotContain("synthetic-sensitive-canary", report);
            Assert.True(report.Length < 256);
            using JsonDocument metadata = JsonDocument.Parse(report);
            Assert.Equal("retained", metadata.RootElement.GetProperty("outcome").GetString());
            Assert.True(metadata.RootElement.GetProperty("stopped").GetBoolean());
        }
        finally {
            await lab.DisposeAsync();
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    [Fact]
    public async Task ColdReopenPreservesWholeInstanceAndRejectsRunningReopen() {
        await using var lab = GalateaScenarioLab.Create("cold-reopen", new RejectingFactory());
        Assert.Throws<InvalidOperationException>(() => { _ = lab.ReopenAsync(new RejectingFactory()); });
        using (HttpClient client = lab.Host.CreateClient()) {
            using HttpResponseMessage login = await GalateaTestHost.LoginAsync(client);
            Assert.Equal(System.Net.HttpStatusCode.Redirect, login.StatusCode);
            GalateaHostService service = lab.Host.Factory.Services.GetRequiredService<GalateaHostService>();
            _ = await service.GetSessionAsync("alice", CancellationToken.None);
        }
        string memory = lab.Host.CharacterMemoryStateDirectory;
        string delegation = lab.Host.DelegationStateDirectory;
        // No Note/Memo binding is enabled in this fixture. Absence is itself
        // part of the durable seed; do not mistake a path setting for a store.
        Assert.False(Directory.Exists(memory));
        Assert.True(Directory.Exists(delegation));
        await lab.StopAsync();
        Assert.Throws<InvalidOperationException>(() => lab.Host);
        string[] before = Directory.GetFiles(lab.RootDirectory, "*", SearchOption.AllDirectories);
        Assert.DoesNotContain(before, path => path.StartsWith(memory, StringComparison.Ordinal));
        Assert.Contains(before, path => path.StartsWith(delegation, StringComparison.Ordinal));
        await lab.ReopenAsync(new RejectingFactory());
        using (HttpClient client = lab.Host.CreateClient()) {
            using HttpResponseMessage login = await GalateaTestHost.LoginAsync(client);
            Assert.Equal(System.Net.HttpStatusCode.Redirect, login.StatusCode);
            GalateaHostService service = lab.Host.Factory.Services.GetRequiredService<GalateaHostService>();
            _ = await service.GetSessionAsync("alice", CancellationToken.None);
        }
        Assert.Equal(memory, lab.Host.CharacterMemoryStateDirectory);
        Assert.False(Directory.Exists(memory));
        Assert.Equal(delegation, lab.Host.DelegationStateDirectory);
        await lab.CompleteAsync();
    }

    [Fact]
    public async Task ChangedOwnershipMarkerPreventsCleanup() {
        var lab = GalateaScenarioLab.Create("ownership-guard", new RejectingFactory(),
            reportArtifact: _ => { });
        string root = lab.RootDirectory;
        try {
            await lab.CompleteAsync();
            File.WriteAllText(Path.Combine(root, "scenario-owner.txt"), "changed");
            await Assert.ThrowsAsync<InvalidDataException>(() => lab.DisposeAsync().AsTask());
            Assert.True(Directory.Exists(root));
        }
        finally {
            await lab.DisposeAsync();
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    [Fact]
    public async Task UncompletedScenarioWithChangedMarkerRetainsWithoutMaskingFailure() {
        string? artifact = null;
        var lab = GalateaScenarioLab.Create("failed-ownership-guard", new RejectingFactory(),
            reportArtifact: path => artifact = path);
        string root = lab.RootDirectory;
        try {
            await lab.StopAsync();
            File.WriteAllText(Path.Combine(root, "scenario-owner.txt"), "changed");
            // In the uncompleted path, ownership validation is diagnostic-only.
            // It must not replace the original scenario assertion failure.
            await lab.DisposeAsync();
            Assert.Equal(root, artifact);
            Assert.True(Directory.Exists(root));
            Assert.False(File.Exists(Path.Combine(root, "scenario-result.json")));
        }
        finally {
            await lab.DisposeAsync();
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    [Fact]
    public async Task LinkedStatePreventsCleanupWithoutTouchingItsTarget() {
        if (OperatingSystem.IsWindows()) { return; }
        var lab = GalateaScenarioLab.Create("link-guard", new RejectingFactory(),
            reportArtifact: _ => { });
        string root = lab.RootDirectory;
        string outside = Directory.CreateTempSubdirectory("atelia-lab-link-target-").FullName;
        string link = Path.Combine(root, "linked-state");
        try {
            File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "untouched");
            Directory.CreateSymbolicLink(link, outside);
            await Assert.ThrowsAsync<InvalidDataException>(() => lab.CompleteAsync());
            await lab.DisposeAsync();
            Assert.True(Directory.Exists(root));
            Assert.Equal("untouched", File.ReadAllText(Path.Combine(outside, "sentinel.txt")));
        }
        finally {
            await lab.DisposeAsync();
            if (Directory.Exists(link)) { Directory.Delete(link); }
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task DelegateTransportRejectsEveryExternalOperation() {
        await using var transport = new GalateaScenarioLab.RejectingDelegateTransport();
        Assert.Throws<InvalidOperationException>(() => { _ = transport.EnsureBindingAsync(null!, default); });
        Assert.Throws<InvalidOperationException>(() => { _ = transport.StartTurnAsync(null!, default); });
        Assert.Throws<InvalidOperationException>(() => { _ = transport.InspectDispatchAsync(null!, default); });
    }

    [Fact]
    public void EnvironmentConnectionsAndUnboundedScenarioNamesAreRejected() {
        Assert.Throws<ArgumentException>(() => GalateaScenarioLab.Create("credentials", new RejectingFactory(),
            connections: [new CompletionConnectionConfig("test", "openai-chat", "model-a",
                "openai-chat/strict", BaseAddress: "http://127.0.0.1/", ApiKeyEnv: "REAL_API_KEY")]));
        Assert.Throws<ArgumentException>(() => GalateaScenarioLab.Create("not/a/path", new RejectingFactory()));
    }

    private sealed class RejectingFactory : ICompletionClientFactory {
        public ICompletionClient Create(CompletionConnectionConfig connection) =>
            throw new InvalidOperationException("This scenario must not request a Completion client.");
    }
}
