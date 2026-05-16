using Atelia.TextEditScript;
using Xunit;

namespace Atelia.Tests;

public class TextEditScriptXmlTests {
    [Fact]
    public void ParseXml_ShouldParseInsertWithNumericAnchor() {
        var xml = """
<text-edit-script>
  <insert side="after" anchor="123">我今天探索了小溪，发现有鱼。</insert>
</text-edit-script>
""";

        var result = TextEditScriptDocument.ParseXml(xml);

        Assert.True(result.IsSuccess);
        var script = result.Value;
        var insert = Assert.IsType<InsertTextEdit>(Assert.Single(script.Operations));
        Assert.Equal(TextInsertSide.AfterAnchor, insert.Side);
        Assert.Equal(TextAnchorKind.BlockId, insert.Anchor.Kind);
        Assert.Equal((uint)123, insert.Anchor.BlockId);
        Assert.Equal("我今天探索了小溪，发现有鱼。", insert.Content);
    }

    [Fact]
    public void ParseXml_ShouldParseHeadAndTailAnchors() {
        var xml = """
<text-edit-script>
  <insert side="before" anchor="head">第一条。</insert>
  <insert side="after" anchor="tail">最后补一句。</insert>
</text-edit-script>
""";

        var result = TextEditScriptDocument.ParseXml(xml);

        Assert.True(result.IsSuccess);
        var script = result.Value;
        var first = Assert.IsType<InsertTextEdit>(script.Operations[0]);
        var second = Assert.IsType<InsertTextEdit>(script.Operations[1]);
        Assert.Equal(TextAnchorKind.Head, first.Anchor.Kind);
        Assert.Equal(TextInsertSide.BeforeAnchor, first.Side);
        Assert.Equal(TextAnchorKind.Tail, second.Anchor.Kind);
        Assert.Equal(TextInsertSide.AfterAnchor, second.Side);
    }

    [Fact]
    public void ParseXml_ShouldParseReplaceAndDeleteWithAnchors() {
        var xml = """
<text-edit-script>
  <replace anchor="tail">怀疑北边可能有淡水，尚未确认。</replace>
  <delete anchor="head" />
</text-edit-script>
""";

        var result = TextEditScriptDocument.ParseXml(xml);

        Assert.True(result.IsSuccess);
        var script = result.Value;
        var replace = Assert.IsType<ReplaceTextEdit>(script.Operations[0]);
        var delete = Assert.IsType<DeleteTextEdit>(script.Operations[1]);
        Assert.Equal(TextAnchorKind.Tail, replace.Anchor.Kind);
        Assert.Equal("怀疑北边可能有淡水，尚未确认。", replace.Content);
        Assert.Equal(TextAnchorKind.Head, delete.Anchor.Kind);
    }

    [Fact]
    public void ToXml_ShouldRoundTrip() {
        var script = new TextEditScriptDocument([
            new InsertTextEdit(TextInsertSide.AfterAnchor, TextAnchor.Tail, "记住：沙滩 north 通往密林。"),
          new ReplaceTextEdit(TextAnchor.ForBlockId(3), "怀疑密林里有淡水。"),
          new DeleteTextEdit(TextAnchor.Head),
        ]);

        var xml = script.ToXml();
        var parsed = TextEditScriptDocument.ParseXml(xml);

        Assert.True(parsed.IsSuccess);
        Assert.Equal(script.Operations, parsed.Value.Operations);
    }

    [Fact]
    public void ParseXml_ShouldRejectInvalidAnchorZero() {
        var xml = """
<text-edit-script>
  <insert side="after" anchor="0">无效</insert>
</text-edit-script>
""";

        var result = TextEditScriptDocument.ParseXml(xml);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("Invalid anchor", result.Error.Message);
    }

    [Fact]
    public void ParseXml_ShouldRejectUnknownInsertSide() {
        var xml = """
<text-edit-script>
  <insert side="middle" anchor="tail">无效</insert>
</text-edit-script>
""";

        var result = TextEditScriptDocument.ParseXml(xml);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("Invalid insert side", result.Error.Message);
    }

    [Fact]
    public void ParseXml_ShouldRejectUnexpectedElement() {
        var xml = """
<text-edit-script>
  <move anchor="tail">无效</move>
</text-edit-script>
""";

        var result = TextEditScriptDocument.ParseXml(xml);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("Unexpected operation element", result.Error.Message);
    }

    [Fact]
    public void ParseXml_ShouldRejectMultilineContent() {
        var xml = """
<text-edit-script>
  <replace anchor="7">第一行
第二行</replace>
</text-edit-script>
""";

        var result = TextEditScriptDocument.ParseXml(xml);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("cannot contain newlines", result.Error.Message);
    }

    [Fact]
    public void ParseXml_ShouldRejectDeleteWithContent() {
        var xml = """
<text-edit-script>
  <delete anchor="7">不允许</delete>
</text-edit-script>
""";

        var result = TextEditScriptDocument.ParseXml(xml);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("must be empty", result.Error.Message);
    }

    [Fact]
    public void ParseXml_ShouldRejectLegacyTargetAttributeOnReplaceAndDelete() {
        var replaceXml = """
<text-edit-script>
  <replace target="7">旧属性</replace>
</text-edit-script>
""";
        var deleteXml = """
<text-edit-script>
  <delete target="7" />
</text-edit-script>
""";

        var replaceResult = TextEditScriptDocument.ParseXml(replaceXml);
        var deleteResult = TextEditScriptDocument.ParseXml(deleteXml);

        Assert.False(replaceResult.IsSuccess);
        Assert.NotNull(replaceResult.Error);
        Assert.Contains("Unexpected attribute 'target'", replaceResult.Error.Message);

        Assert.False(deleteResult.IsSuccess);
        Assert.NotNull(deleteResult.Error);
        Assert.Contains("Unexpected attribute 'target'", deleteResult.Error.Message);
    }
}
