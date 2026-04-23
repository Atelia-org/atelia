using System;
using System.Net.Http;
using Atelia.Completion.Anthropic;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class AnthropicClientTests {
    [Fact]
    public void Constructor_DoesNotOverwriteExternalBaseAddressWhenNoneProvided() {
        using var handler = new EmptyHttpMessageHandler();
        var preconfigured = new Uri("http://localhost:9000/");
        using var httpClient = new HttpClient(handler) {
            BaseAddress = preconfigured
        };

        var client = new AnthropicClient(apiKey: null, httpClient: httpClient, baseAddress: null);

        Assert.NotNull(client);
        Assert.Equal(preconfigured, httpClient.BaseAddress);
    }

    [Fact]
    public void Constructor_ExplicitBaseAddressOverridesExternalHttpClientBaseAddress() {
        using var handler = new EmptyHttpMessageHandler();
        using var httpClient = new HttpClient(handler) {
            BaseAddress = new Uri("http://localhost:9000/")
        };
        var explicitAddress = new Uri("http://localhost:7777/");

        var client = new AnthropicClient(apiKey: null, httpClient: httpClient, baseAddress: explicitAddress);

        Assert.NotNull(client);
        Assert.Equal(explicitAddress, httpClient.BaseAddress);
    }

    private sealed class EmptyHttpMessageHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            throw new NotSupportedException();
        }
    }
}
