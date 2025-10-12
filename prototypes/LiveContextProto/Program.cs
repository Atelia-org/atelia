using System.IO;
using System.Text;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.Agent;
using Atelia.LiveContextProto.Provider;
using Atelia.LiveContextProto.Provider.Stub;
using Atelia.LiveContextProto.State;

namespace Atelia.LiveContextProto;

internal static class Program {
    private const string DebugCategory = "History";

    public static void Main(string[] args) {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        DebugUtil.Print(DebugCategory, "LiveContextProto bootstrap starting");

        var agentState = AgentState.CreateDefault();
        var stubScriptsDirectory = Path.Combine(AppContext.BaseDirectory, "Provider", "StubScripts");
        var stubProvider = new StubProviderClient(stubScriptsDirectory);
        var router = ProviderRouter.CreateDefault(stubProvider);
        var orchestrator = new AgentOrchestrator(agentState, router);
        var loop = new AgentLoop(agentState, orchestrator);

        loop.Run();

        DebugUtil.Print(DebugCategory, "LiveContextProto shutdown");
    }
}
