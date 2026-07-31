using System.Net;
using System.Net.Http.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaHostSmokeTests {
    [Fact]
    public async Task ActualProgram_UsesAuthenticationAndInjectedServices() {
        var completionFactory = new TrackingCompletionClientFactory();
        var normalizer = new TrackingNormalizer();
        await using var host = GalateaTestHost.Create(
            completionFactory,
            normalizer
        );
        using HttpClient client = host.CreateClient();

        using HttpResponseMessage anonymous = await client.GetAsync(
            "/api/me"
        );
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using HttpResponseMessage login = await GalateaTestHost.LoginAsync(
            client
        );
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/", login.Headers.Location?.OriginalString);

        GalateaMeDto? me = await client.GetFromJsonAsync<GalateaMeDto>(
            "/api/me"
        );
        Assert.NotNull(me);
        Assert.Equal("alice", me!.UserId);

        Assert.Same(
            completionFactory,
            host.Factory.Services
                .GetRequiredService<ICompletionClientFactory>()
        );
        Assert.Same(
            normalizer,
            host.Factory.Services
                .GetRequiredService<IGalateaUserMessageNormalizer>()
        );

        RecentTurnsResponseDto? recent = await client
            .GetFromJsonAsync<RecentTurnsResponseDto>("/api/recent-turns");
        Assert.NotNull(recent);
        Assert.Empty(recent!.Turns);
        Assert.Equal(0, completionFactory.CreateCallCount);
        Assert.Equal(0, completionFactory.Client.DispatchCallCount);
        Assert.Equal(0, normalizer.NormalizeCallCount);
    }

    private sealed class TrackingCompletionClientFactory
        : ICompletionClientFactory {
        private int _createCallCount;

        public TrackingCompletionClient Client { get; } = new();

        public int CreateCallCount => Volatile.Read(
            ref _createCallCount
        );

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            ArgumentNullException.ThrowIfNull(connection);
            Interlocked.Increment(ref _createCallCount);
            return Client;
        }
    }

    private sealed class TrackingCompletionClient : ICompletionClient {
        private int _dispatchCallCount;

        public string Name => "galatea-test";

        public string ApiSpecId => "openai-chat-v1";

        public int DispatchCallCount => Volatile.Read(
            ref _dispatchCallCount
        );

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = request;
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _dispatchCallCount);
            throw new InvalidOperationException(
                "Smoke test must not dispatch a completion request."
            );
        }
    }

    private sealed class TrackingNormalizer
        : IGalateaUserMessageNormalizer {
        private int _normalizeCallCount;

        public int NormalizeCallCount => Volatile.Read(
            ref _normalizeCallCount
        );

        public bool ShouldNormalize(string userMessage) {
            _ = userMessage;
            return true;
        }

        public ValueTask<string> NormalizeAsync(
            string userMessage,
            CancellationToken ct
        ) {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _normalizeCallCount);
            return ValueTask.FromResult(userMessage);
        }
    }
}
