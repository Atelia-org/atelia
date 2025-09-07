using System.Net;
using System.Net.Sockets;
using StreamJsonRpc;
using CodeCortex.ServiceV2.Services;
using CodeCortex.Workspace;
using CodeCortex.Workspace.SymbolQuery;
using Microsoft.CodeAnalysis;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Atelia.CodeCortex.RpcHost;

public sealed class OutlineV2Rpc {
    private static readonly DateTime _start = DateTime.UtcNow;
    private static long _requests;
    private readonly OnDemandOutlineService _svc = new();

    [JsonRpcMethod("getOutline2")]
    public async Task<string?> getOutline2Async(string path, string fqn, CancellationToken cancellationToken) {
        Interlocked.Increment(ref _requests);
        return await _svc.GetOutlineByFqnAsync(path, fqn, cancellationToken).ConfigureAwait(false);
    }


    [JsonRpcMethod("search2")]
    public async Task<Search2Result> search2Async(string path, string pattern, int offset, int limit, CancellationToken cancellationToken) {
        Interlocked.Increment(ref _requests);
        var loader = new MsBuildWorkspaceLoader();
        var loaded = await loader.LoadAsync(path, cancellationToken).ConfigureAwait(false);

        var resolver = new CoreResolverAdapter(new WorkspaceTypeSource());
        var paged = await resolver.SearchAsync(loaded, pattern, offset, limit, cancellationToken).ConfigureAwait(false);
        return new Search2Result(paged.Items, paged.Total, paged.MatchKinds);
    }



    [JsonRpcMethod("status2")]
    public Status2 status2() {
        var up = (long)(DateTime.UtcNow - _start).TotalMilliseconds;
        var req = Interlocked.Read(ref _requests);
        return new Status2(up, req);
    }
}

public readonly record struct Status2(long UptimeMs, long Requests);
public readonly record struct Search2Result(string[] Items, int Total, string[] MatchKinds);

public static class Program {
    public static async Task Main(string[] args) {
        int port = 33440;
        if (args.Length >= 2 && args[0] == "--port" && int.TryParse(args[1], out var p)) {
            port = p;
        }

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) => {
            e.Cancel = true;
            cts.Cancel();
        };

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine($"RpcHost listening on 127.0.0.1:{port} (methods: getOutline2, search2, status2)");

        try {
            while (!cts.IsCancellationRequested) {
                var client = await listener.AcceptTcpClientAsync(cts.Token);
                _ = Task.Run(
                    async () => {
                        using (client)
                        using (var stream = client.GetStream()) {
                            var target = new OutlineV2Rpc();
                            using var rpc = new JsonRpc(stream, stream, target);
                            rpc.StartListening();
                            try { await rpc.Completion.ConfigureAwait(false); } catch { /* client dropped */ }
                        }
                    }, cts.Token
                );
            }
        } catch (OperationCanceledException) { } finally {
            listener.Stop();
        }
    }
}

