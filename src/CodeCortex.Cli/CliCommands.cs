using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CodeCortex.Service;

namespace CodeCortex.Cli {
    public static class CliCommands {
        // ===== v2 commands (RpcHost: getOutline2, search2, status2) =====
        internal sealed record Status2Dto(long UptimeMs, long Requests);
        internal sealed record Search2Dto(string[] Items, int Total, string[] MatchKinds);

        private static string ResolveSlnOrThrow(string? pathOpt) {
            var path = pathOpt;
            if (string.IsNullOrWhiteSpace(path)) {
                // find unique .sln under CWD
                var cwd = Directory.GetCurrentDirectory();
                var slns = Directory.GetFiles(cwd, "*.sln", SearchOption.TopDirectoryOnly);
                if (slns.Length == 1) {
                    return slns[0];
                }

                throw new InvalidOperationException($"未指定 path 且在当前目录未找到唯一的 .sln 文件（找到 {slns.Length} 个）。");
            }
            if (Directory.Exists(path)) {
                var slns = Directory.GetFiles(path, "*.sln", SearchOption.TopDirectoryOnly);
                if (slns.Length == 1) {
                    return slns[0];
                }

                if (slns.Length == 0) {
                    throw new InvalidOperationException($"目录中未找到 .sln: {path}");
                }

                throw new InvalidOperationException($"目录中存在多个 .sln，请显式指定: {path}");
            }
            try { return Path.GetFullPath(path); } catch { return path; }
        }

        public static async Task Outline2Async(string query, string? pathOpt, string host, int port) {
            // 先按 search2 语义执行；若唯一匹配则输出 outline，否则输出列表
            string path;
            try { path = ResolveSlnOrThrow(pathOpt); } catch (Exception ex) {
                await Console.Error.WriteLineAsync(ex.Message);
                return;
            }

            var dto = await TcpRpcClient.InvokeAsync(host, port, rpc => rpc.InvokeAsync<Search2Dto>("search2", path, query, 0, 50));
            if (dto.Total == 1 && dto.Items.Length == 1) {
                var fqn = dto.Items[0];
                var outline = await TcpRpcClient.InvokeAsync(host, port, rpc => rpc.InvokeAsync<string?>("getOutline2", path, fqn));
                Console.WriteLine(outline ?? "<null>");
                return;
            }
            // 非唯一：像 search2 一样打印
            var shown = dto.Items.Length;
            var from = 0;
            var to = shown == 0 ? -1 : shown - 1;
            Console.WriteLine($"Total={dto.Total} | Showing {from}..{to} (count={shown})");
            foreach (var fqn in dto.Items) {
                Console.WriteLine(fqn);
            }
        }

        public static async Task Search2Async(string pattern, string? pathOpt, int offset, int limit, bool json, string host, int port) {
            string path;
            try { path = ResolveSlnOrThrow(pathOpt); } catch (Exception ex) {
                await Console.Error.WriteLineAsync(ex.Message);
                return;
            }

            var dto = await TcpRpcClient.InvokeAsync(host, port, rpc => rpc.InvokeAsync<Search2Dto>("search2", path, pattern, offset, limit));
            if (json) {
                var items = dto.Items.Select((fqn, i) => new { fqn, matchKind = (i < dto.MatchKinds.Length ? dto.MatchKinds[i] : null) });
                var obj = new { total = dto.Total, offset, limit, items };
                Console.WriteLine(JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            var shown = dto.Items.Length;
            var from = shown == 0 ? 0 : offset;
            var to = shown == 0 ? -1 : offset + shown - 1;
            Console.WriteLine($"Total={dto.Total} | Showing {from}..{to} (count={shown})");
            foreach (var fqn in dto.Items) {
                Console.WriteLine(fqn);
            }
        }

        public static async Task Status2Async(string host, int port) {
            var result = await TcpRpcClient.InvokeAsync(host, port, rpc => rpc.InvokeAsync<Status2Dto>("status2"));
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }

        // ===== legacy v1 placeholders (to be removed later) =====
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
