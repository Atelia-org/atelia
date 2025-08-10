using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using System.Collections.Generic;

// NexusZero: Minimal OpenRouter chat completion sample using .NET 9
// - Reads API key from environment variable: OPENROUTER_API_KEY
// - Uses model IDs referenced in example/ref_code/example_openrouter.py
namespace NexusZero;

static class Roles
{
    public static readonly string
        SYSTEM = "system",
        MODEL = "model",
        ASSUSTANT = "assistant",
        USER = "user";
}

record Message(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content
);

class LlmContext
{
    public string? SystemInstruction{ get; set; }
    public List<Message> Messages{ get; init; }

    public LlmContext()
    {
        Messages = new List<Message>();
    }
    public void AddUser(string content)
    {
        Messages.Add(new Message(Roles.USER, content));
    }
    public void AddAssistant(string content)
    {
        Messages.Add(new Message(Roles.ASSUSTANT, content));
    }
}

interface IChatClient
{
    Task<string?> Chat(LlmContext context);
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        IChatClient client = new OpenRouterClient();
        var llmContext = new LlmContext();
        llmContext.SystemInstruction = "You are a helpful assistant.";
        llmContext.AddUser("Hi from C#. Please reply with a short greeting and your model name if available.");

        var content = await client.Chat(llmContext);

        if (content is null)
        {
            Console.WriteLine("[Fail] 未获得有效响应。");
            return 1;
        }

        Console.WriteLine("=== Assistant ===");
        Console.WriteLine(content);
        Console.WriteLine("=================");
        return 0;
    }
}
