using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Data;
using Atelia.EventJournal;
using Atelia.Galatea.Input;
using Atelia.MdJson;
using Atelia.SessionJournal;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaConnectionStateV4ProjectionTests {
    private const string OldId = "technical-old-id";
    private const string NewId = "technical-new-id";
    private const string DiagnosticId = "technical-diagnostic-id";

    [Fact]
    public void HistoricalV3StillProjectsExactMachineFields() {
        JsonObject state = State("日常生活", "日常生活");
        state.Remove("effectiveName");
        state.Remove("turnName");
        state["lastChange"] = Change("旧配置", "日常生活");
        state["lastChange"]!.AsObject().Remove("previousName");
        var input = SessionInputContent.Structured(GalateaObservationSchema.V3SchemaId, Content(state).JsonValue);
        string projected = GalateaObservationInputProjector.Instance.Project(input);
        Assert.True(JsonElement.DeepEquals(input.JsonValue, MdJsonSerializer.Read(projected)));
        Assert.Contains(NewId, projected, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryProjectionShowsOnlyFrozenEffectiveName() {
        SessionInputContent input = Content(State("日常生活", "日常生活"));
        byte[] original = input.ToUtf8Json();

        string projected = GalateaObservationInputProjector.Instance.Project(input);

        Assert.Equal(original, input.ToUtf8Json());
        Assert.Equal(projected, GalateaObservationInputProjector.Instance.Project(input));
        Assert.Equal("当前运行配置：日常生活。", MdJsonSerializer.Read(projected).GetProperty("connectionState").GetString());
        Assert.DoesNotContain(NewId, projected, StringComparison.Ordinal);
        Assert.DoesNotContain("runtimeOverrideConnectionId", projected, StringComparison.Ordinal);
        Assert.Contains("玩家输入", projected, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticProjectionShowsBothNamesAndNoDiagnosticId() {
        JsonObject state = State("日常生活", "深入推演");
        state["turnConnectionId"] = DiagnosticId;
        SessionInputContent input = Content(state);

        string visible = MdJsonSerializer.Read(GalateaObservationInputProjector.Instance.Project(input))
            .GetProperty("connectionState").GetString()!;

        Assert.Equal("常规运行配置：日常生活。\n本回合临时使用：深入推演。", visible);
        Assert.DoesNotContain(DiagnosticId, GalateaObservationInputProjector.Instance.Project(input), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("深入推演", "日常生活", "运行配置已调整：")]
    [InlineData("日常生活", "日常生活", "连接已更新，配置名称不变：")]
    public void ChangeProjectionIncludesBothFrozenNamesIdsAndEvidence(string previousName, string currentName, string prefix) {
        JsonObject state = State(currentName, currentName);
        state["lastChange"] = Change(previousName, currentName);
        SessionInputContent input = Content(state);

        string projected = GalateaObservationInputProjector.Instance.Project(input);
        string visible = MdJsonSerializer.Read(projected).GetProperty("connectionState").GetString()!;

        Assert.Contains(prefix, visible, StringComparison.Ordinal);
        Assert.Contains($"{previousName}（{OldId}）→ {currentName}（{NewId}）", visible, StringComparison.Ordinal);
        Assert.Contains("依据：上一回合戴着眼镜。", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceActionAddress", projected, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticAndChangeCanAppearTogetherWithoutTreatingDiagnosticAsChangeTarget() {
        JsonObject state = State("日常生活", "深入推演");
        state["turnConnectionId"] = DiagnosticId;
        state["lastChange"] = Change("旧配置", "日常生活");

        string projected = GalateaObservationInputProjector.Instance.Project(Content(state));
        string visible = MdJsonSerializer.Read(projected).GetProperty("connectionState").GetString()!;

        Assert.Contains("常规运行配置：日常生活。", visible, StringComparison.Ordinal);
        Assert.Contains("本回合临时使用：深入推演。", visible, StringComparison.Ordinal);
        Assert.Contains($"旧配置（{OldId}）→ 日常生活（{NewId}）", visible, StringComparison.Ordinal);
        Assert.DoesNotContain(DiagnosticId, projected, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyAndMultilineNamesRemainValidDisplayData() {
        JsonObject state = State("", "");
        state["lastChange"] = Change("第一行\n第二行", "");

        string visible = MdJsonSerializer.Read(GalateaObservationInputProjector.Instance.Project(Content(state)))
            .GetProperty("connectionState").GetString()!;

        Assert.Contains("当前运行配置：未命名配置。", visible, StringComparison.Ordinal);
        Assert.Contains("第一行\n第二行", visible, StringComparison.Ordinal);
        Assert.Contains($"未命名配置（{NewId}）", visible, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-effective-name")]
    [InlineData("missing-previous-name")]
    [InlineData("oversize-name")]
    [InlineData("unknown-state-field")]
    public void V4RejectsMalformedFrozenNames(string defect) {
        JsonObject state = State("日常生活", "日常生活");
        state["lastChange"] = Change("旧配置", "日常生活");
        if (defect == "missing-effective-name") { state.Remove("effectiveName"); }
        if (defect == "missing-previous-name") { state["lastChange"]!.AsObject().Remove("previousName"); }
        if (defect == "oversize-name") { state["turnName"] = new string('x', 4097); }
        if (defect == "unknown-state-field") { state["unknown"] = true; }

        Assert.ThrowsAny<Exception>(() => GalateaObservationInputProjector.Instance.Project(Content(state)));
    }

    private static SessionInputContent Content(JsonObject state) => SessionInputContent.Structured(
        GalateaObservationSchema.V4SchemaId,
        JsonSerializer.SerializeToElement(new JsonObject {
            ["v"] = 1,
            ["kind"] = "player-action",
            ["sender"] = new JsonObject { ["kind"] = "player", ["id"] = "player", ["name"] = "Player" },
            ["externalLocalTimestamp"] = "2026-09-25T10:20:30.0000000+08:00",
            ["action"] = new JsonObject { ["text"] = "玩家输入" },
            ["notices"] = new JsonArray(),
            ["recalls"] = new JsonArray(),
            ["connectionState"] = state
        }));

    private static JsonObject State(string effectiveName, string turnName) => new() {
        ["runtimeOverrideConnectionId"] = NewId,
        ["effectiveConnectionId"] = NewId,
        ["turnConnectionId"] = NewId,
        ["effectiveName"] = effectiveName,
        ["turnName"] = turnName,
        ["lastChange"] = null
    };

    private static JsonObject Change(string previousName, string name) => new() {
        ["sourceActionAddress"] = EventAddressTextCodec.Format(new EventAddress(SizedPtr.Create(4, 4), 1, AddressHint.None)),
        ["previousConnectionId"] = OldId,
        ["connectionId"] = NewId,
        ["previousName"] = previousName,
        ["name"] = name,
        ["evidence"] = "上一回合戴着眼镜。"
    };
}
