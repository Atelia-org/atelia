using System.Text;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.Agent;
using Atelia.LiveContextProto.State;

namespace Atelia.LiveContextProto;

internal static class Program {
    private const string DebugCategory = "History";

    public static void Main(string[] args) {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        DebugUtil.Print(DebugCategory, "LiveContextProto bootstrap starting");

        var agentState = AgentState.CreateDefault();
        var loop = new AgentLoop(agentState);

        loop.Run();

        DebugUtil.Print(DebugCategory, "LiveContextProto shutdown");
    }
}
