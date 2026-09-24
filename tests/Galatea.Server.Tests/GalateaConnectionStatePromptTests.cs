using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.MdJson;
using Atelia.SessionJournal;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaConnectionStatePromptTests {
    private static readonly GalateaSenderSnapshot Character = new("character", "alice", "Alice");
    private static readonly GalateaCharacterConnectionOption[] Options = [
        new("dress-model", "裙装 `${characterName}`", "穿着裙装 `${characterName}`"),
        new("pants-model", "裤装", "穿着裤装"),
        new("manual-model", "诊断", "")
    ];

    [Fact]
    public void EnabledStateAddsBoundOptionsAndEmbeddedAppendixWithoutReplacingDataTokens() {
        SessionInputContent content = Create(true);
        Assert.Equal(GalateaSystemInstructionContent.V2SchemaId, content.SchemaId);
        JsonElement stored = content.JsonValue;
        Assert.Equal(2, stored.GetProperty("v").GetInt32());
        JsonElement bindings = stored.GetProperty("bindings");
        Assert.True(bindings.GetProperty("capabilities").GetProperty("characterConnectionState").GetBoolean());
        Assert.Equal(Options.Length, bindings.GetProperty("connectionOptions").GetArrayLength());
        Assert.Equal("", bindings.GetProperty("connectionOptions")[2].GetProperty("trigger").GetString());
        JsonElement instructions = stored.GetProperty("instructions");
        Assert.Equal("character-connection-state", instructions[instructions.GetArrayLength() - 2].GetProperty("kind").GetString());
        Assert.Equal(GalateaSystemPromptComposer.CharacterConnectionStateAppendixSource,
            instructions[instructions.GetArrayLength() - 2].GetProperty("source").GetString());
        Assert.Contains("lastChange", instructions[instructions.GetArrayLength() - 1].GetProperty("source").GetString(), StringComparison.Ordinal);

        string before = stored.GetRawText();
        JsonElement projected = MdJsonSerializer.Read(GalateaInputProjector.Instance.Project(content));
        Assert.True(JsonElement.DeepEquals(bindings, projected.GetProperty("bindings")));
        Assert.Equal("穿着裙装 `${characterName}`",
            projected.GetProperty("bindings").GetProperty("connectionOptions")[0].GetProperty("trigger").GetString());
        Assert.Contains("Alice", projected.GetProperty("instructions")[instructions.GetArrayLength() - 2]
            .GetProperty("source").GetString(), StringComparison.Ordinal);
        Assert.Equal(before, stored.GetRawText());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void DisabledOrAllBlankTriggersKeepExactV1Shape(bool enabled, bool useTrigger) {
        GalateaCharacterConnectionOption[] options = useTrigger ? Options : [new("manual-model", "手动", "")];
        SessionInputContent content = Create(enabled, options);
        Assert.Equal(GalateaSystemInstructionContent.SchemaId, content.SchemaId);
        Assert.Equal(1, content.JsonValue.GetProperty("v").GetInt32());
        JsonElement bindings = content.JsonValue.GetProperty("bindings");
        Assert.False(bindings.TryGetProperty("connectionOptions", out _));
        Assert.False(bindings.GetProperty("capabilities").TryGetProperty("characterConnectionState", out _));
        Assert.DoesNotContain(content.JsonValue.GetProperty("instructions").EnumerateArray(),
            item => item.GetProperty("kind").GetString() == "character-connection-state");
        _ = MdJsonSerializer.Read(GalateaInputProjector.Instance.Project(content));
    }

    [Fact]
    public void V2ValidationRejectsMissingCapabilityMismatchedSourcesAndInvalidOptions() {
        SessionInputContent content = Create(true);
        AssertInvalid(content, root => root["bindings"]!["capabilities"]!.AsObject().Remove("characterConnectionState"));
        AssertInvalid(content, root => root["bindings"]!["capabilities"]!["characterConnectionState"] = false);
        AssertInvalid(content, root => root["bindings"]!.AsObject().Remove("connectionOptions"));
        AssertInvalid(content, root => root["bindings"]!["connectionOptions"]![1]!["connectionId"] = "dress-model");
        AssertInvalid(content, root => root["bindings"]!["connectionOptions"]![0]!["unexpected"] = "x");
        AssertInvalid(content, root => root["instructions"]!.AsArray().RemoveAt(root["instructions"]!.AsArray().Count - 2));
        AssertInvalid(content, root => root["v"] = 1);
        Assert.Throws<InvalidDataException>(() => GalateaSystemInstructionContent.Validate(
            SessionInputContent.Structured(GalateaSystemInstructionContent.SchemaId, content.JsonValue)));
        Assert.Throws<InvalidDataException>(() => GalateaSystemInstructionContent.Project(
            SessionInputContent.Structured(GalateaSystemInstructionContent.SchemaId, content.JsonValue)));
        AssertInvalid(content, root => root["v"] = "2");
        AssertInvalid(content, root => root["v"] = 3);
        AssertInvalid(content, root => root.AsObject().Remove("v"));

        SessionInputContent legacy = Create(false);
        AssertInvalid(legacy, root => root["bindings"]!["capabilities"]!["characterConnectionState"] = true);
        AssertInvalid(legacy, root => root["bindings"]!["connectionOptions"] = new JsonArray());
    }

    [Fact]
    public void ProjectedV2ContentEnforcesPostSubstitutionByteLimit() {
        var longName = new string('N', 1024);
        SessionInputContent content = GalateaSystemPromptComposer.CreateContent(
            new GalateaSenderSnapshot("character", "alice", longName),
            string.Concat(Enumerable.Repeat("${characterName}", 9000)),
            false, false, "/test-home", characterConnectionStateEnabled: true,
            connectionOptions: Options);
        Assert.Throws<InvalidDataException>(() => GalateaInputProjector.Instance.Project(content));
    }

    private static SessionInputContent Create(bool enabled, IReadOnlyList<GalateaCharacterConnectionOption>? options = null)
        => GalateaSystemPromptComposer.CreateContent(Character, "${characterName} lives here.", false, false,
            "/test-home", characterConnectionStateEnabled: enabled, connectionOptions: options ?? Options);

    private static void AssertInvalid(SessionInputContent source, Action<JsonObject> edit) {
        JsonObject copy = JsonNode.Parse(source.JsonValue.GetRawText())!.AsObject();
        edit(copy);
        JsonElement changed = JsonSerializer.SerializeToElement(copy);
        Assert.ThrowsAny<Exception>(() => GalateaSystemInstructionContent.Validate(changed));
    }
}
