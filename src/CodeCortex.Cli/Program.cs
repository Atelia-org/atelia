using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Text.Json;
using StreamJsonRpc;
using CodeCortex.Service; // 引用 RpcContracts.cs 中的 SymbolMatch/MatchKind

var root = new RootCommand("CodeCortex CLI (纯RPC模式)");

var rpcExeOption = new Option<string>("--rpc-exe", () => "CodeCortex.Service.exe", "RPC服务端可执行文件路径");
var rpcArgsOption = new Option<string>("--rpc-args", () => "", "RPC服务端启动参数");

// resolve 命令
var resolveQueryArg = new Argument<string>("query", "符号名/后缀/通配/模糊");
var resolveLimitOpt = new Option<int>("--limit", () => 20, "最大返回数量");
var resolveCmd = new Command("resolve", "通过RPC解析符号");
resolveCmd.AddArgument(resolveQueryArg);
resolveCmd.AddOption(resolveLimitOpt);
resolveCmd.SetHandler(async (InvocationContext ctx) =>
{
    var query = ctx.ParseResult.GetValueForArgument<string>(resolveQueryArg);
    var limit = ctx.ParseResult.GetValueForOption<int>(resolveLimitOpt);
    var rpcExe = ctx.ParseResult.GetValueForOption<string>(rpcExeOption);
    var rpcArgs = ctx.ParseResult.GetValueForOption<string>(rpcArgsOption);
    using var proc = StartRpcProcess(rpcExe, rpcArgs);
    using var rpc = new JsonRpc(proc.StandardInput.BaseStream, proc.StandardOutput.BaseStream);
    rpc.StartListening();
    var req = new ResolveRequest(query);
    var result = await rpc.InvokeWithParameterObjectAsync<List<SymbolMatch>>(RpcMethods.ResolveSymbol, req);
    foreach (var m in result)
    {
        var amb = m.IsAmbiguous ? " *AMB*" : string.Empty;
        Console.WriteLine($"{m.MatchKind,-14} {m.Fqn} [{m.Kind}] (Id={m.Id}{(m.Distance != null ? $",d={m.Distance}" : "")}){amb}");
    }
});
root.Add(resolveCmd);

// outline 命令
var outlineIdArg = new Argument<string>("idOrFqn", "类型Id或FQN");
var outlineCmd = new Command("outline", "通过RPC获取类型Outline");
outlineCmd.AddArgument(outlineIdArg);
outlineCmd.SetHandler(async (InvocationContext ctx) =>
{
    var idOrFqn = ctx.ParseResult.GetValueForArgument<string>(outlineIdArg);
    var rpcExe = ctx.ParseResult.GetValueForOption<string>(rpcExeOption);
    var rpcArgs = ctx.ParseResult.GetValueForOption<string>(rpcArgsOption);
    using var proc = StartRpcProcess(rpcExe, rpcArgs);
    using var rpc = new JsonRpc(proc.StandardInput.BaseStream, proc.StandardOutput.BaseStream);
    rpc.StartListening();
    var req = new OutlineRequest(idOrFqn);
    var result = await rpc.InvokeWithParameterObjectAsync<string?>(RpcMethods.GetOutline, req);
    if (result == null)
        Console.WriteLine("未找到Outline");
    else
        Console.WriteLine(result);
});
root.Add(outlineCmd);

// search 命令
var searchQueryArg = new Argument<string>("query", "搜索关键字");
var searchLimitOpt = new Option<int>("--limit", () => 20, "最大返回数量");
var searchCmd = new Command("search", "通过RPC搜索符号");
searchCmd.AddArgument(searchQueryArg);
searchCmd.AddOption(searchLimitOpt);
searchCmd.SetHandler(async (InvocationContext ctx) =>
{
    var query = ctx.ParseResult.GetValueForArgument<string>(searchQueryArg);
    var limit = ctx.ParseResult.GetValueForOption<int>(searchLimitOpt);
    var rpcExe = ctx.ParseResult.GetValueForOption<string>(rpcExeOption);
    var rpcArgs = ctx.ParseResult.GetValueForOption<string>(rpcArgsOption);
    using var proc = StartRpcProcess(rpcExe, rpcArgs);
    using var rpc = new JsonRpc(proc.StandardInput.BaseStream, proc.StandardOutput.BaseStream);
    rpc.StartListening();
    var req = new SearchRequest(query, limit);
    var result = await rpc.InvokeWithParameterObjectAsync<List<SymbolMatch>>(RpcMethods.SearchSymbols, req);
    foreach (var m in result)
    {
        Console.WriteLine($"{m.Fqn} [{m.Kind}] (Id={m.Id})");
    }
});
root.Add(searchCmd);

// status 命令
var statusCmd = new Command("status", "查询RPC服务状态");
statusCmd.SetHandler(async (string rpcExe, string rpcArgs) =>
{
    using var proc = StartRpcProcess(rpcExe, rpcArgs);
    using var rpc = new JsonRpc(proc.StandardInput.BaseStream, proc.StandardOutput.BaseStream);
    rpc.StartListening();
    var result = await rpc.InvokeAsync<StatusResponse>(RpcMethods.Status);
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
}, rpcExeOption, rpcArgsOption);
root.Add(statusCmd);

// 守护进程管理命令（start/stop/status）
var daemonCmd = new Command("daemon", "管理RPC服务守护进程");
var startCmd = new Command("start", "启动守护进程");
startCmd.SetHandler(() =>
{
    // TODO: 实现后台启动并写入 daemon.pid
    Console.WriteLine("[TODO] 守护进程启动实现");
});
daemonCmd.AddCommand(startCmd);
var stopCmd = new Command("stop", "停止守护进程");
stopCmd.SetHandler(() =>
{
    // TODO: 实现守护进程停止
    Console.WriteLine("[TODO] 守护进程停止实现");
});
daemonCmd.AddCommand(stopCmd);
var daemonStatusCmd = new Command("status", "查询守护进程状态");
daemonStatusCmd.SetHandler(() =>
{
    // TODO: 检查 daemon.pid 并输出状态
    Console.WriteLine("[TODO] 守护进程状态查询实现");
});
daemonCmd.AddCommand(daemonStatusCmd);
root.Add(daemonCmd);

return await root.InvokeAsync(args);

static Process StartRpcProcess(string exe, string args)
{
    var psi = new ProcessStartInfo(exe, args)
    {
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    return Process.Start(psi) ?? throw new Exception($"无法启动RPC服务进程: {exe}");
}
