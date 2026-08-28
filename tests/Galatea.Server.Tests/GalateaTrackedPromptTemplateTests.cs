using Atelia.Galatea.Prompts;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaTrackedPromptTemplateTests {
    [Fact]
    public void TrackedResourcesComposeInExactCodeOwnedOrder() {
        string prefix = ReadTracked(
            "trpg-protocol-prefix-zh-cn.md"
        ).Trim();
        string context = ReadTracked(
            "character-context-standard-zh-cn.md"
        ).Trim();
        string suffix = ReadTracked(
            "trpg-mailbox-protocol-suffix-zh-cn.md"
        ).Trim();

        Assert.Equal(prefix, GalateaSystemPromptComposer.ProtocolPrefixSource);
        Assert.Equal(
            context,
            GalateaBuiltInCharacterContextTemplate.Source
        );
        Assert.Equal(
            suffix,
            GalateaSystemPromptComposer.MailboxProtocolSuffixSource
        );

        string compositeSource = string.Concat(
            prefix,
            GalateaSystemPromptComposer.SectionSeparator,
            context,
            GalateaSystemPromptComposer.SectionSeparator,
            suffix
        );
        string expected = compositeSource
            .Replace("${characterName}", "Alice", StringComparison.Ordinal)
            .Replace("${playerName}", "Alex", StringComparison.Ordinal);
        string rendered = GalateaSystemPromptComposer.Compose(
            context,
            new GalateaCharacterName("Alice"),
            new GalateaPlayerName("Alex"),
            GalateaStrictConfigReader.MaximumSystemPromptUtf8Bytes
        );

        Assert.Equal(expected, rendered);
        Assert.DoesNotContain("${", rendered, StringComparison.Ordinal);
        Assert.True(
            rendered.IndexOf("## 输出结构", StringComparison.Ordinal)
            < rendered.IndexOf(
                "## 世界观与人物设定",
                StringComparison.Ordinal
            )
        );
        Assert.True(
            rendered.IndexOf(
                "## 世界观与人物设定",
                StringComparison.Ordinal
            ) < rendered.IndexOf(
                "## 界外邮箱机制",
                StringComparison.Ordinal
            )
        );
    }

    [Fact]
    public void StandardContextKeepsRecommendedTwoModulesAndMemorySlots() {
        string context = GalateaBuiltInCharacterContextTemplate.Source;

        Assert.DoesNotContain("Galatea", context, StringComparison.Ordinal);
        Assert.DoesNotContain("刘世超", context, StringComparison.Ordinal);
        Assert.DoesNotContain("老刘", context, StringComparison.Ordinal);
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
    public void ProtocolLocksVoiceSourceAndMailboxBoundaries() {
        string prefix = GalateaSystemPromptComposer.ProtocolPrefixSource;
        string suffix =
            GalateaSystemPromptComposer.MailboxProtocolSuffixSource;

        Assert.Contains("GM carrier", prefix, StringComparison.Ordinal);
        Assert.Contains("[${characterName}]", prefix,
            StringComparison.Ordinal);
        Assert.Contains("[旁白]", prefix, StringComparison.Ordinal);
        Assert.Contains("[状态摘要]", prefix, StringComparison.Ordinal);
        Assert.Contains("普通User消息", prefix, StringComparison.Ordinal);
        Assert.Contains("邮件、recap和历史摘要", prefix,
            StringComparison.Ordinal);
        Assert.Contains("`Codex`", suffix, StringComparison.Ordinal);
        Assert.Contains("收件人和完整正文", suffix,
            StringComparison.Ordinal);
        Assert.Contains("已经完成发送", suffix, StringComparison.Ordinal);
        Assert.Contains("回信会立刻出现", suffix, StringComparison.Ordinal);
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
            GalateaStrictConfigReader.MaximumSystemPromptUtf8Bytes
        );

        Assert.Contains("## 第三个模块", rendered,
            StringComparison.Ordinal);
        Assert.Contains("Alice remembers Alex.", rendered,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AggregateCompositeSourceUsesTheSingleSystemPromptCap() {
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
