using System.Text;

namespace Atelia.MutableContextAgentProto;

internal static class Program {
    public static async Task<int> Main(string[] args) {
        Console.OutputEncoding = Encoding.UTF8;

        var command = args.Length == 0 ? "help" : args[0];
        return command switch {
            "smoke" => RunSmoke(),
            "render-demo" => Phase1Commands.RunRenderDemo(),
            "maze-demo" => Phase1Commands.RunMazeDemo(),
            "maze-fake-run" => Phase1Commands.RunMazeFake(),
            "ping-llm" => await Phase1Commands.RunPingLlmAsync(),
            "maze-llm-run" => await Phase1Commands.RunMazeLlmAsync(),
            "phase2-fake-wizard" => Phase2Commands.RunFakeWizard(),
            "phase2-llm-wizard" => await Phase2Commands.RunLlmWizardAsync(),
            "help" or "-h" or "--help" => RunHelp(),
            _ => RunUnknown(command)
        };
    }

    private static int RunSmoke() {
        Console.WriteLine("MutableContextAgentProto smoke OK.");
        Console.WriteLine("Use `render-demo`, `maze-demo`, `maze-fake-run`, `ping-llm`, or `maze-llm-run` next.");
        return 0;
    }

    private static int RunHelp() {
        Console.WriteLine(
            """
MutableContextAgentProto

Commands:
  smoke          Validate the prototype executable starts.
  render-demo    Render a sample mutable working context as one user message.
  maze-demo      Show the deterministic maze environment.
  maze-fake-run  Run the maze tool-loop with a scripted fake policy.
  ping-llm       Send a one-message connectivity ping to DeepSeek V4.
  maze-llm-run   Let DeepSeek V4 drive the maze through native server tool calls.
  phase2-fake-wizard
                 Run local view_file -> select_remember -> reduced view demo.
  phase2-llm-wizard
                 Run DeepSeek view_file micro-wizard with history tool calls.
"""
        );
        return 0;
    }

    private static int RunUnknown(string command) {
        Console.Error.WriteLine($"Unknown command: {command}");
        return RunHelp() == 0 ? 1 : 1;
    }
}
