using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CodeCortex.Cli.Util;

namespace CodeCortex.Cli {
    public static class CliCommands {
        // ===== v2 commands (RpcHost: getOutline2, search2, status2) =====
        internal sealed record Status2Dto(long UptimeMs, long Requests);
        internal sealed record Search2Dto(string[] Items, int Total, string[] MatchKinds);



        public static async Task Outline2Async(string query, string? pathOpt, string host, int port) {
            // 先按 search2 语义执行；若唯一匹配则输出 outline，否则输出列表
            string path;
            try { path = SlnPathHelper.ResolveSlnOrThrow(pathOpt); }
            catch (Exception ex) {
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
            try { path = SlnPathHelper.ResolveSlnOrThrow(pathOpt); }
            catch (Exception ex) {
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


    }
}
