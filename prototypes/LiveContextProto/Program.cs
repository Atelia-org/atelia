using System.Text;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.Agent;
using Atelia.LiveContextProto.Provider;
using Atelia.LiveContextProto.Provider.Anthropic;
using Atelia.LiveContextProto.State;
using Atelia.LiveContextProto.Tools;

namespace Atelia.LiveContextProto;

internal static class Program {
    private const string DebugCategory = "History";

    public static int Main(string[] args) {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        DebugUtil.Print(DebugCategory, "LiveContextProto bootstrap starting");

        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) {
            Console.Error.WriteLine("[error] 环境变量 ANTHROPIC_API_KEY 未设置，无法启动 Anthropic Provider。");
            return 1;
        }

        var model = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL") ?? "claude-3-5-sonnet-20241022";
        var specification = Environment.GetEnvironmentVariable("ANTHROPIC_SPEC") ?? "messages-v1";
        var providerId = Environment.GetEnvironmentVariable("ANTHROPIC_PROVIDER_ID") ?? "anthropic";

        var agentState = AgentState.CreateDefault();
        var anthropicClient = new AnthropicProviderClient(apiKey);
        var router = ProviderRouter.CreateAnthropic(anthropicClient, model, specification, providerId);

        var toolCatalog = ToolCatalog.Create(Array.Empty<ITool>());
        var toolExecutor = new ToolExecutor(toolCatalog.CreateHandlers());

        var orchestrator = new AgentOrchestrator(agentState, router, toolExecutor, toolCatalog);
        var agent = new LlmAgent(agentState, orchestrator, toolExecutor, toolCatalog);
        var defaultInvocation = new ProviderInvocationOptions(ProviderRouter.DefaultAnthropicStrategy);
        var loop = new ConsoleTui(agent, defaultInvocation);

        loop.Run();

        DebugUtil.Print(DebugCategory, "LiveContextProto shutdown");
        return 0;
    }
}
