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
            RecapRewriterProtocolV3.RuntimeProtocolId,
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
            CompletionConnectionConfigLoader.Decode(
                Encoding.UTF8.GetBytes("""
                    {"v":1,"connections":[{"id":"main","kind":"test","modelId":"model","completionSurfaceId":"test-v1","baseAddress":"https://example.invalid/"}],"defaultConnectionId":"main"}
                    """)
            );
        await using RecapGridRuntimeHost host = RecapGridRuntimeHost.Create(
            manifest,
            connections,
            factory
        );

        Assert.NotNull(host.Executor);
        Assert.Equal(0, factory.CallCount);
        Assert.Empty(host.ReadTelemetrySnapshot().Events);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task ExternalCompositionCanOwnAgentAndRecapInOneHost() {
        var key = new RecapCompletionRouteKey(
            new FamilyDefinitionDigest(new string('a', 64)),
            RecapRewriterProtocolV3.RuntimeProtocolId,
            null
        );
        CompletionConnectionsFileConfig connections =
            CompletionConnectionConfigLoader.Decode(
                Encoding.UTF8.GetBytes("""
                    {"v":1,"connections":[{"id":"main","kind":"test","modelId":"model","completionSurfaceId":"test-v1","baseAddress":"https://example.invalid/"}],"defaultConnectionId":"main"}
                    """));
        var factory = new BorrowedFactory();
        await using RecapGridCompletionHost host =
            RecapGridCompletionHost.Create(
                () => RecapGridRouteManifest.Create([
                    new RecapGridRouteManifestEntry(
                        key,
                        "main",
                        1,
                        TimeSpan.FromSeconds(30),
                        1024
                    )
                ]),
                connections,
                factory);

        RecapGridConfiguredRouteInspectionResult.Configured route =
            Assert.IsType<RecapGridConfiguredRouteInspectionResult.Configured>(
                host.InspectRouteExact(key)
            );
        Assert.Equal("main", route.ConnectionId);
        Assert.Equal("model", route.ModelId);
        Assert.Equal(0, factory.CreateCount);
        RecapGridAgentConnectionLookupResult.Found inspected = Assert.IsType<
            RecapGridAgentConnectionLookupResult.Found>(
            host.InspectAgentExact("main"));
        Assert.Equal("main", inspected.Connection.Id);
        Assert.Equal(0, factory.CreateCount);
        RecapGridAgentConnectionResult.Bound bound = Assert.IsType<
            RecapGridAgentConnectionResult.Bound>(
            host.BindAgentExact("main"));
        Assert.Equal("main", bound.Connection.Id);
        Assert.Equal(1, factory.CreateCount);
        Assert.NotNull(host.Executor);

        await host.DisposeAsync();
        Assert.Equal(1, factory.Client.DisposeCount);
    }

    [Fact]
    public void HostTelemetrySurfaceIsReadOnlyWhileStandaloneCollectorRemainsPublic() {
        Type runtimeHost = typeof(RecapGridRuntimeHost);
        Type completionHost = typeof(RecapGridCompletionHost);

        Assert.Null(runtimeHost.GetProperty("Telemetry"));
        Assert.Null(completionHost.GetProperty("Telemetry"));
        Assert.Equal(
            typeof(RecapCompletionTelemetrySnapshot),
            runtimeHost.GetMethod("ReadTelemetrySnapshot")?.ReturnType
        );
        Assert.Equal(
            typeof(RecapCompletionTelemetrySnapshot),
            completionHost.GetMethod("ReadTelemetrySnapshot")?.ReturnType
        );
        Assert.Null(typeof(BoundedRecapCompletionTelemetry).GetProperty(
            "IsMaterialized"
        ));

        IRecapCompletionTelemetry telemetry =
            new BoundedRecapCompletionTelemetry();
        Assert.Empty(Assert.IsType<BoundedRecapCompletionTelemetry>(telemetry)
            .ReadSnapshot().Events);
    }

    [Fact]
    public void HostingDoesNotExportAConnectionsByteReaderOrLimits() {
        Type[] exported = typeof(RecapGridRuntimeHost).Assembly
            .GetExportedTypes();

        Assert.DoesNotContain(
            exported,
            static type => type.Name
                is "RecapGridCompletionConnectionsManifest"
                    or "RecapGridCompletionConnectionsLimits"
        );
    }

    private sealed class ThrowingFactory : ICompletionClientFactory {
        public int CallCount { get; private set; }
        public ICompletionClient Create(CompletionConnectionConfig connection) {
            CallCount++;
            throw new InvalidOperationException("must remain lazy");
        }
    }

    private sealed class BorrowedFactory : ICompletionClientFactory {
        internal BorrowedClient Client { get; } = new();
        internal int CreateCount { get; private set; }

        public ICompletionClient Create(CompletionConnectionConfig connection) {
            CreateCount++;
            return Client;
        }
    }

    private sealed class BorrowedClient : ICompletionClient, IDisposable {
        internal int DisposeCount { get; private set; }
        public string Name => "public-surface-client";
        public string ApiSpecId => "test-v1";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("No dispatch expected.");

        public void Dispose() => DisposeCount++;
    }
}
