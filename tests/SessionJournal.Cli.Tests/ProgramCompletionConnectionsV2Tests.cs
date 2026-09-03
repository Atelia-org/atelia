using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.Cli;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class ProgramCompletionConnectionsV2Tests : IDisposable {
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "atelia-cli-connections-v2-tests",
        Guid.NewGuid().ToString("N")
    );

    [Fact]
    public void LlmSmokeRejectsNoVersionBeforeProviderOrCallLogCreation() {
        Directory.CreateDirectory(_root);
        string connections = Path.Combine(_root, "connections.json");
        File.WriteAllText(connections, """
            {"connections":[{"id":"main","kind":"test","modelId":"model","completionSurfaceId":"test-v1","baseAddress":"endpoint"}],"defaultConnectionId":"main"}
            """);
        string callLogDirectory = Path.Combine(_root, "call-logs");
        var factory = new TrackingFactory();

        int exitCode = Program.MainCore(
            [
                "llm-smoke",
                "--connections", connections,
                "--call-log-dir", callLogDirectory
            ],
            factory
        );

        Assert.Equal(1, exitCode);
        Assert.Equal(0, factory.CreateCallCount);
        Assert.False(Directory.Exists(callLogDirectory));
    }

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class TrackingFactory : ICompletionClientFactory {
        internal int CreateCallCount { get; private set; }

        public ICompletionClient Create(CompletionConnectionConfig connection) {
            CreateCallCount++;
            throw new InvalidOperationException("must remain provider-free");
        }
    }
}
