using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using StreamJsonRpc;
using System;
using System.IO;
using CodeCortex.Service;

namespace CodeCortex.Service {
    public static class TcpRpcHost {
        public static async Task StartAsync(RpcService service, int port = 9000) {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            Console.WriteLine($"[CodeCortex.Service] TCP JSON-RPC 服务监听于 127.0.0.1:{port}");
            while (true) {
                var client = await listener.AcceptTcpClientAsync();
                _ = HandleClientAsync(client, service);
            }
        }

        private static async Task HandleClientAsync(TcpClient client, RpcService service) {
            using (client)
            using (var stream = client.GetStream())
            using (var rpc = new JsonRpc(stream, stream, service)) {
                rpc.StartListening();
                try {
#pragma warning disable VSTHRD003 // JsonRpc.Completion is a "foreign" Task; no sync context/UI thread here, deadlock risk is not applicable.
                    await rpc.Completion.ConfigureAwait(false);
#pragma warning restore VSTHRD003
                } catch (Exception ex) {
                    Console.WriteLine($"[TCP RPC] 客户端连接异常: {ex.Message}");
                }
            }
        }
    }
}
