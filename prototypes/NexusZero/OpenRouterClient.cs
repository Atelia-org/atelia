using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;

// NexusZero: Minimal OpenRouter chat completion sample using .NET 9
// - Reads API key from environment variable: OPENROUTER_API_KEY
// - Uses model IDs referenced in example/ref_code/example_openrouter.py
namespace NexusZero;

static class Models {
    // 行业顶尖参考 / 这些常量与 Python 示例中保持一致命名和值
    public const string LLM_CLAUDE4_SONNET = "anthropic/claude-sonnet-4"; // 默认使用此模型
    public const string LLM_GEMINI_2_5_PRO = "google/gemini-2.5-pro";
    public const string LLM_GPT_5_beta = "openrouter/horizon-beta";

    public const string LLM_QWEN3_32B = "qwen/qwen3-32b";
    public const string LLM_QWEN3_30BA3B = "qwen/qwen3-30b-a3b-instruct-2507";
    public const string LLM_GLM_4_5_AIR = "z-ai/glm-4.5-air:free";

    public const string LLM_GLM_4_5 = "z-ai/glm-4.5";
    public const string LLM_QWEN3_CODER = "qwen/qwen3-coder:free";
    public const string LLM_DEEPSEEK_R1 = "deepseek/deepseek-r1-0528:free";
    public const string LLM_DEEPSEEK_V3 = "deepseek/deepseek-chat-v3-0324:free";
}

record ChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] List<Message> Messages
// You can extend here: temperature, max_tokens, reasoning, etc.
);

record ChatChoiceMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content
);

record ChatChoice(
    [property: JsonPropertyName("message")] ChatChoiceMessage Message
);

record ChatResponse(
    [property: JsonPropertyName("choices")] List<ChatChoice> Choices
);

class OpenRouterClient : IChatClient {
    private string _apiKey;
    private readonly HttpClient _http;
    private string _modelId;

    public OpenRouterClient() {
        this._apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")!;
        this._http = new HttpClient();
        this._modelId = Models.LLM_CLAUDE4_SONNET;
    }

    public async Task<string?> Call(ILlmContext context) {

        var messages = new List<Message>();
        string? temInstruction = context.GetSystemInstruction();
        if (temInstruction != null) {
            messages.Add(new Message(Roles.SYSTEM, temInstruction));
        }
        messages.AddRange(context.GetMessages());

        var req = new ChatRequest(_modelId, messages);
        var json = JsonSerializer.Serialize(req, new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        });

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions") {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        // 可选但推荐的 headers（用于来源标识）
        // httpReq.Headers.Add("HTTP-Referer", "https://your-app.example");
        // httpReq.Headers.Add("X-Title", "NexusZero");

        var resp = await _http.SendAsync(httpReq);
        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode) {
            Console.WriteLine($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}\n{body}");
            return null;
        }

        try {
            var parsed = JsonSerializer.Deserialize<ChatResponse>(body);
            var content = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
            if (content != null) {
                context.AddAssistant(content);
            }
            return content;
        } catch (Exception ex) {
            Console.WriteLine("Failed to parse response: " + ex.Message);
            Console.WriteLine(body);
            return null;
        }
    }
}