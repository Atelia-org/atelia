using System.CommandLine;

var root = new RootCommand("CodeCortex CLI (Phase1 Skeleton)");

var outlineCmd = new Command("outline", "Get outline for a symbol")
{
    new Argument<string>("symbol")
};
outlineCmd.SetHandler((string symbol) =>
{
    Console.WriteLine($"[stub] outline {symbol}");
});

root.Add(outlineCmd);

await root.InvokeAsync(args);
