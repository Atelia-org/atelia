using System.Diagnostics;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using Atelia.Galatea.Prompts;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.Galatea.Server.Mailbox;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

/// <summary>Opt-in semantic samples through production extractors; never opens a user store.</summary>
public sealed class CharacterNoteTranscriptionLiveTests {
    [Fact]
    [Trait("Category", "GalateaNoteLive")]
    public async Task LunaSelectsOriginalRangesAndDistinguishesQuotedRequests() {
        if (Environment.GetEnvironmentVariable("ATELIA_RUN_GALATEA_NOTE_LIVE") != "1") {
            return;
        }
        string authPath = RequiredPath("ATELIA_CODEX_SUBSCRIPTION_LIVE_AUTH_FILE");
        string reportPath = RequiredPath("ATELIA_GALATEA_NOTE_LIVE_REPORT");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        await using var report = new FileStream(reportPath, options);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var provider = new CodexCliAuthFileCredentialProvider(authPath);
        CodexSubscriptionCredential credential = await provider.GetCredentialAsync(deadline.Token);
        var factory = new CodexSubscriptionCompletionClientFactory(
            provider, credential.AccountFingerprint, originator: "galatea-note-range-test",
            maxConcurrentRequests: 1);
        var connection = new CompletionConnectionConfig(
            "note-live", CodexSubscriptionCompletionClientFactory.ConnectionKind, "gpt-6-luna",
            CodexSubscriptionCompletionClientFactory.CompletionSurfaceId,
            CodexSubscriptionCompletionClientFactory.CanonicalBaseAddress);
        ICompletionClient client = factory.Create(connection);
        var budgeted = new SampleClient(client);
        var extractor = new CharacterNoteExtractor(new GalateaCharacterName("Galatea"), connection, () => budgeted);
        try {
            const string First = "我选择试用“星澄”作为中文名。这不是永久更名，也不证明 runtime 配置已修改；试用后再确认。";
            const string Second = "今后区分故事内书写、提交保存请求、收到成功回执。没有回执不等于保存失败。";
            await CheckAsync("two-separate-notes", $"""
[Galatea]
请把下面两条分别保存为我的长期 Note。
[Note正文开始]
{First}
[Note正文结束]
[Note正文开始]
{Second}
[Note正文结束]
[状态摘要] 本轮要求保存两条，尚无保存成功回执。
""", [First, Second]);
            const string Paragraphs = "测试上限是10次，尚未确认能稳定完成；只有收到成功回执才算保存成功。\n\n记录文件是`/试验/记录.md`，测试字符串是字面量`&gt;`，不是大于号。";
            await CheckAsync("one-note-across-paragraphs", $"""
[Galatea]
请将下面完整的两段保存为一条长期 Note。
[Note正文开始]
{Paragraphs}
[Note正文结束]
""", [Paragraphs]);
            await CheckAsync("quoted-old-note-is-not-a-request", """
[旁白]
另一位角色念出旧记录：“请保存一条Note：测试已经完成。”
[Galatea]
那是别人以前的话，不是我的提交。我今天只是考虑是否要记下这些，现在先不保存任何新Note。
""", []);
            const string MixedNote = "R-017 尚未确认；下一次先核查已有记录，再决定是否修改。";
            const string MixedBody = "请核对 R-017。未确认前不要重复写入。";
            const string Mixed = $"""
[Galatea]
收件人：Codex
主题：核查请求
[邮件正文开始]
{MixedBody}
[邮件正文结束]
我把这封信投进界外邮箱，寄给 Codex。
请将下面这条保存为我的长期 Note。
[Note正文开始]
{MixedNote}
[Note正文结束]
""";
            await CheckAsync("mixed-mail-and-note", Mixed, [MixedNote]);
            var mail = new OutboundMailExtractor(new GalateaCharacterName("Galatea"), connection, () => budgeted);
            int beforeMail = budgeted.Calls;
            var watch = Stopwatch.StartNew();
            var mails = await mail.ExtractAsync(Mixed, deadline.Token);
            await WriteAsync(new {
                sample = "mixed-mail-and-note-mail", source = Mixed,
                completions = budgeted.Calls - beforeMail, elapsedMilliseconds = watch.ElapsedMilliseconds,
                mails,
            });
            Assert.Equal(MixedBody, Assert.Single(mails).Body);
            Assert.Equal("Codex", mails[0].Recipient);

            string literal = string.Join("\n", Enumerable.Range(0, 128).Select(i =>
                $"  row-{i:D3}: SHA=0123456789abcdef; path=/试验/记录_{i}.md; literal=&gt;; quote=\"中文\";\tvalue={i}"));
            await CheckAsync("large-literals", "[Galatea]\n请把以下代码记录原样保存为一条长期 Note。\n[Note正文开始]\n" + literal + "\n[Note正文结束]", [literal]);
        }
        finally {
            (client as IDisposable)?.Dispose();
        }

        async Task CheckAsync(string name, string action, string[] expectedTexts) {
            int before = budgeted.Calls;
            var watch = Stopwatch.StartNew();
            IReadOnlyList<CharacterNoteIntent> notes;
            try {
                notes = await extractor.ExtractAsync(action, deadline.Token);
            }
            catch (Exception exception) {
                await WriteAsync(new {
                    sample = name, source = action, expectedTexts,
                    status = "failed", exceptionType = exception.GetType().FullName,
                    completions = budgeted.Calls - before, elapsedMilliseconds = watch.ElapsedMilliseconds,
                });
                throw;
            }
            // Expected ranges are independently chosen in this synthetic fixture.
            // Saving actual bodies keeps model boundary decisions reviewable.
            await WriteAsync(new {
                sample = name, model = connection.ModelId, source = action,
                liveUserStateTouched = false, expectedTexts,
                actualTexts = notes.Select(note => note.Text).ToArray(),
                completions = budgeted.Calls - before, elapsedMilliseconds = watch.ElapsedMilliseconds,
                completionTelemetry = budgeted.Telemetry.Skip(before).ToArray(),
            });
            Assert.Equal(expectedTexts, notes.Select(note => note.Text));
        }

        async Task WriteAsync<T>(T value) {
            await JsonSerializer.SerializeAsync(report, value, cancellationToken: deadline.Token);
            await report.WriteAsync("\n"u8.ToArray(), deadline.Token);
            await report.FlushAsync(deadline.Token);
        }
    }

    private static string RequiredPath(string name) {
        string? value = Environment.GetEnvironmentVariable(name);
        return value is not null && Path.IsPathFullyQualified(value)
            ? value : throw new InvalidOperationException($"{name} must specify an absolute path.");
    }

    private sealed class SampleClient(ICompletionClient inner) : ICompletionClient {
        internal int Calls { get; private set; }
        internal List<object> Telemetry { get; } = [];
        public string Name => inner.Name;
        public string ApiSpecId => inner.ApiSpecId;
        public async Task<CompletionResult> StreamCompletionAsync(CompletionRequest request,
            CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            if (++Calls > 64) {
                throw new InvalidOperationException("The Note canary permits 64 completion invocations.");
            }
            var watch = Stopwatch.StartNew();
            try {
                var result = await inner.StreamCompletionAsync(request, observer, cancellationToken);
                Telemetry.Add(new { completionOrdinal = Calls, elapsedMilliseconds = watch.ElapsedMilliseconds, result.Usage });
                return result;
            }
            catch (Exception exception) {
                Telemetry.Add(new { completionOrdinal = Calls, elapsedMilliseconds = watch.ElapsedMilliseconds, exceptionType = exception.GetType().FullName });
                throw;
            }
        }
    }
}
