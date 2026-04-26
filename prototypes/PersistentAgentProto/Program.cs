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

        bool useV2Interactive = args.Length > 0 && (
            string.Equals(args[0], "v2", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(args[0], "interactive-v2", StringComparison.OrdinalIgnoreCase)
        );

        if (args.Length > 0 && args[0] == "smoke") {
            return RunSmoke();
        }
        if (args.Length > 0 && args[0] == "stress") {
            int n = args.Length > 1 ? int.Parse(args[1]) : 100;
            int contentLen = args.Length > 2 ? int.Parse(args[2]) : 200;
            return RunStress(n, contentLen);
        }
        if (args.Length > 0 && args[0] == "stress-json") {
            int n = args.Length > 1 ? int.Parse(args[1]) : 100;
            int contentLen = args.Length > 2 ? int.Parse(args[2]) : 200;
            return RunStressJson(n, contentLen);
        }
        if (args.Length > 0 && args[0] == "stress-dup") {
            int n = args.Length > 1 ? int.Parse(args[1]) : 100;
            int contentLen = args.Length > 2 ? int.Parse(args[2]) : 200;
            return RunStressDuplicate(n, contentLen);
        }
        if (args.Length > 0 && args[0] == "stress-inline") {
            int n = args.Length > 1 ? int.Parse(args[1]) : 100;
            int contentLen = args.Length > 2 ? int.Parse(args[2]) : 200;
            return RunStressInline(n, contentLen);
        }
        if (args.Length > 0 && args[0] == "smoke-v2") {
            return RunSmokeV2();
        }
        if (args.Length > 0 && args[0] == "stress-v2") {
            int n = args.Length > 1 ? int.Parse(args[1]) : 100;
            int contentLen = args.Length > 2 ? int.Parse(args[2]) : 200;
            return RunStressV2(n, contentLen);
        }

        // 仓库目录：interactive-v2 / v2 时优先 args[1]，否则优先 args[0]。
        var repoDir = useV2Interactive
            ? (args.Length > 1
                ? Path.GetFullPath(args[1])
                : Path.Combine(AppContext.BaseDirectory, ".atelia-state-v2"))
            : (args.Length > 0
                ? Path.GetFullPath(args[0])
                : Path.Combine(AppContext.BaseDirectory, ".atelia-state"));

        DebugUtil.Info("Boot", $"PersistentAgentProto starting repo={repoDir} v2={useV2Interactive}");

        using IPersistentChatSession session = useV2Interactive
            ? PersistentSessionV2.OpenOrCreate(repoDir, DefaultSystemPrompt)
            : PersistentSession.OpenOrCreate(repoDir, DefaultSystemPrompt);

        Console.WriteLine(useV2Interactive
            ? "=== PersistentAgentProto V2 ==="
            : "=== PersistentAgentProto ===");
        Console.WriteLine($"repo: {repoDir}");
        Console.WriteLine($"system: {session.SystemPrompt}");
        Console.WriteLine($"resumed messages: {session.MessageCount}");
        Console.WriteLine($"commands: /history /exit");
        Console.WriteLine();

        return await RunInteractiveLoop(session);
    }

    private static async Task<int> RunInteractiveLoop(IPersistentChatSession session) {
        var client = new OpenAIChatClient(
            apiKey: null,
            baseAddress: new Uri(LocalLlmEndpoint),
            dialect: OpenAIChatDialects.SgLangCompatible
        );
        var invocation = new CompletionDescriptor("sglang", client.ApiSpecId, DefaultModelId);

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
                    .StreamCompletionAsync(request, cts.Token)
                    .AggregateAsync(invocation, cts.Token);

                // 流式 UI 简化版：StreamCompletionAsync 是 IAsyncEnumerable，但我们这里直接拿聚合结果
                // —— 真要边流边打可以分两路（先 Tee）；为了原型简洁先这样。
                Console.WriteLine(aggregated.Content);
                if (aggregated.Errors is { Count: > 0 } errs) {
                    foreach (var e in errs) { Console.WriteLine($"[error] {e}"); }
                }
                if (aggregated.Usage is { } u) {
                    DebugUtil.Info("Boot", $"usage prompt={u.PromptTokens} completion={u.CompletionTokens}");
                }

                session.AppendAssistant(aggregated.Content);
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

    private static void PrintHistory(IPersistentChatSession session) {
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

    /// <summary>V2 烟测：结构化 schema + InlineString 文本字段。</summary>
    private static int RunSmokeV2() {
        var dir = Path.Combine(Path.GetTempPath(), "persistent-agent-smoke-v2-" + Guid.NewGuid().ToString("N")[..8]);
        Console.WriteLine($"smoke-v2 dir = {dir}");

        using (var s = PersistentSessionV2.OpenOrCreate(dir, "system-A")) {
            s.AppendUser("hi");
            s.AppendAssistant("hello!");
            s.AppendUser("how are you?");
            s.AppendAssistant("I am fine.");
            Console.WriteLine($"pass1: count={s.MessageCount} system={s.SystemPrompt}");
        }

        using (var s = PersistentSessionV2.OpenOrCreate(dir, "should-be-ignored")) {
            Console.WriteLine($"pass2: count={s.MessageCount} system={s.SystemPrompt}");
            foreach (var (role, content, _) in s.EnumerateMessages()) {
                Console.WriteLine($"  {role}: {content}");
            }
            s.AppendUser("one more");
            s.AppendAssistant("ok");
            Console.WriteLine($"pass2 after append: count={s.MessageCount}");
        }

        using (var s = PersistentSessionV2.OpenOrCreate(dir, "ignored")) {
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
        Console.WriteLine("smoke-v2 ok");
        return 0;
    }

    /// <summary>
    /// 压测：写 N 轮高熵长消息（每条 ~contentLen 字节随机文本），观察 RBF 体积增长。
    /// 主要想验证假说：mixed dict 把每条 content 都 intern 进 SymbolTable，文件会随消息数线性膨胀。
    /// </summary>
    private static int RunStress(int n, int contentLen) {
        var dir = Path.Combine(Path.GetTempPath(), "persistent-agent-stress-" + Guid.NewGuid().ToString("N")[..8]);
        Console.WriteLine($"stress dir = {dir} n={n} contentLen={contentLen}");

        var rng = new Random(42);
        var checkpoints = new[] { 1, 10, 50, 100, 200, 500, 1000 };
        long lastSize = 0;

        using (var s = PersistentSession.OpenOrCreate(dir, "stress-system")) {
            for (int i = 1; i <= n; i++) {
                var u = RandomString(rng, contentLen);
                var a = RandomString(rng, contentLen);
                s.AppendUser(u);
                s.AppendAssistant(a);
                if (Array.IndexOf(checkpoints, i) >= 0 || i == n) {
                    long total = 0;
                    foreach (var f in Directory.EnumerateFiles(dir, "*.sj.rbf", SearchOption.AllDirectories)) {
                        total += new FileInfo(f).Length;
                    }
                    var perTurn = i == 0 ? 0 : (total - lastSize) / (double)(i - checkpoints.Where(c => c < i).DefaultIfEmpty(0).Max());
                    Console.WriteLine($"  after {i,4} turns: total={total,8} bytes  bytes/turn-since-last≈{perTurn,7:F1}");
                    lastSize = total;
                }
            }
        }

        // reopen 一次确认仍然能读
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

    /// <summary>对照实验：每条消息整体序列化为 JSON 字符串，存到 DurableDeque&lt;string&gt;。</summary>
    private static int RunStressJson(int n, int contentLen) {
        var dir = Path.Combine(Path.GetTempPath(), "persistent-agent-stress-json-" + Guid.NewGuid().ToString("N")[..8]);
        Console.WriteLine($"stress-json dir = {dir} n={n} contentLen={contentLen}");
        var rng = new Random(42);
        var checkpoints = new[] { 1, 10, 50, 100, 200, 500, 1000 };
        long lastSize = 0;
        using (var s = JsonStringSession.Create(dir, "stress-system")) {
            for (int i = 1; i <= n; i++) {
                s.Append("user", RandomString(rng, contentLen));
                s.Append("assistant", RandomString(rng, contentLen));
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
        Console.WriteLine("stress-json ok");
        return 0;
    }

    /// <summary>
    /// 对照实验：所有 turn 反复写入完全相同的 user/assistant 内容。
    /// 用于观测 SymbolTable intern 命中后的“理想路径”体积。
    /// </summary>
    private static int RunStressDuplicate(int n, int contentLen) {
        var dir = Path.Combine(Path.GetTempPath(), "persistent-agent-stress-dup-" + Guid.NewGuid().ToString("N")[..8]);
        Console.WriteLine($"stress-dup dir = {dir} n={n} contentLen={contentLen}");
        var checkpoints = new[] { 1, 10, 50, 100, 200, 500, 1000 };
        long lastSize = 0;
        string userText = new('u', contentLen);
        string assistantText = new('a', contentLen);

        using (var s = PersistentSession.OpenOrCreate(dir, "stress-system")) {
            for (int i = 1; i <= n; i++) {
                s.AppendUser(userText);
                s.AppendAssistant(assistantText);
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
        Console.WriteLine("stress-dup ok");
        return 0;
    }

    /// <summary>InlineString 路线：mixed-dict-per-message + content 字段用 InlineString 绕过 SymbolTable。</summary>
    private static int RunStressInline(int n, int contentLen) {
        var dir = Path.Combine(Path.GetTempPath(), "persistent-agent-stress-inline-" + Guid.NewGuid().ToString("N")[..8]);
        Console.WriteLine($"stress-inline dir = {dir} n={n} contentLen={contentLen}");
        var rng = new Random(42);
        var checkpoints = new[] { 1, 10, 50, 100, 200, 500, 1000 };
        long lastSize = 0;
        using (var s = InlineSession.Create(dir, "stress-system")) {
            for (int i = 1; i <= n; i++) {
                s.Append("user", RandomString(rng, contentLen));
                s.Append("assistant", RandomString(rng, contentLen));
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
        Console.WriteLine("stress-inline ok");
        return 0;
    }

    /// <summary>
    /// V2 对照实验：保留结构化 message schema，但把高熵文本放进 InlineString typed dict。
    /// 目标是测量“结构化易用性”与“更低体积”之间是否能取得更好的折中。
    /// </summary>
    private static int RunStressV2(int n, int contentLen) {
        var dir = Path.Combine(Path.GetTempPath(), "persistent-agent-stress-v2-" + Guid.NewGuid().ToString("N")[..8]);
        Console.WriteLine($"stress-v2 dir = {dir} n={n} contentLen={contentLen}");
        var rng = new Random(42);
        var checkpoints = new[] { 1, 10, 50, 100, 200, 500, 1000 };
        long lastSize = 0;
        using (var s = PersistentSessionV2.OpenOrCreate(dir, "stress-system")) {
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

        using (var s = PersistentSessionV2.OpenOrCreate(dir, "ignored")) {
            Console.WriteLine($"reopen: count={s.MessageCount}");
        }
        Console.WriteLine("stress-v2 ok");
        return 0;
    }
}
