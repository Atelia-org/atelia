using CodeCortexV2.Workspace;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("CodeCortexV2.DevCli - dev-time one-shot CLI\n" +
                      "Usage:\n  ccv2 <sln> search <query>\n  ccv2 <sln> outline <symbolId> [--level type|member|namespace|assembly] [--md]\n  ccv2 <sln> source <typeId>\n");
    return 0;
}

var sln = args[0];
using var cts = new CancellationTokenSource();
var host = await RoslynWorkspaceHost.LoadAsync(sln, cts.Token);
Console.WriteLine($"Loaded: {host.Workspace.CurrentSolution.FilePath}");
Console.WriteLine("TODO: wire search/outline/source once providers are in place.");
return 0;

