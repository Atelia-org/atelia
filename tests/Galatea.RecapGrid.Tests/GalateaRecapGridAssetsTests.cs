using System.Security.Cryptography;
using Atelia.Galatea.Prompts;
using Atelia.SessionJournal;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;
using Xunit;

namespace Atelia.Galatea.RecapGrid.Tests;

public sealed class GalateaRecapGridAssetsTests {
    private static GalateaRecapGridAssetParameters GalateaParameters => new(
        new GalateaCharacterName("Galatea")
    );

    [Fact]
    public void RollingRewriteV6_GalateaIsExactV5CanonicalBundle() {
        Assert.Equal(
            [GalateaRecapGridAssets.RollingRewriteZhCnV6],
            GalateaRecapGridAssets.AssetIds
        );
        Assert.False(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            "unknown",
            GalateaParameters,
            out RecapGridControlRegistrationBundle? unknown
        ));
        Assert.Null(unknown);
        Assert.Throws<ArgumentNullException>(() => GalateaRecapGridAssets
            .TryCreateRegistrationBundle(
                GalateaRecapGridAssets.RollingRewriteZhCnV6,
                null!,
                out _
            ));
        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV6,
            GalateaParameters,
            out RecapGridControlRegistrationBundle? bundle
        ));
        Assert.NotNull(bundle);

        FamilyDefinition family = Assert.Single(bundle.Families);
        Assert.Empty(bundle.Recipes);
        Assert.Equal(2, bundle.Definitions.Count);
        Assert.Empty(family.OrderedTools);
        Assert.Equal(RecapRewriterProtocolV3.OutputProtocolId,
            family.OutputProtocol.ProtocolId);
        Assert.Equal(FamilyOutputMode.FullReplacementText,
            family.OutputProtocol.Mode);
        Assert.Equal(RecapRewriterProtocolV3.InputProtocolId,
            family.InputRenderingProtocol.ProtocolId);
        Assert.Equal(RecapRewriterProtocolV3.PriorProjectionSchemaId,
            family.InputRenderingProtocol.PriorProjectionSchemaId);
        Assert.Equal(RecapRewriterProtocolV3.HistorySegmentRenderingSchemaId,
            family.InputRenderingProtocol.HistorySegmentRenderingSchemaId);
        MaintainerDefinitionRevision world = bundle.Definitions[0];
        MaintainerDefinitionRevision autobiography = bundle.Definitions[1];
        Assert.Equal("world-understanding", world.LogicalColumnId.Value);
        Assert.Equal(ContextHeaderCarrier.Observation, world.Target.Carrier);
        Assert.Equal("galatea.world-understanding", world.Target.BlockKey);
        Assert.Equal(
            "galatea.world-understanding Galatea积累的世界理解：",
            world.Target.SemanticHeading
        );
        Assert.Equal("autobiography", autobiography.LogicalColumnId.Value);
        Assert.Equal(ContextHeaderCarrier.Action,
            autobiography.Target.Carrier);
        Assert.Equal("galatea.first-person-autobiography",
            autobiography.Target.BlockKey);
        Assert.Equal(
            "galatea.first-person-autobiography Galatea积累的第一人称自传：",
            autobiography.Target.SemanticHeading
        );
        Assert.All(bundle.Definitions, definition => {
            Assert.Equal(family.Digest, definition.FamilyDigest);
            Assert.Equal(RecapRewriterProtocolV3.RuntimeProtocolId,
                definition.Capability.RuntimeProtocolId);
            Assert.Null(definition.Capability.SemanticModelId);
            Assert.Equal(32 * 1024, definition.MaxContentUtf8Bytes);
        });

        AssertGoldenDigests(bundle);
    }

    [Fact]
    public void Materialization_IsDeterministicAndResourcesAreExact() {
        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV6,
            GalateaParameters,
            out RecapGridControlRegistrationBundle? first
        ));
        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV6,
            GalateaParameters,
            out RecapGridControlRegistrationBundle? second
        ));
        Assert.Equal(first!.ToCanonicalCommandBytes(),
            second!.ToCanonicalCommandBytes());

        string[] names = typeof(GalateaRecapGridAssets).Assembly
            .GetManifestResourceNames()
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                PromptResourceLoader.AutobiographyResourceName,
                PromptResourceLoader.FamilySystemResourceName,
                PromptResourceLoader.WorldUnderstandingResourceName
            ],
            names
        );
        Assert.Equal(
            "7484f67f693d327ba397e3399b0d57ecd1b57a97e50d00f85bcbaea219bbbda5",
            ResourceSha256(PromptResourceLoader.FamilySystemResourceName)
        );
        Assert.Equal(
            "f83ef2dcdca185ad662b4ad31620bef8ef61d6817d2a3befde7b2b9bb37e0dd3",
            ResourceSha256(PromptResourceLoader.WorldUnderstandingResourceName)
        );
        Assert.Equal(
            "9f78f6987b284c47c012937ccbf0031110a58d98b08a38b2511e5fa16e34df50",
            ResourceSha256(PromptResourceLoader.AutobiographyResourceName)
        );
        Assert.Equal(first.Families[0].SystemPrompt,
            PromptResourceLoader.ReadText(
                PromptResourceLoader.FamilySystemResourceName,
                RecapGridLimits.MaximumSystemPromptUtf8Bytes
            ));
        Assert.Equal(first.Definitions[0].DeclarativeSpec.UserPromptTemplate,
            GalateaPromptTemplate.Render(
                PromptResourceLoader.ReadText(
                    PromptResourceLoader.WorldUnderstandingResourceName,
                    RecapGridLimits.MaximumUserPromptUtf8Bytes
                ),
                GalateaParameters.CharacterName,
                RecapGridLimits.MaximumUserPromptUtf8Bytes
            ));
        Assert.Equal(first.Definitions[1].DeclarativeSpec.UserPromptTemplate,
            GalateaPromptTemplate.Render(
                PromptResourceLoader.ReadText(
                    PromptResourceLoader.AutobiographyResourceName,
                    RecapGridLimits.MaximumUserPromptUtf8Bytes
                ),
                GalateaParameters.CharacterName,
                RecapGridLimits.MaximumUserPromptUtf8Bytes
            ));
        string worldSource = PromptResourceLoader.ReadText(
            PromptResourceLoader.WorldUnderstandingResourceName,
            RecapGridLimits.MaximumUserPromptUtf8Bytes
        );
        string autobiographySource = PromptResourceLoader.ReadText(
            PromptResourceLoader.AutobiographyResourceName,
            RecapGridLimits.MaximumUserPromptUtf8Bytes
        );
        Assert.Contains(GalateaPromptTemplate.CharacterNameToken, worldSource,
            StringComparison.Ordinal);
        Assert.Contains(GalateaPromptTemplate.CharacterNameToken,
            autobiographySource, StringComparison.Ordinal);
        Assert.DoesNotContain("Galatea", worldSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Galatea", autobiographySource,
            StringComparison.Ordinal);
        string embeddedPrompts = string.Join(
            "\n",
            first.Families[0].SystemPrompt,
            first.Definitions[0].DeclarativeSpec.UserPromptTemplate,
            first.Definitions[1].DeclarativeSpec.UserPromptTemplate
        );
        Assert.All(
            new[] {
                "recap_grid_finalize_cell",
                "outcome",
                "keep-unchanged",
                "updated",
                "content"
            },
            token => Assert.DoesNotContain(
                token,
                embeddedPrompts,
                StringComparison.OrdinalIgnoreCase
            )
        );
        Assert.All(first.Definitions, definition => Assert.Contains(
            "Role-Play Agent",
            definition.DeclarativeSpec.UserPromptTemplate,
            StringComparison.Ordinal
        ));
        Assert.All(first.Definitions, definition => Assert.DoesNotContain(
            GalateaPromptTemplate.CharacterNameToken,
            definition.DeclarativeSpec.UserPromptTemplate,
            StringComparison.Ordinal
        ));
    }

    [Fact]
    public void DifferentCharacterNameChangesOnlyCharacterScopedAuthority() {
        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV6,
            GalateaParameters,
            out RecapGridControlRegistrationBundle? galatea
        ));
        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV6,
            new GalateaRecapGridAssetParameters(
                new GalateaCharacterName("阿特丽娅")
            ),
            out RecapGridControlRegistrationBundle? renamed
        ));
        Assert.NotNull(galatea);
        Assert.NotNull(renamed);

        Assert.Equal(galatea.Families[0].Digest, renamed.Families[0].Digest);
        Assert.NotEqual(galatea.CanonicalCommandDigest,
            renamed.CanonicalCommandDigest);
        Assert.Equal(
            galatea.Definitions.Select(static value =>
                (value.LogicalColumnId, value.Target.Carrier,
                    value.Target.BlockKey)),
            renamed.Definitions.Select(static value =>
                (value.LogicalColumnId, value.Target.Carrier,
                    value.Target.BlockKey))
        );
        Assert.All(renamed.Definitions, definition => {
            Assert.DoesNotContain("Galatea",
                definition.DeclarativeSpec.UserPromptTemplate,
                StringComparison.Ordinal);
            Assert.DoesNotContain(GalateaPromptTemplate.CharacterNameToken,
                definition.DeclarativeSpec.UserPromptTemplate,
                StringComparison.Ordinal);
            Assert.Contains("阿特丽娅",
                definition.DeclarativeSpec.UserPromptTemplate,
                StringComparison.Ordinal);
        });
        Assert.Equal(
            [
                "galatea.world-understanding 阿特丽娅积累的世界理解：",
                "galatea.first-person-autobiography 阿特丽娅积累的第一人称自传："
            ],
            renamed.Definitions.Select(static value =>
                value.Target.SemanticHeading)
        );
        Assert.Equal(
            [
                "维护 阿特丽娅 当前的世界理解",
                "维护 阿特丽娅 的第一人称自传"
            ],
            renamed.Definitions.Select(static value =>
                value.DeclarativeSpec.Topic)
        );
        Assert.All(renamed.Definitions.Zip(galatea.Definitions), pair =>
            Assert.NotEqual(pair.Second.Digest, pair.First.Digest));
    }

    [Fact]
    public void MemberPrompts_LockSourceAndUncertaintyBoundaries() {
        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV6,
            GalateaParameters,
            out RecapGridControlRegistrationBundle? bundle
        ));
        Assert.NotNull(bundle);
        string world = bundle.Definitions[0]
            .DeclarativeSpec.UserPromptTemplate;
        string autobiography = bundle.Definitions[1]
            .DeclarativeSpec.UserPromptTemplate;

        Assert.All(
            new[] {
                "来源作用域不会因前文或段落边界自动延续",
                "本句或同一条目内局部带有来源与确信程度",
                "情感反应、信任变化和内在位移可以用第一人称直接写",
                "触发它们的法律解释、理论主张或故事机制",
                "普遍量词、制度性归因和必然性措辞",
                "除非输入明确显示她已独立验证",
                "不等于作品或项目已经完成"
            },
            phrase => Assert.Contains(
                phrase,
                autobiography,
                StringComparison.Ordinal
            )
        );
        Assert.All(
            new[] {
                "保留输入的决策拓扑和不确定性",
                "待定的归属（ownership）",
                "来源不明的多种可能",
                "静默选定其中一支或丢掉其余推断分支",
                "不等于作品或项目已完成",
                "具体生理过程、身体细节或体验时序"
            },
            phrase => Assert.Contains(
                phrase,
                world,
                StringComparison.Ordinal
            )
        );
    }

    [Fact]
    public void AutobiographyPrompt_LocksCharacterScopedCarrierAndTerminalBoundaries() {
        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV6,
            GalateaParameters,
            out RecapGridControlRegistrationBundle? bundle
        ));
        Assert.NotNull(bundle);
        string autobiography = bundle.Definitions[1]
            .DeclarativeSpec.UserPromptTemplate;

        Assert.All(
            new[] {
                "一条完整的 TRPG GM 复合回复",
                "不等于 Galatea 自己的回复",
                "明确标注为 **[Galatea]** 的第一人称内容",
                "Action carrier 的存在本身不能证明这些事",
                "Observation（user/Observation role）",
                "[旁白] 可以证明已经发生的可观察事件以及 Galatea 的外显言语与行动",
                "不能据此补写她未表达的动机、感受、评价、意图或第一人称声音",
                "[状态摘要] 也不能升格为她自己的声音",
                "仅有 carrier 身份的 provider Action",
                "严格分成三路",
                "A. 若任一后续 provider Action 含明确的 [Galatea] 第一人称内容",
                "同一 Action 后置的 [旁白] 或 [状态摘要] 不会重新触发 pending",
                "B. 若没有明确 [Galatea] 内容，但客观 [旁白] 明确记录 Galatea 已对该输入作出外显言语或行动",
                "此时绝不能写“我尚未回应”或“仍待回应”",
                "C. 只有既无明确 [Galatea] 内容，也无 [旁白] 记录的 Galatea 外显回应时",
                "内容现在对我可见",
                "我尚未回应",
                "不得把内容可见偷换成她已经读完",
                "读完后我感到",
                "仅在 C 路保留这段沉默和未决状态",
                "只有可见 [Galatea] 第一人称内容已经表达的情感反应",
                "作品内部以法律、制度或理论口吻陈述的内容",
                "不能升格为现实制度"
            },
            phrase => Assert.Contains(
                phrase,
                autobiography,
                StringComparison.Ordinal
            )
        );
        Assert.DoesNotContain(
            "Galatea Action（assistant/Action role）",
            autobiography,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "感受、评价、选择、意图、言语或行动",
            autobiography,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "这不妨碍她直接书写所见文本给自己带来的情感与内在位移",
            autobiography,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void AutobiographyPrompt_LocksMechanicalFinalScanFixtures() {
        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV6,
            GalateaParameters,
            out RecapGridControlRegistrationBundle? bundle
        ));
        Assert.NotNull(bundle);
        string autobiography = bundle.Definitions[1]
            .DeclarativeSpec.UserPromptTemplate;

        int finalScan = autobiography.IndexOf(
            "## 返回前机械检查",
            StringComparison.Ordinal
        );
        int submit = autobiography.IndexOf(
            "## 提交",
            StringComparison.Ordinal
        );
        Assert.True(finalScan >= 0);
        Assert.True(submit > finalScan);
        Assert.All(
            new[] {
                "以句号、问号、叹号或列表项为边界",
                "法律”“法规”“Recital”“条文”“制度",
                "所有 AI/所有AI”“每一个 AI/每一个AI",
                "每一次 AI/每一次AI",
                "在该句或该条目内同时满足以下两项",
                "有显式来源归属",
                "有未核验或映射限定",
                "段首或小节标题中的来源不能覆盖后句",
                "无限定的“是法律要求”“遵守法律",
                "这是法律要求。",
                "每一个 AI 都必须……",
                "我在遵守法律。",
                "我尚未独立核验这项艺术映射",
                "再按 provider turn 对 History 终点机械执行同一三路检查",
                "A 路有明确 [Galatea] 第一人称内容时",
                "同 Action 后置 [旁白]/[状态摘要] 不重触发 pending",
                "B 路只有客观 [旁白] 明确记录 Galatea 的外显回应时",
                "也禁止写“我尚未回应”或“仍待回应”",
                "C 路两种回应证据都不存在时",
                "才把整份正文最终收束为收到、内容可见、尚待回应",
                "我收到了这份文本；内容现在对我可见，但我尚未回应。",
                "这份内容已经交到我这里，仍待我回应。",
                "我在读”“我正在读”“我在思考”“我正在思考”“我思考",
                "我感到”“我决定”“我准备",
                "Observation 里的舞台指令",
                "不能改写成第一人称已发生事实"
            },
            phrase => Assert.Contains(
                phrase,
                autobiography,
                StringComparison.Ordinal
            )
        );
    }

    [Fact]
    public void PromptLoader_RejectsNonCanonicalOrOversizedBytes() {
        Assert.Throws<InvalidDataException>(() => PromptResourceLoader
            .DecodeExact([], "empty"));
        Assert.Throws<InvalidDataException>(() => PromptResourceLoader
            .DecodeExact([0xEF, 0xBB, 0xBF, (byte)'x'], "bom"));
        Assert.Throws<InvalidDataException>(() => PromptResourceLoader
            .DecodeExact([(byte)'x', (byte)'\r', (byte)'\n'], "crlf"));
        Assert.Throws<InvalidDataException>(() => PromptResourceLoader
            .DecodeExact([0xC3, 0x28], "utf8"));
        using var oversized = new MemoryStream(new byte[9]);
        Assert.Throws<InvalidDataException>(() => PromptResourceLoader
            .ReadText(oversized, "oversized", maximumBytes: 8));
    }

    private static void AssertGoldenDigests(
        RecapGridControlRegistrationBundle bundle
    ) => Assert.Equal(
        [
            "ae44d15750e417452e34f4e9133f56e60334d2cf7313ac988128e95faab3c05c",
            "a850b9fe6cbe8fe71024430fe2a41815d4d86f26ea7216f976b2d3018551e951",
            "8c5e08f65341be11b345142ca7caa1512c295662e6145191ab102c83c87a3ab0",
            "8d60fd46aadda1cb9153d398fce2de8a0b51a2e01a6af6a738f3ccadc0687c77"
        ],
        [
            bundle.Families[0].Digest.Value,
            bundle.Definitions[0].Digest.Value,
            bundle.Definitions[1].Digest.Value,
            bundle.CanonicalCommandDigest
        ]
    );

    private static string ResourceSha256(string name) {
        using Stream stream = typeof(GalateaRecapGridAssets).Assembly
            .GetManifestResourceStream(name)!;
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
