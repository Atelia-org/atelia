
using System.CommandLine;
using CodeCortex.Cli;

var root = new RootCommand("CodeCortex CLI (纯RPC模式)");

var rpcHostOption = new Option<string>("--rpc-host", () => "127.0.0.1", "RPC服务端主机名");
var rpcPortOption = new Option<int>("--rpc-port", () => 9000, "RPC服务端端口");

var rpcEndpointBinder = new RpcEndpointBinder(rpcHostOption, rpcPortOption);

// resolve 命令
var resolveQueryArg = new Argument<string>("query", "符号名/后缀/通配/模糊");
var resolveLimitOpt = new Option<int>("--limit", () => 20, "最大返回数量");
var resolveCmd = new Command("resolve", "通过RPC解析符号");
resolveCmd.AddArgument(resolveQueryArg);
resolveCmd.AddOption(resolveLimitOpt);
resolveCmd.SetHandler(
    (string query, int limit, RpcEndpoint ep) => CliCommands.ResolveAsync(query, limit, ep.Host, ep.Port),
    resolveQueryArg, resolveLimitOpt, rpcEndpointBinder
);
root.Add(resolveCmd);

// outline 命令
var outlineIdArg = new Argument<string>("idOrFqn", "类型Id或FQN");
var outlineCmd = new Command("outline", "通过RPC获取类型Outline");
outlineCmd.AddArgument(outlineIdArg);
outlineCmd.SetHandler(
    (string idOrFqn, RpcEndpoint ep) => CliCommands.OutlineAsync(idOrFqn, ep.Host, ep.Port),
    outlineIdArg, rpcEndpointBinder
);
root.Add(outlineCmd);

// search 命令
var searchQueryArg = new Argument<string>("query", "搜索关键字");
var searchLimitOpt = new Option<int>("--limit", () => 20, "最大返回数量");
var searchCmd = new Command("search", "通过RPC搜索符号");
searchCmd.AddArgument(searchQueryArg);
searchCmd.AddOption(searchLimitOpt);
searchCmd.SetHandler(
    (string query, int limit, RpcEndpoint ep) => CliCommands.SearchAsync(query, limit, ep.Host, ep.Port),
    searchQueryArg, searchLimitOpt, rpcEndpointBinder
);
root.Add(searchCmd);

// status 命令
var statusCmd = new Command("status", "查询RPC服务状态");
statusCmd.SetHandler(
    (RpcEndpoint ep) => CliCommands.StatusAsync(ep.Host, ep.Port),
    rpcEndpointBinder
);
root.Add(statusCmd);

// 守护进程管理命令（start/stop/status）
var daemonCmd = new Command("daemon", "管理RPC服务守护进程");
var startCmd = new Command("start", "启动守护进程");
startCmd.SetHandler(
    () => {
        // TODO: 实现后台启动并写入 daemon.pid
        Console.WriteLine("[TODO] 守护进程启动实现");
    }
);
daemonCmd.AddCommand(startCmd);
var stopCmd = new Command("stop", "停止守护进程");
stopCmd.SetHandler(
    () => {
        // TODO: 实现守护进程停止
        Console.WriteLine("[TODO] 守护进程停止实现");
    }
);
daemonCmd.AddCommand(stopCmd);
var daemonStatusCmd = new Command("status", "查询守护进程状态");
daemonStatusCmd.SetHandler(
    () => {
        // TODO: 检查 daemon.pid 并输出状态
        Console.WriteLine("[TODO] 守护进程状态查询实现");
    }
);
daemonCmd.AddCommand(daemonStatusCmd);
root.Add(daemonCmd);

root.AddGlobalOption(rpcHostOption);
root.AddGlobalOption(rpcPortOption);

return await root.InvokeAsync(args);
