using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.State.History;

namespace Atelia.LiveContextProto.Provider.Anthropic;

/// <summary>
/// Anthropic Messages API 客户端实现。
/// 规范：https://docs.anthropic.com/claude/reference/messages_post
/// </summary>
internal sealed class AnthropicProviderClient : IProviderClient {
    private const string DebugCategory = "Provider";
    private const string DefaultApiVersion = "2023-06-01";

    private static readonly JsonSerializerOptions SerializerOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _apiVersion;

    public AnthropicProviderClient(string apiKey, HttpClient? httpClient = null, string? apiVersion = null) {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _httpClient = httpClient ?? new HttpClient { BaseAddress = new Uri("https://api.anthropic.com/") };
        _apiVersion = apiVersion ?? DefaultApiVersion;

        DebugUtil.Print(DebugCategory, $"[Anthropic] Client initialized version={_apiVersion}");
    }

    public async IAsyncEnumerable<ModelOutputDelta> CallModelAsync(
        ProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken
    ) {
        DebugUtil.Print(DebugCategory, $"[Anthropic] Starting call model={request.Invocation.Model}");

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request);
        var httpRequest = CreateHttpRequest(apiRequest);

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var parser = new AnthropicStreamParser();
        string? line;

        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null) {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line)) { continue; }
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) { continue; }

            var json = line["data: ".Length..];
            if (json == "[DONE]") { break; }

            var deltas = parser.ParseEvent(json);
            foreach (var delta in deltas) {
                yield return delta;
            }
        }

        // 输出最终统计
        if (parser.GetFinalUsage() is { } usage) {
            yield return ModelOutputDelta.Usage(usage);
        }

        DebugUtil.Print(DebugCategory, "[Anthropic] Stream completed");
    }

    private HttpRequestMessage CreateHttpRequest(AnthropicApiRequest apiRequest) {
        var json = JsonSerializer.Serialize(apiRequest, SerializerOptions);
        DebugUtil.Print(DebugCategory, $"[Anthropic] Request payload length={json.Length}");

        var request = new HttpRequestMessage(HttpMethod.Post, "v1/messages") {
            Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
        };

        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Add("anthropic-version", _apiVersion);

        return request;
    }
}
