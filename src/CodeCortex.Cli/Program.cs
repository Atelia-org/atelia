
using System.CommandLine;
using CodeCortex.Cli;

var root = new RootCommand("CodeCortex CLI (纯RPC模式)");

var rpcHostOption = new Option<string>("--rpc-host", () => "127.0.0.1", "RPC服务端主机名");
var rpcPortOption = new Option<int>("--rpc-port", () => 33440, "RPC服务端端口");

var rpcEndpointBinder = new RpcEndpointBinder(rpcHostOption, rpcPortOption);

// v2: outline2（可选 path；query 为通配/后缀/简名/FQN。若唯一命中则返回 outline，否返回列表）
var outline2QueryArg = new Argument<string>("query", "查询：通配/后缀/简名/FQN");
var outline2PathArg = new Argument<string?>("path", () => null, ".sln 或 .csproj 路径（可选）");
var outline2Cmd = new Command("outline2", "按 search2 语义匹配；唯一命中则返回 outline，否返回匹配列表") { outline2QueryArg, outline2PathArg };
outline2Cmd.SetHandler(
    (string query, string? path, RpcEndpoint ep) => CliCommands.Outline2Async(query, path, ep.Host, ep.Port),
    outline2QueryArg, outline2PathArg, rpcEndpointBinder
);
root.Add(outline2Cmd);


// v2: search2（支持通配符与分页；path 可选）
var search2PathArg = new Argument<string?>("path", () => null, ".sln 或 .csproj 路径（可选）");
var search2PatternArg = new Argument<string>("pattern", "通配符模式，如 CodeCortex.* 或 *.Hashing.*");
var search2OffsetOpt = new Option<int>("--offset", () => 0, "起始编号（从0开始）");
var search2LimitOpt = new Option<int>("--limit", () => 20, "最大返回数量");
var search2Cmd = new Command("search2", "调用 search2(pattern, path?, offset, limit) 进行分页搜索") { search2PatternArg, search2PathArg };
search2Cmd.AddOption(search2OffsetOpt);
search2Cmd.AddOption(search2LimitOpt);
var search2JsonOpt = new Option<bool>("--json", () => false, "以 JSON 输出结果");
search2Cmd.AddOption(search2JsonOpt);
search2Cmd.SetHandler(
    (string pattern, string? path, int offset, int limit, bool json, RpcEndpoint ep) => CliCommands.Search2Async(pattern, path, offset, limit, json, ep.Host, ep.Port),
    search2PatternArg, search2PathArg, search2OffsetOpt, search2LimitOpt, search2JsonOpt, rpcEndpointBinder
);
root.Add(search2Cmd);




root.AddGlobalOption(rpcHostOption);
root.AddGlobalOption(rpcPortOption);

return await root.InvokeAsync(args);
