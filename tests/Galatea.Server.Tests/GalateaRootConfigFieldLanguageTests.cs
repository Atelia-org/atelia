using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion;
using Atelia.Galatea.Prompts;
using Atelia.SessionJournal;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.AgentControl;
using Atelia.SessionJournal.RecapGrid.Control;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaRootConfigFieldLanguageTests {
    [Fact]
    public void HeartbeatEnrollmentComesOnlyFromCharactersAndAllowsZeroPlayers() {
        using var fixture = new RootConfigFixture();
        JsonObject root = ParseRoot(MinimalV10);
        root["players"] = new JsonArray();
        Assert.Empty(fixture.Load(root.ToJsonString()).HeartbeatCharacterIds);
        CharacterObject(root)["heartbeatEnabled"] = true;
        GalateaConfig config = fixture.Load(root.ToJsonString());
        Assert.Empty(config.Players);
        Assert.Equal(["alice"], config.HeartbeatCharacterIds);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)config.HeartbeatCharacterIds)[0] = "changed");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("[]")]
    public void HeartbeatEnrollmentRejectsNonBooleanValues(string json) {
        JsonObject root = ParseRoot(MinimalV10);
        CharacterObject(root)["heartbeatEnabled"] = JsonNode.Parse(json);
        Assert.Throws<InvalidDataException>(() => GalateaStrictConfigReader.ValidateRoot(Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    [Theory]
    [InlineData("serverAgentUserIds")]
    [InlineData("users")]
    [InlineData("Characters")]
    public void OldOrWrongCaseRootFieldsAreRejected(string field) {
        JsonObject root = ParseRoot(MinimalV10);
        root[field] = new JsonArray();
        Assert.Throws<InvalidDataException>(() => GalateaStrictConfigReader.ValidateRoot(Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    [Fact]
    public void PlayerAndCharacterIdsHaveIndependentNamespaces() {
        using var fixture = new RootConfigFixture();
        JsonObject root = ParseRoot(MinimalV10);
        PlayerObject(root)["id"] = "alice";
        GalateaConfig config = fixture.Load(root.ToJsonString());
        Assert.Equal("alice", Assert.Single(config.Players).PlayerId);
        Assert.Equal("alice", Assert.Single(config.Characters).CharacterId);
        root["players"]!.AsArray().Add(PlayerObject(root).DeepClone());
        Assert.Throws<InvalidOperationException>(() => fixture.Load(root.ToJsonString()));
    }

    private const string MinimalV10 = """
        {"v":10,"characters":[{"id":"alice","name":"Galatea","homeDir":"/__TEST_HOME__/alice","sessionDir":"sessions/alice","delegationStateDir":"delegation-state/alice","characterMemoryStateDir":"character-memory/alice","sessionProvisioning":"create-if-missing","defaultConnectionId":"test","characterContextTemplate":"inline ${characterName}"}],"players":[{"id":"player-main","name":"刘世超","password":"pw"}],"runtime":{"recapGrid":{"routeManifestPath":"routes.json","agentControlProfileFiles":["profile.json"],"currentAgentControlProfileId":"test-profile"}}}
        """;

    private const string ReorderedEscapedFullV10 = """
        {"runtime":{"maintenanceMode":true,"recapGrid":{"currentAgentControlProfileId":"test-profile","agentControlProfileFiles":["profile.json"],"\u0072outeManifestPath":"routes.json"},"callLogDir":"call-logs","listenUrls":["opaque-listener","opaque-listener"]},"players":[{"password":"pw","name":"刘世超","id":"player-main"}],"\u0063haracters":[{"characterContextTemplateFile":null,"characterContextTemplate":"inline ${characterName}","name":"Galatea","homeDir":"/__TEST_HOME__/alice","sessionDir":"sessions/alice","delegationStateDir":"delegation-state/alice","characterMemoryStateDir":"character-memory/alice","sessionProvisioning":"existing-only","defaultConnectionId":"test","\u0069d":"alice"}],"\u0076":10}
        """;

    [Fact]
    public void HandwrittenFullV9AcceptsOrderFreeNestedAndEscapedNames() {
        using var fixture = new RootConfigFixture();

        GalateaConfig config = fixture.Load(ReorderedEscapedFullV10);

        GalateaCharacterConfig user = Assert.Single(config.Characters);
        Assert.Equal("alice", user.CharacterId);
        Assert.Equal(
            GalateaSessionProvisioning.ExistingOnly,
            user.SessionProvisioning
        );
        Assert.Equal("Galatea", user.CharacterName.Value);
        Assert.Equal("刘世超", Assert.Single(config.Players).Name.Value);
        Assert.Equal(
            "inline ${characterName}",
            user.SystemPrompt.JsonValue.GetProperty("instructions")[1].GetProperty("source").GetString()
        );
        Assert.Equal(
            Path.Combine(fixture.Root, "delegation-state", "alice"),
            user.DelegationStateDir
        );
        Assert.Equal(
            Path.Combine(fixture.Root, "character-memory", "alice"),
            user.CharacterMemoryStateDir
        );
        Assert.True(config.MaintenanceMode);
        Assert.Equal(["opaque-listener", "opaque-listener"],
            config.ListenUrls);
        Assert.Equal(
            Path.Combine(fixture.Root, "call-logs"),
            config.CallLogDir
        );
        Assert.Equal(
            Path.Combine(fixture.Root, "routes.json"),
            config.RecapGrid!.RouteManifestPath
        );
        Assert.Equal(
            "test-profile",
            config.RecapGrid.CurrentAgentControlProfileId
        );
    }

    [Fact]
    public void RequiredRootUserAndRecapFieldsRejectMissingNullAndWrongType() {
        using var fixture = new RootConfigFixture();
        (string Scope, string Field)[] required = [
            ("root", "characters"),
            ("root", "players"),
            ("root", "runtime"),
            ("runtime", "recapGrid"),
            ("user", "id"),
            ("user", "name"),
            ("player", "id"),
            ("player", "name"),
            ("player", "password"),
            ("user", "sessionDir"),
            ("user", "delegationStateDir"),
            ("user", "characterMemoryStateDir"),
            ("user", "homeDir"),
            ("user", "defaultConnectionId"),
            ("recap", "routeManifestPath"),
            ("recap", "agentControlProfileFiles"),
            ("recap", "currentAgentControlProfileId")
        ];

        foreach ((string scope, string field) in required) {
            Exception missing = Assert.ThrowsAny<Exception>(() =>
                fixture.Load(MutateRequired(
                    scope,
                    field,
                    RequiredMutation.Remove
                ))
            );
            if (scope != "recap" && !(scope == "runtime" && field == "recapGrid")) {
                Assert.IsType<InvalidDataException>(missing);
            }
            else {
                Assert.IsType<InvalidOperationException>(missing);
            }
            Assert.Throws<InvalidDataException>(() => fixture.Load(
                MutateRequired(scope, field, RequiredMutation.Null)
            ));
            Assert.Throws<InvalidDataException>(() => fixture.Load(
                MutateRequired(scope, field, RequiredMutation.WrongType)
            ));
        }

        Assert.Throws<InvalidDataException>(() =>
            GalateaStrictConfigReader.ValidateRoot("null"u8));
        Assert.Throws<InvalidDataException>(() =>
            GalateaStrictConfigReader.ValidateRoot("[]"u8));
        Assert.Throws<InvalidDataException>(() =>
            GalateaStrictConfigReader.ValidateRoot("{}"u8));
    }

    [Fact]
    public void SessionProvisioningIsRequiredAndUsesClosedExactTokens() {
        using var fixture = new RootConfigFixture();

        JsonObject missing = ParseRoot(MinimalV10);
        Assert.True(CharacterObject(missing).Remove("sessionProvisioning"));
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            missing.ToJsonString()
        ));

        foreach (JsonNode? invalid in new JsonNode?[] {
                     null,
                     JsonValue.Create(17),
                     JsonValue.Create("Existing-Only"),
                     JsonValue.Create("create_if_missing"),
                     JsonValue.Create("")
                 }) {
            JsonObject root = ParseRoot(MinimalV10);
            CharacterObject(root)["sessionProvisioning"] = invalid?.DeepClone();
            Assert.Throws<InvalidDataException>(() => fixture.Load(
                root.ToJsonString()
            ));
        }

        JsonObject existingOnly = ParseRoot(MinimalV10);
        CharacterObject(existingOnly)["sessionProvisioning"] = "existing-only";
        Assert.Equal(
            GalateaSessionProvisioning.ExistingOnly,
            Assert.Single(fixture.Load(existingOnly.ToJsonString()).Characters)
                .SessionProvisioning
        );
        Assert.Equal(
            GalateaSessionProvisioning.CreateIfMissing,
            Assert.Single(fixture.Load(MinimalV10).Characters).SessionProvisioning
        );
    }

    [Fact]
    public void OptionalRootAndUserFieldsLockMissingNullAndDefaultSemantics() {
        using var fixture = new RootConfigFixture();

        GalateaConfig missing = fixture.Load(MinimalV10);
        Assert.Null(missing.ListenUrls);
        Assert.Null(missing.CallLogDir);
        Assert.False(missing.MaintenanceMode);

        JsonObject explicitValues = ParseRoot(MinimalV10);
        RuntimeObject(explicitValues)["listenUrls"] = null;
        RuntimeObject(explicitValues)["callLogDir"] = null;
        RuntimeObject(explicitValues)["maintenanceMode"] = false;
        CharacterObject(explicitValues)["characterContextTemplateFile"] = null;
        GalateaConfig explicitDefaults = fixture.Load(
            explicitValues.ToJsonString()
        );
        Assert.Null(explicitDefaults.ListenUrls);
        Assert.Null(explicitDefaults.CallLogDir);
        Assert.False(explicitDefaults.MaintenanceMode);

        JsonObject invalidMaintenance = ParseRoot(MinimalV10);
        RuntimeObject(invalidMaintenance)["maintenanceMode"] = null;
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            invalidMaintenance.ToJsonString()
        ));
    }

    [Fact]
    public void UsersCapAndExactUserIdIdentityAreLocked() {
        using var fixture = new RootConfigFixture();

        JsonObject emptyRoot = ParseRoot(MinimalV10);
        emptyRoot["characters"] = new JsonArray();
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            emptyRoot.ToJsonString()
        ));

        GalateaConfig maximum = fixture.Load(ConfigWithCharacters(256));
        Assert.Equal(256, maximum.Characters.Count);
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            ConfigWithCharacters(257)
        ));

        JsonObject duplicate = ParseRoot(MinimalV10);
        duplicate["characters"] = new JsonArray(
            CharacterNode("alice", "sessions/first"),
            CharacterNode("alice", "sessions/second")
        );
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            duplicate.ToJsonString()
        ));

        JsonObject ordinalDistinct = ParseRoot(MinimalV10);
        JsonObject lower = CharacterNode("alice", "sessions/lower");
        lower["name"] = "lower";
        JsonObject upper = CharacterNode("Alice", "sessions/upper");
        upper["name"] = "upper";
        ordinalDistinct["characters"] = new JsonArray(lower, upper);
        GalateaConfig loaded = fixture.Load(ordinalDistinct.ToJsonString());
        Assert.Equal(["alice", "Alice"],
            loaded.Characters.Select(static user => user.CharacterId));
    }

    [Fact]
    public void RequiredUserTextFieldsRejectBlankValues() {
        using var fixture = new RootConfigFixture();

        foreach (string field in new[] {
                     "id",
                     "sessionDir",
                     "delegationStateDir",
                     "characterMemoryStateDir"
                 }) {
            JsonObject root = ParseRoot(MinimalV10);
            CharacterObject(root)[field] = " \t ";
            Assert.Throws<InvalidOperationException>(() => fixture.Load(
                root.ToJsonString()
            ));
        }
    }

    [Fact]
    public void CharacterNameLanguageIsCanonicalAndBounded() {
        using var fixture = new RootConfigFixture();

        foreach (string invalid in new[] {
                     string.Empty,
                     " Galatea",
                     "Galatea ",
                     "e\u0301",
                     "Gala\u0001tea",
                     "Gala\u2028tea",
                     "Gala\u202Etea",
                     "Gala\u2066tea",
                     "Gala\u2069tea",
                     "\u200D",
                     "[Galatea]",
                     "Gala$tea",
                     "Gala{tea",
                     "Gala}tea",
                     "旁白",
                     "状态摘要",
                     "角色名",
                     new string('a', 129)
                 }) {
            JsonObject root = ParseRoot(MinimalV10);
            CharacterObject(root)["name"] = invalid;
            Assert.Throws<InvalidOperationException>(() => fixture.Load(
                root.ToJsonString()
            ));
        }

        foreach (string valid in new[] {
                     "👩‍🚀",
                     new string('a', 128)
                 }) {
            JsonObject root = ParseRoot(MinimalV10);
            CharacterObject(root)["name"] = valid;
            GalateaCharacterConfig user = Assert.Single(
                fixture.Load(root.ToJsonString()).Characters
            );
            Assert.Equal(valid, user.CharacterName.Value);
            Assert.Equal(
                "inline ${characterName}",
                user.SystemPrompt.JsonValue.GetProperty("instructions")[1].GetProperty("source").GetString()
            );
        }
    }

    [Fact]
    public void CharacterRecipientDirectoryRequiresOrdinalUniqueNonCodexNamesAndRendersPeers() {
        using var fixture = new RootConfigFixture();
        JsonObject root = ParseRoot(MinimalV10);
        JsonObject alice = CharacterNode("alice", "sessions/alice");
        alice["name"] = "Alice";
        JsonObject bob = CharacterNode("bob", "sessions/bob");
        bob["name"] = "Bob";
        root["characters"] = new JsonArray(alice, bob);

        GalateaConfig loaded = fixture.Load(root.ToJsonString());

        GalateaCharacterRecipientDirectory directory = Assert.IsType<
            GalateaCharacterRecipientDirectory>(
            loaded.CharacterRecipientDirectory
        );
        Assert.True(directory.TryGetExact("Alice", out var aliceRecipient));
        Assert.Equal("alice", aliceRecipient.CharacterId);
        Assert.Equal(
            GalateaDelegationSupervisor.CreateSessionRepositoryId(
                loaded.Characters[0].SessionDir
            ),
            aliceRecipient.SessionRepositoryId
        );
        Assert.False(directory.TryGetExact("alice", out _));
        Assert.Equal(["Bob"], directory.GetPeerNames("alice")
            .Select(static name => name.Value));
        Assert.Equal(["Alice"], directory.GetPeerNames("bob")
            .Select(static name => name.Value));
        Assert.True(loaded.Characters[0].SystemPrompt.IsStructured);
        JsonElement peer = Assert.Single(loaded.Characters[0].SystemPrompt.JsonValue
            .GetProperty("bindings").GetProperty("characterPeers").EnumerateArray());
        Assert.Equal("character", peer.GetProperty("kind").GetString());
        Assert.Equal("bob", peer.GetProperty("id").GetString());
        Assert.Equal("Bob", peer.GetProperty("name").GetString());

        bob["name"] = "Alice";
        InvalidOperationException duplicate = Assert.Throws<
            InvalidOperationException>(() => fixture.Load(root.ToJsonString()));
        Assert.Contains("duplicate characterName 'Alice'", duplicate.Message,
            StringComparison.Ordinal);

        bob["name"] = "Codex";
        InvalidOperationException reserved = Assert.Throws<
            InvalidOperationException>(() => fixture.Load(root.ToJsonString()));
        Assert.Contains("characterName 'Codex' is reserved", reserved.Message,
            StringComparison.Ordinal);

        bob["name"] = "alice";
        Assert.Equal(2, fixture.Load(root.ToJsonString())
            .CharacterRecipientDirectory!.Recipients.Count);
    }

    [Fact]
    public void PlayerNameLanguageIsCanonicalAndBounded() {
        using var fixture = new RootConfigFixture();

        foreach (string invalid in new[] {
                     string.Empty,
                     " Player",
                     "Player ",
                     "e\u0301",
                     "Play\u0001er",
                     "Play\u2028er",
                     "Play\u202Eer",
                     "\u200D",
                     "[Player]",
                     "Play$er",
                     "Play{er",
                     "Play}er",
                     "旁白",
                     "状态摘要",
                     "角色名",
                     new string('a', 129)
                 }) {
            JsonObject root = ParseRoot(MinimalV10);
            PlayerObject(root)["name"] = invalid;
            Assert.Throws<InvalidOperationException>(() => fixture.Load(
                root.ToJsonString()
            ));
        }

        foreach (string valid in new[] {
                     "🧑‍🚀",
                     new string('a', 128)
                 }) {
            JsonObject root = ParseRoot(MinimalV10);
            PlayerObject(root)["name"] = valid;
            GalateaConfig loaded = fixture.Load(root.ToJsonString());
            Assert.Equal(valid, Assert.Single(loaded.Players).Name.Value);
            Assert.Equal(Assert.Single(fixture.Load(MinimalV10).Characters).SystemPrompt,
                Assert.Single(loaded.Characters).SystemPrompt);
        }
    }

    [Fact]
    public void LegacyPromptFieldsRemainUnknownInV9() {
        using var fixture = new RootConfigFixture();

        foreach (string oldField in new[] {
                     "systemPrompt",
                     "systemPromptFile",
                     "systemPromptTemplate",
                     "systemPromptTemplateFile"
                 }) {
            JsonObject root = ParseRoot(MinimalV10);
            CharacterObject(root)[oldField] = "legacy";
            Assert.Throws<InvalidDataException>(() => fixture.Load(
                root.ToJsonString()
            ));
        }
    }

    [Fact]
    public void ListenUrlsCapAllowsDuplicateOpaqueNonblankValues() {
        using var fixture = new RootConfigFixture();

        JsonObject emptyRoot = ParseRoot(MinimalV10);
        RuntimeObject(emptyRoot)["listenUrls"] = new JsonArray();
        Assert.Empty(fixture.Load(emptyRoot.ToJsonString()).ListenUrls!);

        JsonObject maximumRoot = ParseRoot(MinimalV10);
        RuntimeObject(maximumRoot)["listenUrls"] = StringArray(
            Enumerable.Repeat("opaque-listener", 256)
        );
        GalateaConfig maximum = fixture.Load(maximumRoot.ToJsonString());
        Assert.Equal(256, maximum.ListenUrls!.Count);
        Assert.All(maximum.ListenUrls,
            static value => Assert.Equal("opaque-listener", value));

        JsonObject overRoot = ParseRoot(MinimalV10);
        RuntimeObject(overRoot)["listenUrls"] = StringArray(
            Enumerable.Repeat("opaque-listener", 257)
        );
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            overRoot.ToJsonString()
        ));

        JsonObject blankRoot = ParseRoot(MinimalV10);
        RuntimeObject(blankRoot)["listenUrls"] = new JsonArray(" ");
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            blankRoot.ToJsonString()
        ));

        JsonObject nullItemRoot = ParseRoot(MinimalV10);
        var nullItem = new JsonArray();
        nullItem.Add((JsonNode?)null);
        RuntimeObject(nullItemRoot)["listenUrls"] = nullItem;
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            nullItemRoot.ToJsonString()
        ));

        JsonObject nonStringRoot = ParseRoot(MinimalV10);
        var nonString = new JsonArray();
        nonString.Add(17);
        RuntimeObject(nonStringRoot)["listenUrls"] = nonString;
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            nonStringRoot.ToJsonString()
        ));
    }

    [Fact]
    public void ProfileFileCountAndResolvedIdentityAreExact() {
        using var fixture = new RootConfigFixture();
        JsonObject one = ParseRoot(MinimalV10);
        Assert.Equal(
            "test-profile",
            fixture.Load(one.ToJsonString()).RecapGrid!
                .CurrentAgentControlProfileId
        );

        JsonObject zero = ParseRoot(MinimalV10);
        RecapObject(zero)["agentControlProfileFiles"] = new JsonArray();
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            zero.ToJsonString()
        ));

        JsonObject missingPath = ParseRoot(MinimalV10);
        RecapObject(missingPath)["agentControlProfileFiles"] =
            new JsonArray("missing-profile.json");
        Assert.Throws<FileNotFoundException>(() => fixture.Load(
            missingPath.ToJsonString()
        ));

        var paths = new List<string>(256);
        for (int index = 0; index < 256; index++) {
            string relative = $"profiles/profile-{index:D3}.json";
            fixture.WriteProfile(
                relative,
                $"profile-{index:D3}",
                identityDiscriminator: index
            );
            paths.Add(relative);
        }
        JsonObject maximumRoot = ParseRoot(MinimalV10);
        JsonObject maximumRecap = RecapObject(maximumRoot);
        maximumRecap["agentControlProfileFiles"] = StringArray(paths);
        maximumRecap["currentAgentControlProfileId"] = "profile-000";
        GalateaConfig maximum = fixture.Load(maximumRoot.ToJsonString());
        Assert.Equal(
            "profile-000",
            maximum.RecapGrid!.CurrentAgentControlProfileId
        );

        JsonObject overRoot = ParseRoot(MinimalV10);
        JsonObject overRecap = RecapObject(overRoot);
        overRecap["agentControlProfileFiles"] = StringArray(
            paths.Append("profiles/not-read-257.json")
        );
        overRecap["currentAgentControlProfileId"] = "profile-000";
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            overRoot.ToJsonString()
        ));

        JsonObject duplicateRoot = ParseRoot(MinimalV10);
        RecapObject(duplicateRoot)["agentControlProfileFiles"] =
            new JsonArray("profile.json", "./profile.json");
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            duplicateRoot.ToJsonString()
        ));
    }

    [Fact]
    public void ProfileRegistryRejectsDuplicateProfileAndRuntimeIdentities() {
        using var fixture = new RootConfigFixture();

        fixture.WriteProfile(
            "registry/duplicate-profile-a.json",
            "duplicate-profile",
            identityDiscriminator: 1
        );
        fixture.WriteProfile(
            "registry/duplicate-profile-b.json",
            "duplicate-profile",
            identityDiscriminator: 2
        );
        JsonObject duplicateProfileRoot = ParseRoot(MinimalV10);
        JsonObject duplicateProfileRecap = RecapObject(duplicateProfileRoot);
        duplicateProfileRecap["agentControlProfileFiles"] = new JsonArray(
            "registry/duplicate-profile-a.json",
            "registry/duplicate-profile-b.json"
        );
        duplicateProfileRecap["currentAgentControlProfileId"] =
            "duplicate-profile";
        Assert.Throws<ArgumentException>(() => fixture.Load(
            duplicateProfileRoot.ToJsonString()
        ));

        fixture.WriteProfile(
            "registry/duplicate-runtime-a.json",
            "runtime-a",
            identityDiscriminator: 3
        );
        fixture.WriteProfile(
            "registry/duplicate-runtime-b.json",
            "runtime-b",
            identityDiscriminator: 3
        );
        JsonObject duplicateRuntimeRoot = ParseRoot(MinimalV10);
        JsonObject duplicateRuntimeRecap = RecapObject(duplicateRuntimeRoot);
        duplicateRuntimeRecap["agentControlProfileFiles"] = new JsonArray(
            "registry/duplicate-runtime-a.json",
            "registry/duplicate-runtime-b.json"
        );
        duplicateRuntimeRecap["currentAgentControlProfileId"] = "runtime-a";
        Assert.Throws<ArgumentException>(() => fixture.Load(
            duplicateRuntimeRoot.ToJsonString()
        ));
    }

    [Fact]
    public void CharacterContextInlineAndFileLanguageIsExact() {
        using var fixture = new RootConfigFixture();

        JsonObject missingInline = ParseRoot(MinimalV10);
        CharacterObject(missingInline).Remove("characterContextTemplate");
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            missingInline.ToJsonString()
        ));

        JsonObject nullInline = ParseRoot(MinimalV10);
        CharacterObject(nullInline)["characterContextTemplate"] = null;
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            nullInline.ToJsonString()
        ));

        JsonObject blankInline = ParseRoot(MinimalV10);
        CharacterObject(blankInline)["characterContextTemplate"] = " \t ";
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            blankInline.ToJsonString()
        ));

        foreach (string invalidTemplate in new[] {
                     "plain prompt",
                     "${CharacterName}",
                     "${characterName} ${other}",
                     "${"
                 }) {
            JsonObject invalid = ParseRoot(MinimalV10);
            CharacterObject(invalid)["characterContextTemplate"] =
                invalidTemplate;
            Assert.Throws<InvalidOperationException>(() => fixture.Load(
                invalid.ToJsonString()
            ));
        }

        foreach (JsonNode? absentFile in new JsonNode?[] {
                     null,
                     JsonValue.Create(""),
                     JsonValue.Create("  ")
                 }) {
            JsonObject noFile = ParseRoot(MinimalV10);
            CharacterObject(noFile)["characterContextTemplateFile"] =
                absentFile?.DeepClone();
            Assert.Equal(
                "inline ${characterName}",
                Assert.Single(fixture.Load(noFile.ToJsonString()).Characters)
                    .SystemPrompt.JsonValue.GetProperty("instructions")[1].GetProperty("source").GetString()
            );
        }
        JsonObject missingFileProperty = ParseRoot(MinimalV10);
        Assert.False(CharacterObject(missingFileProperty)
            .ContainsKey("characterContextTemplateFile"));
        Assert.Equal(
            "inline ${characterName}",
            Assert.Single(fixture.Load(
                missingFileProperty.ToJsonString()
            ).Characters).SystemPrompt.JsonValue.GetProperty("instructions")[1].GetProperty("source").GetString()
        );

        JsonObject absentPath = ConfigWithPromptFile("missing-prompt.txt");
        Assert.Throws<FileNotFoundException>(() => fixture.Load(
            absentPath.ToJsonString()
        ));

        fixture.WriteBytes(
            "prompt.txt",
            " \n file wins ${characterName} \n "u8.ToArray()
        );
        GalateaConfig fileWins = fixture.Load(
            ConfigWithPromptFile("prompt.txt").ToJsonString()
        );
        Assert.Equal(
            "file wins ${characterName}",
            Assert.Single(fileWins.Characters).SystemPrompt.JsonValue.GetProperty("instructions")[1].GetProperty("source").GetString()
        );

        JsonObject missingInlineWithFile = ConfigWithPromptFile("prompt.txt");
        Assert.True(CharacterObject(missingInlineWithFile).Remove(
            "characterContextTemplate"
        ));
        Assert.Equal(
            "file wins ${characterName}",
            Assert.Single(fixture.Load(
                missingInlineWithFile.ToJsonString()
            ).Characters).SystemPrompt.JsonValue.GetProperty("instructions")[1].GetProperty("source").GetString()
        );

        JsonObject blankInlineWithFile = ConfigWithPromptFile("prompt.txt");
        CharacterObject(blankInlineWithFile)["characterContextTemplate"] =
            " \t ";
        Assert.Equal(
            "file wins ${characterName}",
            Assert.Single(fixture.Load(
                blankInlineWithFile.ToJsonString()
            ).Characters).SystemPrompt.JsonValue.GetProperty("instructions")[1].GetProperty("source").GetString()
        );

        JsonObject nullInlineWithFile = ConfigWithPromptFile("prompt.txt");
        CharacterObject(nullInlineWithFile)["characterContextTemplate"] = null;
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            nullInlineWithFile.ToJsonString()
        ));

        fixture.WriteBytes("prompt.txt", []);
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            ConfigWithPromptFile("prompt.txt").ToJsonString()
        ));

        fixture.WriteBytes("prompt.txt", [0x66, 0x6f, 0x80]);
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            ConfigWithPromptFile("prompt.txt").ToJsonString()
        ));
    }

    [Fact]
    public void SharedCharacterContextFileComposesOncePerUserNames() {
        using var fixture = new RootConfigFixture();
        fixture.WriteBytes(
            "shared.md",
            "Hello ${characterName}."u8.ToArray()
        );
        JsonObject root = ParseRoot(MinimalV10);
        JsonObject alice = CharacterNode("alice", "sessions/alice");
        alice["name"] = "Alice";
        alice["characterContextTemplateFile"] = "shared.md";
        JsonObject bob = CharacterNode("bob", "sessions/bob");
        bob["name"] = "鲍勃";
        bob["characterContextTemplateFile"] = "shared.md";
        root["characters"] = new JsonArray(alice, bob);

        GalateaConfig loaded = fixture.Load(root.ToJsonString());

        Assert.Equal(
            [
                "Hello ${characterName}.",
                "Hello ${characterName}."
            ],
            loaded.Characters.Select(static user => user.SystemPrompt.JsonValue.GetProperty("instructions")[1].GetProperty("source").GetString())
        );
        Assert.Equal(["Alice", "鲍勃"], loaded.Characters.Select(static character =>
            character.SystemPrompt.JsonValue.GetProperty("bindings").GetProperty("character").GetProperty("name").GetString()));
    }

    [Fact]
    public void RecapPathsAndCurrentProfileIdLockBlankAndExactMatch() {
        using var fixture = new RootConfigFixture();

        GalateaConfig exact = fixture.Load(MinimalV10);
        Assert.Equal(
            Path.Combine(fixture.Root, "routes.json"),
            exact.RecapGrid!.RouteManifestPath
        );
        Assert.Equal(
            "test-profile",
            exact.RecapGrid.CurrentAgentControlProfileId
        );

        JsonObject blankRoute = ParseRoot(MinimalV10);
        RecapObject(blankRoute)["routeManifestPath"] = "  ";
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            blankRoute.ToJsonString()
        ));

        JsonObject blankProfile = ParseRoot(MinimalV10);
        RecapObject(blankProfile)["agentControlProfileFiles"] =
            new JsonArray(" ");
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            blankProfile.ToJsonString()
        ));

        JsonObject blankCurrent = ParseRoot(MinimalV10);
        RecapObject(blankCurrent)["currentAgentControlProfileId"] = " ";
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            blankCurrent.ToJsonString()
        ));

        JsonObject wrongCase = ParseRoot(MinimalV10);
        RecapObject(wrongCase)["currentAgentControlProfileId"] =
            "Test-Profile";
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            wrongCase.ToJsonString()
        ));
    }

    [Fact]
    public void CallLogPathsResolveRelativeAndAbsoluteAndRemainDisjoint() {
        using var fixture = new RootConfigFixture();

        JsonObject relativeRoot = ParseRoot(MinimalV10);
        RuntimeObject(relativeRoot)["callLogDir"] = "call-logs";
        Assert.Equal(
            Path.Combine(fixture.Root, "call-logs"),
            fixture.Load(relativeRoot.ToJsonString()).CallLogDir
        );

        string absolute = Path.Combine(fixture.Root, "absolute-logs");
        JsonObject absoluteRoot = ParseRoot(MinimalV10);
        RuntimeObject(absoluteRoot)["callLogDir"] = absolute;
        Assert.Equal(
            absolute,
            fixture.Load(absoluteRoot.ToJsonString()).CallLogDir
        );

        JsonObject nestedRoot = ParseRoot(MinimalV10);
        RuntimeObject(nestedRoot)["callLogDir"] = "sessions/alice/call-logs";
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            nestedRoot.ToJsonString()
        ));

        JsonObject blankRoot = ParseRoot(MinimalV10);
        RuntimeObject(blankRoot)["callLogDir"] = "  ";
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            blankRoot.ToJsonString()
        ));
    }

    [Fact]
    public void RootBytesRejectBomInvalidUtf8CommentTrailingCommaAndData() {
        using var fixture = new RootConfigFixture();
        byte[] valid = Encoding.UTF8.GetBytes(MinimalV10);
        byte[] bom = [.. Encoding.UTF8.GetPreamble(), .. valid];
        Assert.Throws<InvalidDataException>(() =>
            GalateaStrictConfigReader.ValidateRoot(bom));

        byte[] invalidUtf8 = (byte[])valid.Clone();
        int inline = Encoding.UTF8.GetString(invalidUtf8)
            .IndexOf("inline", StringComparison.Ordinal);
        Assert.True(inline >= 0);
        invalidUtf8[inline] = 0x80;
        InvalidDataException invalidUtf8Failure = Assert.Throws<
            InvalidDataException
        >(
            () => fixture.LoadBytes(invalidUtf8)
        );
        Assert.IsType<JsonException>(invalidUtf8Failure.InnerException);
        Assert.DoesNotContain(
            "characterContextTemplate",
            invalidUtf8Failure.Message
        );

        string comment = MinimalV10.Replace(
            "\"v\":10,",
            "\"v\":10/*comment*/,",
            StringComparison.Ordinal
        );
        Assert.Throws<InvalidDataException>(() =>
            GalateaStrictConfigReader.ValidateRoot(
                Encoding.UTF8.GetBytes(comment)
            ));

        string trailingComma = MinimalV10[..^1] + ",}";
        Assert.Throws<InvalidDataException>(() =>
            GalateaStrictConfigReader.ValidateRoot(
                Encoding.UTF8.GetBytes(trailingComma)
            ));
        Assert.Throws<InvalidDataException>(() =>
            GalateaStrictConfigReader.ValidateRoot(
                Encoding.UTF8.GetBytes(MinimalV10 + "null")
            ));
    }

    private static string ConfigWithCharacters(int count) {
        JsonObject root = ParseRoot(MinimalV10);
        var users = new JsonArray();
        for (int index = 0; index < count; index++) {
            JsonObject user = CharacterNode(
                $"user-{index:D3}",
                $"sessions/user-{index:D3}"
            );
            user["name"] = $"character-{index:D3}";
            users.Add(user);
        }
        root["characters"] = users;
        return root.ToJsonString();
    }

    private static JsonObject ConfigWithPromptFile(string path) {
        JsonObject root = ParseRoot(MinimalV10);
        CharacterObject(root)["characterContextTemplateFile"] = path;
        return root;
    }

    private static JsonObject CharacterNode(string id, string session) => new() {
        ["id"] = id,
        ["homeDir"] = $"/__TEST_HOME__/{id}",
        ["sessionDir"] = session,
        ["delegationStateDir"] = $"delegation-state/{id}",
        ["characterMemoryStateDir"] = $"character-memory/{id}",
        ["sessionProvisioning"] = "existing-only",
        ["defaultConnectionId"] = "test",
        ["name"] = "Galatea",
        ["characterContextTemplate"] = "inline ${characterName}"
    };

    private static JsonArray StringArray(IEnumerable<string> values) {
        var array = new JsonArray();
        foreach (string value in values) { array.Add(value); }
        return array;
    }

    private static string MutateRequired(
        string scope,
        string field,
        RequiredMutation mutation
    ) {
        JsonObject root = ParseRoot(MinimalV10);
        JsonObject target = scope switch {
            "root" => root,
            "user" => CharacterObject(root),
            "player" => PlayerObject(root),
            "runtime" => RuntimeObject(root),
            "recap" => RecapObject(root),
            _ => throw new ArgumentOutOfRangeException(nameof(scope))
        };
        if (mutation == RequiredMutation.Remove) {
            Assert.True(target.Remove(field));
        }
        else {
            target[field] = mutation == RequiredMutation.Null
                ? null
                : JsonValue.Create(17);
        }
        return root.ToJsonString();
    }

    private static JsonObject ParseRoot(string json) =>
        JsonNode.Parse(json)!.AsObject();

    private static JsonObject CharacterObject(JsonObject root) =>
        root["characters"]!.AsArray()[0]!.AsObject();

    private static JsonObject PlayerObject(JsonObject root) => root["players"]!.AsArray()[0]!.AsObject();

    private static JsonObject RuntimeObject(JsonObject root) => root["runtime"]!.AsObject();

    private static JsonObject RecapObject(JsonObject root) =>
        RuntimeObject(root)["recapGrid"]!.AsObject();

    private enum RequiredMutation { Remove, Null, WrongType }

    private sealed class RootConfigFixture : IDisposable {
        internal RootConfigFixture() {
            Root = Path.Combine(
                Path.GetTempPath(),
                "atelia-galatea-root-config-field-language-tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(Root);
            ConfigPath = Path.Combine(Root, "config.json");
            GalateaTestHost.WriteConnectionsFile(
                Path.Combine(Root, GalateaConfigLoader.ConnectionsFileName),
                Connections,
                "test"
            );
            GalateaTestHost.WriteDelegatesFile(Root);
            WriteProfile("profile.json", "test-profile");
        }

        internal string Root { get; }
        private string ConfigPath { get; }

        internal GalateaConfig Load(string json) {
            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(json, "/__TEST_HOME__/([^\"\\\\]+)")) {
                Directory.CreateDirectory(Path.Combine(Root, "homes", match.Groups[1].Value));
            }
            json = json.Replace("/__TEST_HOME__", Path.Combine(Root, "homes"), StringComparison.Ordinal);
            File.WriteAllText(ConfigPath, json);
            return GalateaConfigLoader.Load(ConfigPath);
        }

        internal GalateaConfig LoadBytes(byte[] bytes) {
            File.WriteAllBytes(ConfigPath, bytes);
            return GalateaConfigLoader.Load(ConfigPath);
        }

        internal void WriteProfile(
            string relative,
            string profileId,
            int identityDiscriminator = 64
        ) {
            WriteBytes(
                relative,
                CreateProfile(profileId, identityDiscriminator)
                    .ToCanonicalBytes()
            );
        }

        internal void WriteBytes(string relative, byte[] bytes) {
            string path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        public void Dispose() {
            if (Directory.Exists(Root)) {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private static RecapGridAgentControlProfile CreateProfile(
        string profileId,
        int identityDiscriminator = 64
    ) {
        Assert.True(RecapGridAgentControlBuiltIns
            .TryCreateRegistrationBundle(
                RecapGridAgentControlBuiltIns.MysteryInvestigationV4,
                out RecapGridControlRegistrationBundle? builtIn
            ));
        return RecapGridAgentControlProfile.Create(
            profileId,
            new RecapGridControlAdmission(
                RecapGridControlPermission.All,
                [builtIn!.Families[0].Digest],
                builtIn.Definitions.Select(static value =>
                    value.Capability.CapabilityFingerprint),
                [ContextHeaderCarrier.System],
                ["case."],
                maximumBootstrapRows: identityDiscriminator,
                maximumProjectedCalls: 1_024
            )
        );
    }

    private static readonly CompletionConnectionConfig[] Connections = [
        new(
            "test",
            "openai-chat",
            "model-a",
            "openai-chat/strict",
            "http://localhost:8000/",
            ApiKey: "test-key"
        )
    ];
}
