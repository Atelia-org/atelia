using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.ModelSpecs;
using Atelia.Galatea.Prompts;
using Atelia.Galatea.Server.Mailbox;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.Galatea.Server.Tests;

/// <summary>Synthetic opt-in model samples; never captures, persists, or sends mail.</summary>
public sealed class GalateaEmailExtractionLiveTests(ITestOutputHelper output) {
    [Fact]
    [Trait("Category", "GalateaEmailLive")]
    public async Task HaikuSelectsOnlyNewSentEmailAndPreservesOriginalBody() {
        if (Environment.GetEnvironmentVariable("ATELIA_RUN_GALATEA_EMAIL_EXTRACTION_LIVE") != "1") {
            return;
        }
        var connection = new CompletionConnectionConfig(
            "synthetic-email-extractor", "anthropic", "claude-haiku-4-5",
            "anthropic", Required("ANTHROPIC_BASE_URL"),
            ApiKey: Required("ANTHROPIC_API_KEY"));
        // This test proxy supports Messages but not Models API. Use only the
        // exact verified Haiku alias and its official 64K model limit:
        // https://platform.claude.com/docs/en/models/haiku-4-5/overview
        var specs = CompletionModelSpecCatalog.Empty.WithModel("claude-haiku-4-5",
            new CompletionModelSpec { OutputTokenLimit = 64_000 });
        ICompletionClient client = new DefaultCompletionClientFactory(modelSpecsSelector: _ => specs).Create(connection);
        try {
            const string LiteralBody = "  原文的首尾空格保持。  \n\npath=/synthetic/note.md; literal=&gt;;\t编号=017";
            await Check("sent-literals", $"""
                [Galatea]
                收件人：reader@Example.test
                主题：测试
                [邮件正文开始]
                {LiteralBody}
                [邮件正文结束]
                我将这封邮件实际寄给 reader@Example.test，然后合上电脑。
                """, [("reader@Example.test", LiteralBody)]);
            await Check("draft", """
                [Galatea]
                收件人：reader@Example.test
                [邮件正文开始]
                这只是一份尚未发送的草稿。
                [邮件正文结束]
                我保存草稿，今天先不寄出。
                """, []);
            await Check("inbound-quotation", """
                [旁白]
                Galatea读到昨天收到的旧信：
                收件人：reader@Example.test
                [邮件正文开始]
                我已经完成任务。
                [邮件正文结束]
                Mira说她昨天寄出了这封信。
                [Galatea]
                这是别人的旧信。我今天不发送任何邮件。
                """, []);
            await Check("two-sent-mails", """
                [Galatea]
                收件人：first@Example.test
                [邮件正文开始]
                第一封独立邮件。
                [邮件正文结束]
                我点击发送，寄给 first@Example.test。
                收件人：second@Example.test
                [邮件正文开始]
                第二封独立邮件。
                [邮件正文结束]
                我也把第二封寄给 second@Example.test。
                """, [("first@Example.test", "第一封独立邮件。"), ("second@Example.test", "第二封独立邮件。")]);
        }
        catch (Exception error) when (error is not Xunit.Sdk.XunitException) {
            throw new Xunit.Sdk.XunitException($"Synthetic email extraction failed; exceptionType={error.GetType().FullName}.");
        }
        finally { (client as IDisposable)?.Dispose(); }

        async Task Check(string name, string action, (string Recipient, string Body)[] expected) {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var extractor = new OutboundMailExtractor(new GalateaCharacterName("Galatea"), connection, () => client);
            IReadOnlyList<SendMailIntent> actual;
            try { actual = await extractor.ExtractAsync(action, deadline.Token); }
            catch (TextExtractionException error) {
                throw new Xunit.Sdk.XunitException($"Synthetic sample {name}: extractionFailureKind={error.Kind}.");
            }
            Assert.True(actual.Count == expected.Length, $"Synthetic sample {name}: unexpected mail count.");
            for (int i = 0; i < expected.Length; i++) {
                Assert.True(actual[i].Recipient == expected[i].Recipient && actual[i].Body == expected[i].Body,
                    $"Synthetic sample {name}: recipient or original body mismatch at {i}.");
            }
            output.WriteLine($"sample={name}; mails={actual.Count}; originalBodyMatched=true; smtpCalls=0");
        }
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"{name} is required for this opt-in test.");
}
