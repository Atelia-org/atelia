using System.Net.Sockets;
using System.Threading.Tasks;
using StreamJsonRpc;
using System;

namespace CodeCortex.Cli {
    public static class TcpRpcClient {
        public static async Task<TResult> InvokeAsync<TResult>(string host, int port, Func<JsonRpc, Task<TResult>> rpcCall) {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port);
            using var stream = client.GetStream();
            using var rpc = new JsonRpc(stream, stream);
            rpc.StartListening();
            return await rpcCall(rpc);
        }
    }
}
