using System.Net;
using System.Net.Sockets;
using StreamJsonRpc;
using CodeCortex.ServiceV2.Services;
using CodeCortex.Workspace;
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

    public async Task<string?> getOutline2Async(string path, string fqn, CancellationToken cancellationToken) {
        Interlocked.Increment(ref _requests);
        return await _svc.GetOutlineByFqnAsync(path, fqn, cancellationToken).ConfigureAwait(false);
    }


    public async Task<Search2Result> search2Async(string path, string pattern, int offset, int limit, CancellationToken cancellationToken) {
        Interlocked.Increment(ref _requests);
        var loader = new MsBuildWorkspaceLoader();
        var loaded = await loader.LoadAsync(path, cancellationToken).ConfigureAwait(false);

        bool hasWildcard = pattern.Contains('*') || pattern.Contains('?');
        Regex? rx = hasWildcard ? WildcardToRegex(pattern) : null;
        var all = new List<(string Fqn, string MatchKind)>();

        foreach (var p in loaded.Projects) {
            var comp = await p.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (comp == null) {
                continue;
            }

            foreach (var t in new RoslynTypeEnumerator().Enumerate(comp, cancellationToken)) {
                var fqn = t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var fqnNoPrefix = fqn.StartsWith("global::", StringComparison.Ordinal) ? fqn.Substring("global::".Length) : fqn;
                var simple = t.Name;

                string? kind = null;
                if (hasWildcard) {
                    if (rx!.IsMatch(fqn)) {
                        kind = "WildcardFqn";
                    } else if (rx.IsMatch(fqnNoPrefix)) {
                        kind = "WildcardFqnNoPrefix";
                    } else if (rx.IsMatch(simple)) {
                        kind = "WildcardSimple";
                    }
                } else {
                    // Non-wildcard heuristic matching: Exact > SimpleExact > Prefix > Suffix > Contains (case-insensitive)
                    if (string.Equals(fqn, pattern, StringComparison.Ordinal) || string.Equals(fqnNoPrefix, pattern, StringComparison.Ordinal)) {
                        kind = "Exact";
                    } else if (string.Equals(simple, pattern, StringComparison.Ordinal)) {
                        kind = "SimpleExact";
                    } else if (fqn.StartsWith(pattern, StringComparison.Ordinal)) {
                        kind = "Prefix";
                    } else if (fqn.EndsWith(pattern, StringComparison.Ordinal)) {
                        kind = "Suffix";
                    } else if (fqn.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0) {
                        kind = "Contains";
                    }
                }

                if (kind != null) {
                    all.Add((fqn, kind));
                }
            }
        }

        // 排序：按 FQN 字母序（契约稳定），后续在 Core 复用 SymbolResolver 时再升级
        var total = all.Count;
        offset = Math.Clamp(offset, 0, Math.Max(0, total - 1));
        limit = Math.Max(0, limit);
        var pagePairs = (limit == 0)
            ? Array.Empty<(string Fqn, string MatchKind)>()
            : all.OrderBy(p => p.Fqn).Skip(offset).Take(limit).ToArray();
        var items = pagePairs.Select(p => p.Fqn).ToArray();
        var kinds = pagePairs.Select(p => p.MatchKind).ToArray();
        return new Search2Result(items, total, kinds);
    }

    private static Regex WildcardToRegex(string pattern) {
        // Escape regex special chars, then replace wildcard tokens
        string esc = Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".");
        return new Regex("^" + esc + "$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }

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

