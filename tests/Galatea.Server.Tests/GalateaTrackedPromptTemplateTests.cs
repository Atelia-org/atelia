using System.Text.Json;
using Atelia.Galatea.Prompts;
using Atelia.MdJson;
using Atelia.SessionJournal;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaTrackedPromptTemplateTests {
    [Fact]
    public void HomePathRemainsLiteralBoundDataAcrossRequestProjection() {
        const string home = "/galatea-homes/${characterName}/literal`path";
        SessionInputContent content = Create("${characterName} lives here.", true, false, home);
        string before = content.JsonValue.GetRawText();
        JsonElement projected = MdJsonSerializer.Read(GalateaInputProjector.Instance.Project(content));

        Assert.Equal(home, content.JsonValue.GetProperty("bindings").GetProperty("homeDir").GetString());
        Assert.Equal(home, projected.GetProperty("bindings").GetProperty("homeDir").GetString());
        Assert.Equal("Alice lives here.", projected.GetProperty("instructions")[1].GetProperty("source").GetString());
        Assert.Equal(before, content.JsonValue.GetRawText());
        Assert.Throws<ArgumentOutOfRangeException>(() => Create("${characterName}", true, false, new string('x', 32769)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TrackedResourcesRemainOrderedSourcesWithBoundCapabilities(bool outbound, bool note) {
        string prefix = ReadTracked("trpg-protocol-prefix-zh-cn.md").Trim();
        string context = ReadTracked("character-context-standard-zh-cn.md").Trim();
        string mailbox = ReadTracked("trpg-mailbox-protocol-base-zh-cn.md").Trim();
        string outgoing = ReadTracked("trpg-outbound-mail-protocol-appendix-zh-cn.md").Trim();
        string save = ReadTracked("trpg-character-note-save-appendix-zh-cn.md").Trim();
        Assert.Equal(prefix, GalateaSystemPromptComposer.ProtocolPrefixSource);
        Assert.Equal(context, GalateaBuiltInCharacterContextTemplate.Source);
        Assert.Equal(mailbox, GalateaSystemPromptComposer.MailboxProtocolBaseSource);
        Assert.Equal(outgoing, GalateaSystemPromptComposer.OutboundMailProtocolAppendixSource);
        Assert.Equal(save, GalateaSystemPromptComposer.CharacterNoteSaveAppendixSource);
        var expected = new List<(string Kind, string Source)> {
            ("protocol", prefix), ("character-context", context), ("mailbox-protocol", mailbox)
        };
        if (outbound) { expected.Add(("outbound-mail", outgoing)); }
        if (note) { expected.Add(("character-note-save", save)); }

        SessionInputContent content = Create(context, outbound, note);
        Assert.Equal(GalateaSystemInstructionContent.SchemaId, content.SchemaId);
        JsonElement sources = content.JsonValue.GetProperty("instructions");
        Assert.Equal(expected.Count + 1, sources.GetArrayLength());
        for (int index = 0; index < expected.Count; index++) {
            Assert.Equal(expected[index].Kind, sources[index].GetProperty("kind").GetString());
            Assert.Equal(expected[index].Source, sources[index].GetProperty("source").GetString());
        }
        Assert.Equal("input-meaning", sources[expected.Count].GetProperty("kind").GetString());
        JsonElement bindings = content.JsonValue.GetProperty("bindings");
        Assert.Equal(outbound, bindings.GetProperty("capabilities").GetProperty("outboundMail").GetBoolean());
        Assert.Equal(note, bindings.GetProperty("capabilities").GetProperty("characterNoteSave").GetBoolean());
        Assert.Equal("alice", bindings.GetProperty("character").GetProperty("id").GetString());

        string before = content.JsonValue.GetRawText();
        JsonElement projected = MdJsonSerializer.Read(GalateaInputProjector.Instance.Project(content));
        Assert.True(JsonElement.DeepEquals(bindings, projected.GetProperty("bindings")));
        for (int index = 0; index < sources.GetArrayLength(); index++) {
            Assert.Equal(sources[index].GetProperty("kind").GetString(), projected.GetProperty("instructions")[index].GetProperty("kind").GetString());
            Assert.Equal(sources[index].GetProperty("source").GetString()!.Replace("${characterName}", "Alice", StringComparison.Ordinal),
                projected.GetProperty("instructions")[index].GetProperty("source").GetString());
        }
        Assert.Equal(before, content.JsonValue.GetRawText());
    }

    [Fact]
    public void StandardContextKeepsRecommendedTwoModulesAndMemorySlots() {
        string context = GalateaBuiltInCharacterContextTemplate.Source;

        Assert.DoesNotContain("Galatea", context, StringComparison.Ordinal);
        Assert.DoesNotContain("刘世超", context, StringComparison.Ordinal);
        Assert.DoesNotContain("老刘", context, StringComparison.Ordinal);
        Assert.DoesNotContain("${playerName}", context, StringComparison.Ordinal);
        Assert.DoesNotContain("最旧的一半", context,
            StringComparison.Ordinal);
        Assert.Contains("由RecapGrid派生为带来源的世界理解", context,
            StringComparison.Ordinal);
        Assert.Contains("以更新的raw History为准", context,
            StringComparison.Ordinal);
        Assert.Contains("独立的长期记录", context,
            StringComparison.Ordinal);
        Assert.Contains("即使没有玩家来访", context,
            StringComparison.Ordinal);
        Assert.Equal(5, CountOccurrences(context, "{{}}"));
        Assert.Equal(
            [
                "## 世界观与人物设定",
                "## ${characterName}的自主记忆"
            ],
            context.Split('\n').Where(static line => line.StartsWith(
                "## ",
                StringComparison.Ordinal
            ))
        );
    }

    [Fact]
    public void ProtocolLocksVoiceMailboxAndNoteSaveBoundaries() {
        string prefix = GalateaSystemPromptComposer.ProtocolPrefixSource;
        string mailboxBase =
            GalateaSystemPromptComposer.MailboxProtocolBaseSource;
        string outboundAppendix =
            GalateaSystemPromptComposer.OutboundMailProtocolAppendixSource;
        string noteAppendix = GalateaSystemPromptComposer
            .CharacterNoteSaveAppendixSource;

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
        Assert.Contains("`<character-peer-roster>` JSON 数组中的一个角色名",
            outboundAppendix, StringComparison.Ordinal);
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
        Assert.Contains("只有寄给`Codex`的信会在后续回合收到成功回信", outboundAppendix,
            StringComparison.Ordinal);
        Assert.Contains("且送达失败会在后续回合通知她", outboundAppendix,
            StringComparison.Ordinal);
        Assert.Contains("寄给其他角色的信不承诺回信或失败通知", outboundAppendix,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[${characterName}]", outboundAppendix,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[旁白]", outboundAppendix,
            StringComparison.Ordinal);
        Assert.Contains("### 保存长期 Note", noteAppendix,
            StringComparison.Ordinal);
        Assert.Contains("主动向Galatea runtime提交自己的长期Note", noteAppendix,
            StringComparison.Ordinal);
        Assert.Contains("表达自己现在要保存的意思", noteAppendix,
            StringComparison.Ordinal);
        Assert.Contains("只有后续runtime发出的`Note 保存回执`", noteAppendix,
            StringComparison.Ordinal);
        Assert.Contains("才能证明相应Note内容已经保存成功", noteAppendix,
            StringComparison.Ordinal);
        Assert.Contains("同一次回复", noteAppendix,
            StringComparison.Ordinal);
        Assert.Contains("每条Note的完整内容", noteAppendix,
            StringComparison.Ordinal);
        Assert.Contains("不需要专门的提交句式", noteAppendix,
            StringComparison.Ordinal);
        Assert.Contains("保留事实、否定、条件和不确定性", noteAppendix,
            StringComparison.Ordinal);
        Assert.Contains("没有回执不能判断成功或失败", noteAppendix,
            StringComparison.Ordinal);
        Assert.Contains("最多16条", noteAppendix,
            StringComparison.Ordinal);
        Assert.Contains("无固定格式", noteAppendix,
            StringComparison.Ordinal);
        Assert.Contains("仅声称已经保存", noteAppendix,
            StringComparison.Ordinal);
        Assert.Contains("不承诺分类、metadata补全或召回", noteAppendix,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OperatorContextIsTrustedProseWithoutH2Schema() {
        const string context = """
            ## 任意人物模块
            ${characterName} remembers a visitor.

            ## 额外世界模块
            This remains operator-owned prose.

            ## 第三个模块
            It is accepted without a Markdown parser.
            """;
        SessionInputContent content = Create(context);
        Assert.Equal(context, content.JsonValue.GetProperty("instructions")[1].GetProperty("source").GetString());
        JsonElement projected = MdJsonSerializer.Read(GalateaInputProjector.Instance.Project(content));
        string renderedSource = projected.GetProperty("instructions")[1].GetProperty("source").GetString()!;
        Assert.Contains("## 第三个模块", renderedSource, StringComparison.Ordinal);
        Assert.Contains("Alice remembers a visitor.", renderedSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SourceLimitIsValidatedWithoutAnAggregateRenderedBudget(bool outbound, bool note) {
        string context = GalateaPromptTemplate.CharacterNameToken + new string('x',
            GalateaStrictConfigReader.MaximumSystemPromptUtf8Bytes - GalateaPromptTemplate.CharacterNameToken.Length);
        SessionInputContent content = Create(context, outbound, note);
        Assert.Equal(context, content.JsonValue.GetProperty("instructions")[1].GetProperty("source").GetString());
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(context + "x", outbound, note));
    }

    [Fact]
    public void PeerRosterPreservesIdentityAndLiteralNamesAcrossProjection() {
        GalateaSenderSnapshot[] peers = [
            new("character", "zed", "`Zed`"),
            new("character", "other", "<${characterName}>"),
            new("character", "quoted", "Quoted\"Name")
        ];
        SessionInputContent content = Create("${characterName} lives here.", peers: peers);
        string before = content.JsonValue.GetRawText();
        JsonElement projected = MdJsonSerializer.Read(GalateaInputProjector.Instance.Project(content));
        foreach (JsonElement roster in new[] {
            content.JsonValue.GetProperty("bindings").GetProperty("characterPeers"),
            projected.GetProperty("bindings").GetProperty("characterPeers")
        }) {
            Assert.Equal(peers.Select(peer => peer.Id), roster.EnumerateArray().Select(peer => peer.GetProperty("id").GetString()));
            Assert.Equal(peers.Select(peer => peer.Name), roster.EnumerateArray().Select(peer => peer.GetProperty("name").GetString()));
            Assert.All(roster.EnumerateArray(), peer => Assert.Equal("character", peer.GetProperty("kind").GetString()));
        }
        Assert.Equal(before, content.JsonValue.GetRawText());
    }

    [Theory]
    [InlineData("${playerName}")]
    [InlineData("${characterNam}")]
    [InlineData("${characterName")]
    public void ResourceSourceValidationRejectsFixedPlayerAndMalformedTokens(string source) {
        Assert.Throws<InvalidDataException>(() => GalateaSystemInstructionContent.ValidateInstructionSource(source, requireCharacterName: false));
    }

    [Fact]
    public void OnlyCharacterContextRequiresACharacterBinding() {
        GalateaSystemInstructionContent.ValidateInstructionSource("Static protocol.", requireCharacterName: false);
        Assert.Throws<InvalidDataException>(() => GalateaSystemInstructionContent.ValidateInstructionSource("Static context.", requireCharacterName: true));
    }

    private static SessionInputContent Create(string context, bool outbound = false, bool note = false,
        string? home = "/galatea-homes/test", IReadOnlyList<GalateaSenderSnapshot>? peers = null)
        => GalateaSystemPromptComposer.CreateContent(new GalateaSenderSnapshot("character", "alice", "Alice"),
            context, outbound, note, home, peers);

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
