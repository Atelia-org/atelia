using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.MdJson;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Input;

/// <summary>Projects stored Galatea observations for an actual LLM request; it never opens stores or hydrates host objects.</summary>
public sealed class GalateaObservationInputProjector : ISessionInputProjector {
    public static GalateaObservationInputProjector Instance { get; } = new();

    public string Project(SessionInputContent input) {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.IsStructured) { return input.TextValue; }
        if (!GalateaObservationSchema.IsSupportedSchemaId(input.SchemaId)) {
            throw new NotSupportedException("Unsupported Galatea Observation schema: " + input.SchemaId);
        }
        IReadOnlyList<string> externalPaths = GalateaObservationSchema.ExternalStringPaths(input.SchemaId, input.JsonValue);
        if (input.SchemaId != GalateaObservationSchema.V4SchemaId) {
            return MdJsonSerializer.Write(input.JsonValue, externalPaths);
        }

        JsonObject visible = JsonNode.Parse(input.JsonValue.GetRawText())!.AsObject();
        visible["connectionState"] = RenderConnectionState(input.JsonValue.GetProperty("connectionState"));
        string[] visiblePaths = externalPaths.Where(path => !path.StartsWith("/connectionState/", StringComparison.Ordinal))
            .Append("/connectionState").ToArray();
        return MdJsonSerializer.Write(JsonSerializer.SerializeToElement(visible), visiblePaths);
    }

    private static string RenderConnectionState(JsonElement state) {
        string effectiveName = DisplayName(state.GetProperty("effectiveName").GetString()!);
        string turnName = DisplayName(state.GetProperty("turnName").GetString()!);
        bool diagnostic = state.GetProperty("turnConnectionId").GetString() != state.GetProperty("effectiveConnectionId").GetString();
        var text = new StringBuilder();
        if (diagnostic) {
            text.Append("常规运行配置：").Append(effectiveName).Append("。\n");
            text.Append("本回合临时使用：").Append(turnName).Append('。');
        }
        else { text.Append("当前运行配置：").Append(effectiveName).Append('。'); }

        JsonElement change = state.GetProperty("lastChange");
        if (change.ValueKind == JsonValueKind.Object) {
            string previousName = DisplayName(change.GetProperty("previousName").GetString()!);
            string currentName = DisplayName(change.GetProperty("name").GetString()!);
            bool sameName = previousName == currentName;
            text.Append('\n').Append(sameName ? "连接已更新，配置名称不变：" : "运行配置已调整：")
                .Append(previousName).Append('（').Append(change.GetProperty("previousConnectionId").GetString()).Append("）→ ")
                .Append(currentName).Append('（').Append(change.GetProperty("connectionId").GetString()).Append("）。\n")
                .Append("依据：").Append(change.GetProperty("evidence").GetString());
        }
        return text.ToString();
    }

    private static string DisplayName(string name) => string.IsNullOrWhiteSpace(name) ? "未命名配置" : name;
}
