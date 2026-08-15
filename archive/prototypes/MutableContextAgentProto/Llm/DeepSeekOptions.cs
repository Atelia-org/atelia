namespace Atelia.MutableContextAgentProto.Llm;

public sealed record DeepSeekOptions {
    public const string DefaultModel = "deepseek-v4-pro";

    public required Uri BaseUrl { get; init; }

    public required string ApiKey { get; init; }

    public string Model { get; init; } = DefaultModel;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    public Uri ChatCompletionsEndpoint {
        get {
            var text = BaseUrl.ToString();
            if (text.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) { return BaseUrl; }

            return new Uri(text.TrimEnd('/') + "/chat/completions");
        }
    }

    public static DeepSeekOptions FromEnvironment() {
        var baseUrl = Environment.GetEnvironmentVariable("DEEPSEEK_BASE_URL");
        var apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");

        if (string.IsNullOrWhiteSpace(baseUrl)) { throw new InvalidOperationException("Missing DEEPSEEK_BASE_URL environment variable."); }

        if (string.IsNullOrWhiteSpace(apiKey)) { throw new InvalidOperationException("Missing DEEPSEEK_API_KEY environment variable."); }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedBaseUrl)) { throw new InvalidOperationException($"DEEPSEEK_BASE_URL is not a valid absolute URL: {baseUrl}"); }

        return new DeepSeekOptions {
            BaseUrl = parsedBaseUrl,
            ApiKey = apiKey,
        };
    }
}
