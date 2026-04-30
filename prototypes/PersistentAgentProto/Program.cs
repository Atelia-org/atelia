using System.Collections.Immutable;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using Atelia.Diagnostics;

namespace Atelia.PersistentAgentProto;

internal static class Program {
    private const string LocalLlmEndpoint = "http://localhost:8000/";
    private const string DefaultModelId = "Qwen3.5-27b-GPTQ-Int4";
    private const string DefaultSystemPrompt = "You are a concise assistant. Answer in 1-3 sentences.";

    public static async Task<int> Main(string[] args) {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length > 0 && args[0] == "smoke") { return RunSmoke(); }
        if (args.Length > 0 && args[0] == "stress") {
            int n = args.Length > 1 ? int.Parse(args[1]) : 1000;
            int contentLen = args.Length > 2 ? int.Parse(args[2]) : 200;
            return RunStress(n, contentLen);
        }

        var repoDir = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.Combine(AppContext.BaseDirectory, ".atelia-state");
        DebugUtil.Info("Boot", $"PersistentAgentProto starting repo={repoDir}");

        using var session = PersistentSession.OpenOrCreate(repoDir, DefaultSystemPrompt);
        Console.WriteLine($"=== PersistentAgentProto ===");
        Console.WriteLine($"repo: {repoDir}");
        Console.WriteLine($"system: {session.SystemPrompt}");
        Console.WriteLine($"resumed messages: {session.MessageCount}");
        Console.WriteLine($"commands: /history /exit");
        Console.WriteLine();

        return await RunInteractiveLoop(session);
    }

    private static async Task<int> RunInteractiveLoop(PersistentSession session) {
        var client = new OpenAIChatClient(
            apiKey: null,
            baseAddress: new Uri(LocalLlmEndpoint),
            dialect: OpenAIChatDialects.SgLangCompatible
        );

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        while (true) {
            Console.Write("user> ");
            var line = Console.ReadLine();
            if (line is null) { break; }

            if (string.Equals(line, "/exit", StringComparison.OrdinalIgnoreCase)) { break; }
            if (string.Equals(line, "/history", StringComparison.OrdinalIgnoreCase)) {
                PrintHistory(session);
                continue;
            }
            if (string.IsNullOrWhiteSpace(line)) { continue; }

            session.AppendUser(line);

            var request = new CompletionRequest(
                ModelId: DefaultModelId,
                SystemPrompt: session.SystemPrompt,
                Context: session.BuildContext(),
                Tools: ImmutableArray<ToolDefinition>.Empty
            );

            try {
                Console.Write("assistant> ");
                var aggregated = await client
                    .StreamCompletionAsync(request, null, cts.Token);

                Console.WriteLine(aggregated.GetFlattenedText());
                if (aggregated.Errors is { Count: > 0 } errs) {
                    foreach (var e in errs) { Console.WriteLine($"[error] {e}"); }
                }
                if (aggregated.Usage is { } u) {
                    DebugUtil.Info("Boot", $"usage prompt={u.PromptTokens} completion={u.CompletionTokens}");
                }

                session.AppendAssistant(aggregated.GetFlattenedText());
            }
            catch (OperationCanceledException) {
                Console.WriteLine("[cancelled]");
                break;
            }
            catch (Exception ex) {
                Console.WriteLine($"[error] {ex.Message}");
                DebugUtil.Error("Boot", $"call failed: {ex}", ex);
            }
        }

        Console.WriteLine("再见！");
        return 0;
    }

    private static void PrintHistory(PersistentSession session) {
        Console.WriteLine($"--- history ({session.MessageCount} messages) ---");
        var i = 0;
        foreach (var (role, content, ts) in session.EnumerateMessages()) {
            var stamp = DateTimeOffset.FromUnixTimeMilliseconds(ts).ToLocalTime().ToString("HH:mm:ss");
            var preview = content.Length > 200 ? content[..200] + "…" : content;
            Console.WriteLine($"[{i++:D3}] {stamp} {role}> {preview}");
        }
        Console.WriteLine("---");
    }

    /// <summary>不依赖外部 LLM 的烟测：跑通 create→commit→reopen→续写→reopen 全流程并报告 RBF 体积。</summary>
    private static int RunSmoke() {
        var dir = Path.Combine(Path.GetTempPath(), "persistent-agent-smoke-" + Guid.NewGuid().ToString("N")[..8]);
        Console.WriteLine($"smoke dir = {dir}");

        using (var s = PersistentSession.OpenOrCreate(dir, "system-A")) {
            s.AppendUser("hi");
            s.AppendAssistant("hello!");
            s.AppendUser("how are you?");
            s.AppendAssistant("I am fine.");
            Console.WriteLine($"pass1: count={s.MessageCount} system={s.SystemPrompt}");
        }

        using (var s = PersistentSession.OpenOrCreate(dir, "should-be-ignored")) {
            Console.WriteLine($"pass2: count={s.MessageCount} system={s.SystemPrompt}");
            foreach (var (role, content, _) in s.EnumerateMessages()) {
                Console.WriteLine($"  {role}: {content}");
            }
            s.AppendUser("one more");
            s.AppendAssistant("ok");
            Console.WriteLine($"pass2 after append: count={s.MessageCount}");
        }

        using (var s = PersistentSession.OpenOrCreate(dir, "ignored")) {
            Console.WriteLine($"pass3: count={s.MessageCount}");
            var ctx = s.BuildContext();
            Console.WriteLine($"  context kinds: {string.Join(",", ctx.Select(m => m.Kind))}");
        }

        long total = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*.sj.rbf", SearchOption.AllDirectories)) {
            var sz = new FileInfo(f).Length;
            total += sz;
            Console.WriteLine($"  rbf {Path.GetRelativePath(dir, f)} size={sz}");
        }
        Console.WriteLine($"total rbf bytes = {total}");
        Console.WriteLine("smoke ok");
        return 0;
    }

    /// <summary>
    /// 体积压测：N 轮高熵随机文本，每条 ~contentLen 字节。
    /// 配合 cost-model fix（April 2026）后，~830 bytes/turn 是当前基线。
    /// </summary>
    private static int RunStress(int n, int contentLen) {
        var dir = Path.Combine(Path.GetTempPath(), "persistent-agent-stress-" + Guid.NewGuid().ToString("N")[..8]);
        Console.WriteLine($"stress dir = {dir} n={n} contentLen={contentLen}");

        var rng = new Random(42);
        var checkpoints = new[] { 1, 10, 50, 100, 200, 500, 1000 };
        long lastSize = 0;

        using (var s = PersistentSession.OpenOrCreate(dir, "stress-system")) {
            for (int i = 1; i <= n; i++) {
                s.AppendUser(RandomString(rng, contentLen));
                s.AppendAssistant(RandomString(rng, contentLen));
                if (Array.IndexOf(checkpoints, i) >= 0 || i == n) {
                    long total = 0;
                    foreach (var f in Directory.EnumerateFiles(dir, "*.sj.rbf", SearchOption.AllDirectories)) {
                        total += new FileInfo(f).Length;
                    }
                    var perTurn = (total - lastSize) / (double)(i - checkpoints.Where(c => c < i).DefaultIfEmpty(0).Max());
                    Console.WriteLine($"  after {i,4} turns: total={total,8} bytes  bytes/turn-since-last≈{perTurn,7:F1}");
                    lastSize = total;
                }
            }
        }

        using (var s = PersistentSession.OpenOrCreate(dir, "ignored")) {
            Console.WriteLine($"reopen: count={s.MessageCount}");
        }
        Console.WriteLine("stress ok");
        return 0;
    }

    private static string RandomString(Random rng, int length) {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789 ";
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++) { sb.Append(chars[rng.Next(chars.Length)]); }
        return sb.ToString();
    }
}
