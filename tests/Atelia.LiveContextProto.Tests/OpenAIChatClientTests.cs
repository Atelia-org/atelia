using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class OpenAIChatClientTests {
    [Fact]
    public async Task StreamCompletionAsync_ParsesContentWithoutRequestingUsage() {
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

        var aggregated = await client.StreamCompletionAsync(request, null, CancellationToken.None);

        var requestBody = Assert.Single(handler.RequestBodies);
        Assert.DoesNotContain("\"stream_options\"", requestBody, StringComparison.Ordinal);
        Assert.Equal("hello", aggregated.Message.GetFlattenedText());
    }

    [Fact]
    public async Task StreamCompletionAsync_IncludesConfiguredExtraBodyFieldsAtRequestRoot() {
        var handler = new SequenceHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(
                    """
                    data: {"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}],"usage":null}

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

        var client = new OpenAIChatClient(
            apiKey: null,
            httpClient: httpClient,
            dialect: OpenAIChatDialects.SgLangCompatible,
            options: OpenAIChatClientOptions.QwenThinkingDisabled()
        );

        await client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None);

        var requestBody = Assert.Single(handler.RequestBodies);
        using var document = JsonDocument.Parse(requestBody);
        var root = document.RootElement;
        Assert.False(root.TryGetProperty("extra_body", out _));
        Assert.True(root.TryGetProperty("chat_template_kwargs", out var kwargs));
        Assert.False(kwargs.GetProperty("enable_thinking").GetBoolean());
    }

    [Fact]
    public async Task StreamCompletionAsync_ThrowsWhenExtraBodyCollidesWithReservedFields() {
        using var handler = new SequenceHttpMessageHandler(
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

        var client = new OpenAIChatClient(
            apiKey: null,
            httpClient: httpClient,
            dialect: OpenAIChatDialects.Strict,
            options: new OpenAIChatClientOptions {
                ExtraBody = new JsonObject {
                    ["model"] = "should-not-override"
                }
            }
        );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None)
        );

        Assert.Contains("collides with a reserved request property", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.RequestBodies);
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

    [Fact]
    public async Task StreamCompletionAsync_EarlyStop_DoesNotFlushIncompleteToolCalls() {
        var handler = new SequenceHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(
                    """
                    data: {"choices":[{"index":0,"delta":{"tool_calls":[{"id":"call_123","index":0,"type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Par"}}]},"finish_reason":null}],"usage":null}

                    data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"type":"function","function":{"arguments":"is\"}"}}]},"finish_reason":"tool_calls"}],"usage":null}

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
        var observer = new CompletionStreamObserver { ShouldStop = true };

        var aggregated = await client.StreamCompletionAsync(CreateRequest(), observer, CancellationToken.None);

        Assert.DoesNotContain(aggregated.Message.Blocks, block => block.Kind == ActionBlockKind.ToolCall);
        var text = Assert.Single(aggregated.Message.Blocks);
        Assert.Equal(string.Empty, Assert.IsType<ActionBlock.Text>(text).Content);
    }

    private static CompletionRequest CreateRequest() {
        return new CompletionRequest(
            ModelId: "gpt-4.1",
            SystemPrompt: "system",
            Context: new[] { new ObservationMessage("hello") },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );
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
