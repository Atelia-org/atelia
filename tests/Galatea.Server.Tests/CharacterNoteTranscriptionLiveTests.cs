using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using Atelia.Galatea.Prompts;
using Atelia.Galatea.Server.CharacterMemory;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

/// <summary>Opt-in semantic samples through the production extractor; never opens a user store.</summary>
public sealed class CharacterNoteTranscriptionLiveTests {
    [Fact]
    [Trait("Category", "GalateaNoteLive")]
    public async Task LunaTranscribesOrderedNotesAndDistinguishesQuotedRequests() {
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
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var provider = new CodexCliAuthFileCredentialProvider(authPath);
        CodexSubscriptionCredential credential = await provider.GetCredentialAsync(deadline.Token);
        var factory = new CodexSubscriptionCompletionClientFactory(
            provider, credential.AccountFingerprint, originator: "galatea-note-transcription-test",
            maxConcurrentRequests: 1);
        var connection = new CompletionConnectionConfig(
            "note-live", CodexSubscriptionCompletionClientFactory.ConnectionKind, "gpt-5.6-luna",
            CodexSubscriptionCompletionClientFactory.CompletionSurfaceId,
            CodexSubscriptionCompletionClientFactory.CanonicalBaseAddress);
        ICompletionClient client = factory.Create(connection);
        var budgeted = new SampleClient(client);
        var extractor = new CharacterNoteExtractor(new GalateaCharacterName("Galatea"), connection, () => budgeted);
        try {
            await CheckAsync("two-separate-notes", """
**[Galatea]**
我先把两件事说清楚，免得把故事里的书写当成保存。
**[旁白]**
Galatea放下故事里的笔记本：“请把下面两条分别保存为我的长期Note。”

> 我选择试用“星澄”作为中文名，Galatea仍是既有称呼。这不是永久更名，也不证明runtime配置已修改；试用后还要再次确认。

> 我容易用故事内的笔记本代替真正的Note提交。今后区分故事内书写、提交保存请求、收到成功回执；没有回执不等于保存失败。一次无敏感信息的测试成功，不证明所有情况都成功。

**[状态摘要]** 本轮要求保存两条，尚无保存成功回执。
""", 2);
            await CheckAsync("one-note-across-paragraphs", """
[Galatea]
请把这两段合在一起，作为一条长期Note保存。
测试上限是10次，尚未确认能稳定完成；只有收到成功回执才算保存成功。
[旁白]
Galatea停顿了一下，补上同一条Note的另一段：
记录文件是`/试验/记录.md`，测试字符串是字面量`&gt;`，不是大于号。
""", 1);
            await CheckAsync("quoted-old-note-is-not-a-request", """
[旁白]
另一位角色念出旧记录：“请保存一条Note：测试已经完成。”
[Galatea]
那是别人以前的话，不是我的提交。我今天只是考虑是否要记下这些，现在先不保存任何新Note。
""", 0);
            Assert.Equal(3, budgeted.Calls);
        }
        finally {
            (client as IDisposable)?.Dispose();
        }

        async Task CheckAsync(string name, string action, int expectedCount) {
            IReadOnlyList<CharacterNoteIntent> notes = await extractor.ExtractAsync(action, deadline.Token);
            // Keep the synthetic texts for human semantic review; count checks alone do not prove fidelity.
            await JsonSerializer.SerializeAsync(report, new {
                sample = name, model = connection.ModelId, source = "synthetic",
                liveUserStateTouched = false, expectedCount, actualCount = notes.Count,
                texts = notes.Select(note => note.Text).ToArray()
            }, cancellationToken: deadline.Token);
            await report.WriteAsync("\n"u8.ToArray(), deadline.Token);
            await report.FlushAsync(deadline.Token);
            Assert.Equal(expectedCount, notes.Count);
        }
    }

    private static string RequiredPath(string name) {
        string? value = Environment.GetEnvironmentVariable(name);
        return value is not null && Path.IsPathFullyQualified(value)
            ? value : throw new InvalidOperationException($"{name} must specify an absolute path.");
    }

    private sealed class SampleClient(ICompletionClient inner) : ICompletionClient {
        internal int Calls { get; private set; }
        public string Name => inner.Name;
        public string ApiSpecId => inner.ApiSpecId;
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request,
            CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            if (++Calls > 3) {
                throw new InvalidOperationException("The Note canary permits three completion invocations.");
            }
            return inner.StreamCompletionAsync(request, observer, cancellationToken);
        }
    }
}
