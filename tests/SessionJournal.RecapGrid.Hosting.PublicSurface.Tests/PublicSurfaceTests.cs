using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.RecapGrid.Hosting;
using Atelia.SessionJournal.RecapGrid.Runtime;
using System.Text;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Hosting.PublicSurface.Tests;

public sealed class PublicSurfaceTests {
    [Fact]
    public async Task ExternalCompositionCanCreateAndDisposeLazyHost() {
        var key = new RecapCompletionRouteKey(
            new FamilyDefinitionDigest(new string('a', 64)),
            RecapCompletionProtocolV1.RuntimeProtocolId,
            null
        );
        RecapGridRouteManifest manifest = RecapGridRouteManifest.Create([
            new RecapGridRouteManifestEntry(
                key,
                "main",
                1,
                TimeSpan.FromSeconds(30),
                null
            )
        ]);
        var factory = new ThrowingFactory();

        CompletionConnectionsFileConfig connections =
            RecapGridCompletionConnectionsManifest.Decode(
                Encoding.UTF8.GetBytes("""
                    {"connections":[{"id":"main","kind":"test","modelId":"model","completionSurfaceId":"test-v1","baseAddress":"https://example.invalid/"}],"defaultConnectionId":"main"}
                    """)
            );
        await using RecapGridRuntimeHost host = RecapGridRuntimeHost.Create(
            manifest,
            connections,
            factory
        );

        Assert.NotNull(host.Executor);
        Assert.Equal(0, factory.CallCount);
        Assert.Empty(host.Telemetry.ReadSnapshot().Events);
        Assert.False(host.Telemetry.IsMaterialized);
        await host.DisposeAsync();
    }

    private sealed class ThrowingFactory : ICompletionClientFactory {
        public int CallCount { get; private set; }
        public ICompletionClient Create(CompletionConnectionConfig connection) {
            CallCount++;
            throw new InvalidOperationException("must remain lazy");
        }
    }
}
