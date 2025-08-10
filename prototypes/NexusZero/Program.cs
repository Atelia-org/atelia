using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MemoTree.Services;

// NexusZero: Minimal OpenRouter chat completion sample using .NET 9
// - Reads API key from environment variable: OPENROUTER_API_KEY
// - Uses model IDs referenced in example/ref_code/example_openrouter.py
namespace NexusZero;

static class Roles {
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

interface ILlmContext {
    public string? GetSystemInstruction();

    public IEnumerable<Message> GetMessages();

    void AddAssistant(string content);
    void AddUser(string content);
}

class SimpleLlmContext : ILlmContext {
    public string? SystemInstruction { get; set; }

    List<Message> Messages { get; init; }

    public SimpleLlmContext() {
        Messages = new List<Message>();
    }

    #region ILlmContext
    public string? GetSystemInstruction() {
        return SystemInstruction;
    }

    public IEnumerable<Message> GetMessages() {
        return Messages;
    }
    public void AddUser(string content) {
        Messages.Add(new Message(Roles.USER, content));
    }
    public void AddAssistant(string content) {
        Messages.Add(new Message(Roles.ASSUSTANT, content));
    }
    #endregion
}

/// <summary>
/// 复合了MemoTree和RecentMessages的上下文。
/// 有待进一步设计和实现。
/// </summary>
class ComposedLlmContext : ILlmContext {
    #region ILlmContext
    public string? GetSystemInstruction() {
        throw new NotImplementedException();
    }
    public IEnumerable<Message> GetMessages() {
        throw new NotImplementedException();
    }
    public void AddUser(string content) {
        throw new NotImplementedException();
    }
    public void AddAssistant(string content) {
        throw new NotImplementedException();
    }
    #endregion
}

interface IChatClient {
    /// <summary>
    /// 使用context中的SystemInstruction和Messages进行一次LLM调用。如果LLM产生了非空回复则在context中添加一条Assistant消息。
    /// </summary>
    /// <param name="context"></param>
    /// <returns>LLM的返回结果。如果为null则表示调用失败了，此时不会在context中添加Assistant消息。</returns>
    Task<string?> Call(ILlmContext context);
}

public static class Program {
    public static async Task<int> Main(string[] args) {
        // 1) 演示：构建 DI 容器并解析 IMemoTreeService（教学用，写法尽量直白）
        // 假设当前工作目录在一个有效的 MemoTree 工作区下
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole());
        // 这里简单起见，直接把当前目录作为工作区根；真实应用可用 WorkspaceManager 自动探测
        var workspaceRoot = Directory.GetCurrentDirectory();
        services.AddMemoTreeServices(workspaceRoot);
        var provider = services.BuildServiceProvider();

        // 解析核心服务并尝试渲染默认视图
        try {
            var memoTree = provider.GetRequiredService<IMemoTreeService>();
            var markdown = await memoTree.RenderViewAsync("default");
            Console.WriteLine("=== MemoTree Render (default) ===");
            Console.WriteLine(markdown);
            Console.WriteLine("=================================");
        } catch (Exception ex) {
            Console.WriteLine($"[MemoTree] 无法渲染默认视图：{ex.Message}");
        }

        // 2) 原有 LLM 示例（保留，方便对比）
        IChatClient client = new OpenRouterClient();
        var llmContext = new SimpleLlmContext();

        // 初始化上下文
        llmContext.SystemInstruction = "You are not just a helpful assistant.";
        llmContext.AddUser("Hi from C#. Please reply with a short greeting and your model name if available.");

        var content = await client.Call(llmContext);
        if (content is null) {
            Console.WriteLine("[Fail] 未获得有效响应。");
            return 1;
        }

        Console.WriteLine("=== Assistant ===");
        Console.WriteLine(content);
        Console.WriteLine("=================");
        return 0;
    }
}
