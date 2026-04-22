using System.Collections.Generic;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class OpenAIChatClientTests {
    [Fact]
    public async Task StreamCompletionAsync_RequestsStreamUsageWhenEnabled() {
        var handler = new SequenceHttpMessageHandler(
            new HttpResponseMessage(
                HttpStatusCode.OK
            ) {
                Content = new StringContent(
                    """
                    data: {"choices":[{"index":0,"delta":{"content":"hello"},"finish_reason":null}],"usage":null}

                    data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":2}}

                    data: [DONE]

                    """,
                    Encoding.UTF8,
                    "text/event-stream"
                )
            }
        );

        using var httpClient = new HttpClient(handler) {
            BaseAddress = new Uri("http://localhost:8000/")
        };

        var client = new OpenAIChatClient(apiKey: null, httpClient: httpClient, dialect: OpenAIChatDialects.SgLangCompatible);
        var request = CreateRequest();

        var chunks = await CollectAsync(client.StreamCompletionAsync(request, CancellationToken.None));

        var requestBody = Assert.Single(handler.RequestBodies);
        Assert.Contains("\"stream_options\":{\"include_usage\":true}", requestBody, StringComparison.Ordinal);

        var usageChunk = Assert.Single(chunks, chunk => chunk.Kind == CompletionChunkKind.TokenUsage);
        Assert.Equal(5, usageChunk.TokenUsage!.PromptTokens);
        Assert.Equal(2, usageChunk.TokenUsage!.CompletionTokens);
    }

    [Fact]
    public async Task StreamCompletionAsync_RetriesWithoutStreamOptionsWhenProviderRejectsThem() {
        var handler = new SequenceHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) {
                Content = new StringContent("{\"error\":{\"message\":\"unknown field stream_options\"}}", Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(
                    """
                    data: [DONE]

                    """,
                    Encoding.UTF8,
                    "text/event-stream"
                )
            }
        );

        using var httpClient = new HttpClient(handler) {
            BaseAddress = new Uri("http://localhost:8000/")
        };

        var client = new OpenAIChatClient(apiKey: null, httpClient: httpClient, dialect: OpenAIChatDialects.SgLangCompatible);

        await CollectAsync(client.StreamCompletionAsync(CreateRequest(), CancellationToken.None));

        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Contains("\"stream_options\":{\"include_usage\":true}", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("\"stream_options\"", handler.RequestBodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamCompletionAsync_StrictDialectDoesNotRetryWhenStreamOptionsAreRejected() {
        var handler = new SequenceHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) {
                Content = new StringContent("{\"error\":{\"message\":\"unknown field stream_options\"}}", Encoding.UTF8, "application/json")
            }
        );

        using var httpClient = new HttpClient(handler) {
            BaseAddress = new Uri("http://localhost:8000/")
        };

        var client = new OpenAIChatClient(apiKey: null, httpClient: httpClient, dialect: OpenAIChatDialects.Strict);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            async () => await CollectAsync(client.StreamCompletionAsync(CreateRequest(), CancellationToken.None))
        );

        Assert.Single(handler.RequestBodies);
        Assert.Contains("status=400", exception.Message, StringComparison.Ordinal);
        Assert.Contains("stream_options", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_DoesNotOverwriteExternalBaseAddressWhenNoneProvided() {
        using var handler = new SequenceHttpMessageHandler();
        var preconfigured = new Uri("http://localhost:9000/");
        using var httpClient = new HttpClient(handler) {
            BaseAddress = preconfigured
        };

        var client = new OpenAIChatClient(apiKey: null, httpClient: httpClient, baseAddress: null, dialect: OpenAIChatDialects.Strict);

        Assert.NotNull(client);
        Assert.Equal(preconfigured, httpClient.BaseAddress);
    }

    [Fact]
    public void Constructor_ExplicitBaseAddressOverridesExternalHttpClientBaseAddress() {
        using var handler = new SequenceHttpMessageHandler();
        using var httpClient = new HttpClient(handler) {
            BaseAddress = new Uri("http://localhost:9000/")
        };
        var explicitAddress = new Uri("http://localhost:7777/");

        var client = new OpenAIChatClient(apiKey: null, httpClient: httpClient, baseAddress: explicitAddress, dialect: OpenAIChatDialects.Strict);

        Assert.NotNull(client);
        Assert.Equal(explicitAddress, httpClient.BaseAddress);
    }

    private static CompletionRequest CreateRequest() {
        return new CompletionRequest(
            ModelId: "gpt-4.1",
            SystemPrompt: "system",
            Context: new[] { new ObservationMessage("hello") },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );
    }

    private static async Task<List<CompletionChunk>> CollectAsync(IAsyncEnumerable<CompletionChunk> deltas) {
        var list = new List<CompletionChunk>();
        await foreach (var delta in deltas) {
            list.Add(delta);
        }

        return list;
    }

    private sealed class SequenceHttpMessageHandler : HttpMessageHandler {
        private readonly Queue<HttpResponseMessage> _responses;

        public SequenceHttpMessageHandler(params HttpResponseMessage[] responses) {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public List<string> RequestBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            if (request.Content is not null) {
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }
            else {
                RequestBodies.Add(string.Empty);
            }

            return _responses.Dequeue();
        }
    }
}
