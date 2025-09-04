using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using CodeCortex.Service;

namespace CodeCortex.Cli {
    public static class CliCommands {
        public static async Task ResolveAsync(string query, int limit, string host, int port) {
            var req = new ResolveRequest(query);
            var result = await TcpRpcClient.InvokeAsync(host, port, rpc => rpc.InvokeAsync<List<SymbolMatch>>(RpcMethods.ResolveSymbol, req));
            if (result.Count == 0) {
                Console.WriteLine("未在当前项目中找到匹配的符号（仅索引工作空间内类型）");
                return;
            }
            foreach (var m in result) {
                var amb = m.IsAmbiguous ? " [存在歧义]" : string.Empty;
                Console.WriteLine($"{m.MatchKind,-14} {m.Fqn} [{m.Kind}] (Id={m.Id}{(m.Distance != null ? $",d={m.Distance}" : "")}){amb}");
            }
        }

        public static async Task OutlineAsync(string idOrFqn, string host, int port) {
            var req = new OutlineRequest(idOrFqn);
            var result = await TcpRpcClient.InvokeAsync(host, port, rpc => rpc.InvokeAsync<string?>(RpcMethods.GetOutline, req));
            if (result == null) {
                Console.WriteLine("未在当前项目中找到该类型（仅索引工作空间内类型）");
            } else {
                Console.WriteLine(result);
            }
        }

        public static async Task SearchAsync(string query, int limit, string host, int port) {
            var req = new SearchRequest(query, limit);
            var result = await TcpRpcClient.InvokeAsync(host, port, rpc => rpc.InvokeAsync<List<SymbolMatch>>(RpcMethods.SearchSymbols, req));
            if (result.Count == 0) {
                Console.WriteLine("未在当前项目中找到匹配的符号（仅索引工作空间内类型）");
                return;
            }
            foreach (var m in result) {
                var amb = m.IsAmbiguous ? " [存在歧义]" : string.Empty;
                Console.WriteLine($"{m.MatchKind,-14} {m.Fqn} [{m.Kind}] (Id={m.Id}{(m.Distance != null ? $",d={m.Distance}" : "")}){amb}");
            }
        }

        public static async Task StatusAsync(string host, int port) {
            var result = await TcpRpcClient.InvokeAsync(host, port, rpc => rpc.InvokeAsync<StatusResponse>(RpcMethods.Status));
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
