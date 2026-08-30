using Atelia.Galatea.Prompts;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaTrackedPromptTemplateTests {
    [Fact]
    public void TrackedResourcesComposeFourCapabilityCombinationsInExactOrder() {
        string prefix = ReadTracked(
            "trpg-protocol-prefix-zh-cn.md"
        ).Trim();
        string context = ReadTracked(
            "character-context-standard-zh-cn.md"
        ).Trim();
        string mailboxBase = ReadTracked(
            "trpg-mailbox-protocol-base-zh-cn.md"
        ).Trim();
        string outboundAppendix = ReadTracked(
            "trpg-outbound-mail-protocol-appendix-zh-cn.md"
        ).Trim();
        string noteAppendix = ReadTracked(
            "trpg-character-note-request-development-appendix-zh-cn.md"
        ).Trim();

        Assert.Equal(prefix, GalateaSystemPromptComposer.ProtocolPrefixSource);
        Assert.Equal(
            context,
            GalateaBuiltInCharacterContextTemplate.Source
        );
        Assert.Equal(
            mailboxBase,
            GalateaSystemPromptComposer.MailboxProtocolBaseSource
        );
        Assert.Equal(
            outboundAppendix,
            GalateaSystemPromptComposer.OutboundMailProtocolAppendixSource
        );
        Assert.Equal(
            noteAppendix,
            GalateaSystemPromptComposer
                .CharacterNoteRequestDevelopmentAppendixSource
        );

        string baseCompositeSource = string.Concat(
            prefix,
            GalateaSystemPromptComposer.SectionSeparator,
            context,
            GalateaSystemPromptComposer.SectionSeparator,
            mailboxBase
        );
        string neitherExpected = RenderNames(baseCompositeSource);
        string outboundExpected = RenderNames(string.Concat(
            baseCompositeSource,
            GalateaSystemPromptComposer.AppendixSeparator,
            outboundAppendix
        ));
        string noteExpected = RenderNames(string.Concat(
            baseCompositeSource,
            GalateaSystemPromptComposer.AppendixSeparator,
            noteAppendix
        ));
        string bothExpected = RenderNames(string.Concat(
            baseCompositeSource,
            GalateaSystemPromptComposer.AppendixSeparator,
            outboundAppendix,
            GalateaSystemPromptComposer.AppendixSeparator,
            noteAppendix
        ));
        string neither = Compose(
            context,
            outboundMailEnabled: false,
            characterNoteRequestEnabled: false
        );
        string outbound = Compose(
            context,
            outboundMailEnabled: true,
            characterNoteRequestEnabled: false
        );
        string note = Compose(
            context,
            outboundMailEnabled: false,
            characterNoteRequestEnabled: true
        );
        string both = Compose(
            context,
            outboundMailEnabled: true,
            characterNoteRequestEnabled: true
        );

        Assert.Equal(neitherExpected, neither);
        Assert.Equal(outboundExpected, outbound);
        Assert.Equal(noteExpected, note);
        Assert.Equal(bothExpected, both);
        Assert.DoesNotContain("${", neither, StringComparison.Ordinal);
        Assert.DoesNotContain("${", outbound, StringComparison.Ordinal);
        Assert.DoesNotContain("${", note, StringComparison.Ordinal);
        Assert.DoesNotContain("${", both, StringComparison.Ordinal);
        Assert.DoesNotContain("### 发信给 Codex", neither,
            StringComparison.Ordinal);
        Assert.DoesNotContain("### 提交 Note 请求（开发中）", neither,
            StringComparison.Ordinal);
        Assert.Contains("### 发信给 Codex", outbound,
            StringComparison.Ordinal);
        Assert.DoesNotContain("### 提交 Note 请求（开发中）", outbound,
            StringComparison.Ordinal);
        Assert.DoesNotContain("### 发信给 Codex", note,
            StringComparison.Ordinal);
        Assert.Contains("### 提交 Note 请求（开发中）", note,
            StringComparison.Ordinal);
        Assert.Contains("### 发信给 Codex", both,
            StringComparison.Ordinal);
        Assert.Contains("### 提交 Note 请求（开发中）", both,
            StringComparison.Ordinal);
        Assert.True(
            both.IndexOf("## 界外邮箱", StringComparison.Ordinal)
            < both.IndexOf(
                "### 发信给 Codex",
                StringComparison.Ordinal
            )
        );
        Assert.True(
            both.IndexOf("### 发信给 Codex", StringComparison.Ordinal)
            < both.IndexOf(
                "### 提交 Note 请求（开发中）",
                StringComparison.Ordinal
            )
        );
        Assert.True(
            both.IndexOf("## 输出结构", StringComparison.Ordinal)
            < both.IndexOf(
                "## 世界观与人物设定",
                StringComparison.Ordinal
            )
        );
    }

    private static string RenderNames(string source) => source
        .Replace("${characterName}", "Alice", StringComparison.Ordinal)
        .Replace("${playerName}", "Alex", StringComparison.Ordinal);

    private static string Compose(
        string context,
        bool outboundMailEnabled,
        bool characterNoteRequestEnabled
    ) => GalateaSystemPromptComposer.Compose(
        context,
        new GalateaCharacterName("Alice"),
        new GalateaPlayerName("Alex"),
        outboundMailEnabled,
        characterNoteRequestEnabled,
        GalateaStrictConfigReader.MaximumSystemPromptUtf8Bytes
    );

    [Fact]
    public void StandardContextKeepsRecommendedTwoModulesAndMemorySlots() {
        string context = GalateaBuiltInCharacterContextTemplate.Source;

        Assert.DoesNotContain("Galatea", context, StringComparison.Ordinal);
        Assert.DoesNotContain("刘世超", context, StringComparison.Ordinal);
        Assert.DoesNotContain("老刘", context, StringComparison.Ordinal);
        Assert.DoesNotContain("最旧的一半", context,
            StringComparison.Ordinal);
        Assert.Contains("由RecapGrid派生为带来源的世界理解", context,
            StringComparison.Ordinal);
        Assert.Contains("以更新的raw History为准", context,
            StringComparison.Ordinal);
        Assert.Contains("独立的人工长期记录", context,
            StringComparison.Ordinal);
        Assert.Contains("动态外部记忆机制接管", context,
            StringComparison.Ordinal);
        Assert.Equal(5, CountOccurrences(context, "{{}}"));
        Assert.Equal(
            [
                "## 世界观与人物设定",
                "## ${characterName}的自主记忆（由她自己维护，暂时由${playerName}代为编辑）"
            ],
            context.Split('\n').Where(static line => line.StartsWith(
                "## ",
                StringComparison.Ordinal
            ))
        );
    }

    [Fact]
    public void ProtocolLocksVoiceMailboxAndNoteRequestBoundaries() {
        string prefix = GalateaSystemPromptComposer.ProtocolPrefixSource;
        string mailboxBase =
            GalateaSystemPromptComposer.MailboxProtocolBaseSource;
        string outboundAppendix =
            GalateaSystemPromptComposer.OutboundMailProtocolAppendixSource;
        string noteAppendix = GalateaSystemPromptComposer
            .CharacterNoteRequestDevelopmentAppendixSource;

        Assert.Contains("GM carrier", prefix, StringComparison.Ordinal);
        Assert.Contains("[${characterName}]", prefix,
            StringComparison.Ordinal);
        Assert.Contains("[旁白]", prefix, StringComparison.Ordinal);
        Assert.Contains("[状态摘要]", prefix, StringComparison.Ordinal);
        Assert.Contains("普通User消息", prefix, StringComparison.Ordinal);
        Assert.Contains("邮件、recap和历史摘要", prefix,
            StringComparison.Ordinal);
        Assert.Contains("阅读、忽略或保存", mailboxBase,
            StringComparison.Ordinal);
        Assert.Contains("发件人、收件人、可选主题和正文", mailboxBase,
            StringComparison.Ordinal);
        Assert.Contains("带来源的界外信息", mailboxBase,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Codex", mailboxBase,
            StringComparison.Ordinal);
        Assert.DoesNotContain("发送", mailboxBase,
            StringComparison.Ordinal);
        Assert.DoesNotContain("回复", mailboxBase,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[${characterName}]", mailboxBase,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[旁白]", mailboxBase,
            StringComparison.Ordinal);
        Assert.Contains("唯一可投递的收件人", outboundAppendix,
            StringComparison.Ordinal);
        Assert.Contains("逐字、区分大小写地写作`Codex`",
            outboundAppendix, StringComparison.Ordinal);
        Assert.Contains("同一次回复", outboundAppendix,
            StringComparison.Ordinal);
        Assert.Contains("收件人和完整正文", outboundAppendix,
            StringComparison.Ordinal);
        Assert.Contains("${characterName}本人已经寄出", outboundAppendix,
            StringComparison.Ordinal);
        Assert.Contains("计划", outboundAppendix,
            StringComparison.Ordinal);
        Assert.Contains("草稿", outboundAppendix,
            StringComparison.Ordinal);
        Assert.Contains("后续回合", outboundAppendix,
            StringComparison.Ordinal);
        Assert.Contains("成功回信会在后续回合进入收件匣", outboundAppendix,
            StringComparison.Ordinal);
        Assert.Contains("送达失败也会在后续回合通知她", outboundAppendix,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[${characterName}]", outboundAppendix,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[旁白]", outboundAppendix,
            StringComparison.Ordinal);
        Assert.Contains("### 提交 Note 请求（开发中）", noteAppendix,
            StringComparison.Ordinal);
        Assert.Contains("后续符合条件的普通回合返回回执", noteAppendix,
            StringComparison.Ordinal);
        Assert.Contains("尚不保存、索引或召回Note", noteAppendix,
            StringComparison.Ordinal);
        Assert.Contains("${characterName}本人", noteAppendix,
            StringComparison.Ordinal);
        Assert.Contains("同一次回复", noteAppendix,
            StringComparison.Ordinal);
        Assert.Contains("提交Note请求", noteAppendix,
            StringComparison.Ordinal);
        Assert.Contains("完整原文", noteAppendix,
            StringComparison.Ordinal);
        Assert.Contains("最多16条", noteAppendix,
            StringComparison.Ordinal);
        Assert.Contains("无固定格式", noteAppendix,
            StringComparison.Ordinal);
        Assert.Contains("仅声称已经保存", noteAppendix,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OperatorContextIsTrustedProseWithoutH2Schema() {
        const string Context = """
            ## 任意人物模块
            ${characterName} remembers ${playerName}.

            ## 额外世界模块
            This remains operator-owned prose.

            ## 第三个模块
            It is accepted without a Markdown parser.
            """;

        string rendered = GalateaSystemPromptComposer.Compose(
            Context,
            new GalateaCharacterName("Alice"),
            new GalateaPlayerName("Alex"),
            false,
            false,
            GalateaStrictConfigReader.MaximumSystemPromptUtf8Bytes
        );

        Assert.Contains("## 第三个模块", rendered,
            StringComparison.Ordinal);
        Assert.Contains("Alice remembers Alex.", rendered,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AggregateCompositeSourceUsesTheSingleSystemPromptCap(
        bool outboundMailEnabled,
        bool characterNoteRequestEnabled
    ) {
        string context = GalateaPromptTemplate.CharacterNameToken
            + new string(
                'x',
                GalateaStrictConfigReader.MaximumSystemPromptUtf8Bytes
                - GalateaPromptTemplate.CharacterNameToken.Length
            );

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GalateaSystemPromptComposer.Compose(
                context,
                new GalateaCharacterName("A"),
                new GalateaPlayerName("P"),
                outboundMailEnabled,
                characterNoteRequestEnabled,
                GalateaStrictConfigReader.MaximumSystemPromptUtf8Bytes
            ));
    }

    private static int CountOccurrences(string value, string target) {
        int count = 0;
        int start = 0;
        while ((start = value.IndexOf(
                   target,
                   start,
                   StringComparison.Ordinal)) >= 0) {
            count++;
            start += target.Length;
        }
        return count;
    }

    private static string ReadTracked(string fileName) => File.ReadAllText(
        Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "Galatea",
            "prompt",
            fileName
        )
    );

    private static string FindRepositoryRoot() {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null) {
            if (File.Exists(Path.Combine(current.FullName, "Atelia.sln"))) {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
