using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion;
using Atelia.Galatea.Prompts;
using Atelia.SessionJournal;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaRootConfigFieldLanguageTests {
    [Fact]
    public void RuntimePolicySerializerRoundTripsDefaultBootstrapShapeWithoutNullOverride() {
        GalateaRootFileConfig root = JsonSerializer.Deserialize<GalateaRootFileConfig>(MinimalV13, GalateaJson.Options)!;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(root, GalateaJson.Options);
        GalateaStrictConfigReader.ValidateRoot(bytes);
        using JsonDocument document = JsonDocument.Parse(bytes);
        Assert.False(document.RootElement.GetProperty("runtime").TryGetProperty("completionAttemptTimeoutSeconds", out _));
        root = root with { Runtime = root.Runtime with { CompletionAttemptTimeoutSeconds = new Dictionary<string, int> { ["test"] = 60 } } };
        byte[] configured = JsonSerializer.SerializeToUtf8Bytes(root, GalateaJson.Options);
        GalateaStrictConfigReader.ValidateRoot(configured);
        Assert.Equal(60, JsonSerializer.Deserialize<GalateaRootFileConfig>(configured, GalateaJson.Options)!.Runtime.CompletionAttemptTimeoutSeconds!["test"]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1800)]
    [InlineData(86400)]
    public void CompletionDeadlineIsHostOwnedPerExactConnection(int seconds) {
        using var fixture = new RootConfigFixture();
        JsonObject root = ParseRoot(MinimalV13);
        root["runtime"]!["completionAttemptTimeoutSeconds"] = new JsonObject { ["test"] = seconds };
        GalateaConfig config = fixture.Load(root.ToJsonString());
        Assert.Equal(seconds, config.CompletionAttemptTimeoutSeconds!["test"]);
        root["runtime"]!["completionAttemptTimeoutSeconds"] = new JsonObject { ["missing"] = seconds };
        Assert.Throws<InvalidOperationException>(() => fixture.Load(root.ToJsonString()));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"test\":0}")]
    [InlineData("{\"test\":86401}")]
    [InlineData("{\"test\":1.5}")]
    [InlineData("{\"test\":1,\"test\":2}")]
    public void CompletionDeadlineRejectsInvalidPolicy(string value) {
        string root = MinimalV13.Replace("\"runtime\":{", "\"runtime\":{\"completionAttemptTimeoutSeconds\":" + value + ",", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => GalateaStrictConfigReader.ValidateRoot(Encoding.UTF8.GetBytes(root)));
    }

    [Fact]
    public void AutonomyEnrollmentComesOnlyFromCharactersAndAllowsZeroPlayers() {
        using var fixture = new RootConfigFixture();
        JsonObject root = ParseRoot(MinimalV13);
        root["players"] = new JsonArray();
        Assert.Empty(fixture.Load(root.ToJsonString()).AutonomyCharacterIds);
        CharacterObject(root)["autonomyIntervalMinutes"] = 10;
        GalateaConfig config = fixture.Load(root.ToJsonString());
        Assert.Empty(config.Players);
        Assert.Equal(["alice"], config.AutonomyCharacterIds);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)config.AutonomyCharacterIds)[0] = "changed");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(10, true)]
    [InlineData(60, true)]
    [InlineData(525_600, true)]
    public void AutonomyIntervalAcceptsClosedMinuteRange(int minutes, bool enrolled) {
        using var fixture = new RootConfigFixture();
        JsonObject root = ParseRoot(MinimalV13);
        CharacterObject(root)["autonomyIntervalMinutes"] = minutes;

        GalateaConfig config = fixture.Load(root.ToJsonString());
        Assert.Equal(minutes, Assert.Single(config.Characters).AutonomyIntervalMinutes);
        Assert.Equal(enrolled, config.AutonomyCharacterIds.Count == 1);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"true\"")]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("2147483648")]
    [InlineData("[]")]
    public void AutonomyIntervalRejectsNonIntegerValues(string json) {
        JsonObject root = ParseRoot(MinimalV13);
        CharacterObject(root)["autonomyIntervalMinutes"] = JsonNode.Parse(json);
        Assert.Throws<InvalidDataException>(() => GalateaStrictConfigReader.ValidateRoot(Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(525_601)]
    public void AutonomyIntervalRejectsOutOfRangeValues(int value) {
        JsonObject root = ParseRoot(MinimalV13);
        CharacterObject(root)["autonomyIntervalMinutes"] = value;
        Assert.Throws<InvalidDataException>(() => GalateaStrictConfigReader.ValidateRoot(Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    [Fact]
    public void AutonomyIntervalIsRequiredAndOldHeartbeatFieldIsUnknown() {
        JsonObject missing = ParseRoot(MinimalV13);
        Assert.True(CharacterObject(missing).Remove("autonomyIntervalMinutes"));
        Assert.Throws<InvalidDataException>(() => GalateaStrictConfigReader.ValidateRoot(Encoding.UTF8.GetBytes(missing.ToJsonString())));

        JsonObject old = ParseRoot(MinimalV13);
        CharacterObject(old)["heartbeatEnabled"] = true;
        Assert.Throws<InvalidDataException>(() => GalateaStrictConfigReader.ValidateRoot(Encoding.UTF8.GetBytes(old.ToJsonString())));
    }

    [Theory]
    [InlineData("serverAgentUserIds")]
    [InlineData("users")]
    [InlineData("Characters")]
    public void OldOrWrongCaseRootFieldsAreRejected(string field) {
        JsonObject root = ParseRoot(MinimalV13);
        root[field] = new JsonArray();
        Assert.Throws<InvalidDataException>(() => GalateaStrictConfigReader.ValidateRoot(Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    [Fact]
    public void PlayerAndCharacterIdsHaveIndependentNamespaces() {
        using var fixture = new RootConfigFixture();
        JsonObject root = ParseRoot(MinimalV13);
        PlayerObject(root)["id"] = "alice";
        GalateaConfig config = fixture.Load(root.ToJsonString());
        Assert.Equal("alice", Assert.Single(config.Players).PlayerId);
        Assert.Equal("alice", Assert.Single(config.Characters).CharacterId);
        root["players"]!.AsArray().Add(PlayerObject(root).DeepClone());
        Assert.Throws<InvalidOperationException>(() => fixture.Load(root.ToJsonString()));
    }

    private const string MinimalV13 = """
        {"v":13,"characters":[{"id":"alice","name":"Galatea","homeDir":"/__TEST_HOME__/alice","sessionDir":"sessions/alice","delegationStateDir":"delegation-state/alice","characterMemoryStateDir":"character-memory/alice","sessionProvisioning":"create-if-missing","defaultConnectionId":"test","characterContextTemplate":"inline ${characterName}","autonomyIntervalMinutes":0}],"players":[{"id":"player-main","name":"刘世超","password":"pw"}],"runtime":{"recapGrid":{"maintenance":{"connectionId":"test","maximumConcurrency":1,"dispatchTimeoutMilliseconds":900000}}}}
        """;

    private const string ReorderedEscapedFullV13 = """
        {"runtime":{"maintenanceMode":true,"recapGrid":{"maintenance":{"dispatchTimeoutMilliseconds":900000,"maximumConcurrency":1,"connectionId":"test"}},"callLogDir":"call-logs","listenUrls":["opaque-listener","opaque-listener"]},"players":[{"password":"pw","name":"刘世超","id":"player-main"}],"\u0063haracters":[{"characterContextTemplateFile":null,"characterContextTemplate":"inline ${characterName}","name":"Galatea","homeDir":"/__TEST_HOME__/alice","sessionDir":"sessions/alice","delegationStateDir":"delegation-state/alice","characterMemoryStateDir":"character-memory/alice","sessionProvisioning":"existing-only","defaultConnectionId":"test","autonomyIntervalMinutes":10,"\u0069d":"alice"}],"\u0076":13}
        """;

    [Fact]
    public void HandwrittenFullV13AcceptsOrderFreeNestedAndEscapedNames() {
        using var fixture = new RootConfigFixture();

        GalateaConfig config = fixture.Load(ReorderedEscapedFullV13);

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
        Assert.Equal("test", config.RecapGrid!.Maintenance.ConnectionId);
        Assert.Equal(1, config.RecapGrid.Maintenance.MaximumConcurrency);
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
            ("recap", "maintenance"),
        ];

        foreach ((string scope, string field) in required) {
            Exception missing = Assert.ThrowsAny<Exception>(() =>
                fixture.Load(MutateRequired(
                    scope,
                    field,
                    RequiredMutation.Remove
                ))
            );
            if (!(scope == "runtime" && field == "recapGrid")) {
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

        JsonObject missing = ParseRoot(MinimalV13);
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
            JsonObject root = ParseRoot(MinimalV13);
            CharacterObject(root)["sessionProvisioning"] = invalid?.DeepClone();
            Assert.Throws<InvalidDataException>(() => fixture.Load(
                root.ToJsonString()
            ));
        }

        JsonObject existingOnly = ParseRoot(MinimalV13);
        CharacterObject(existingOnly)["sessionProvisioning"] = "existing-only";
        Assert.Equal(
            GalateaSessionProvisioning.ExistingOnly,
            Assert.Single(fixture.Load(existingOnly.ToJsonString()).Characters)
                .SessionProvisioning
        );
        Assert.Equal(
            GalateaSessionProvisioning.CreateIfMissing,
            Assert.Single(fixture.Load(MinimalV13).Characters).SessionProvisioning
        );
    }

    [Fact]
    public void OptionalRootAndUserFieldsLockMissingNullAndDefaultSemantics() {
        using var fixture = new RootConfigFixture();

        GalateaConfig missing = fixture.Load(MinimalV13);
        Assert.Null(missing.ListenUrls);
        Assert.Null(missing.CallLogDir);
        Assert.False(missing.MaintenanceMode);

        JsonObject explicitValues = ParseRoot(MinimalV13);
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

        JsonObject invalidMaintenance = ParseRoot(MinimalV13);
        RuntimeObject(invalidMaintenance)["maintenanceMode"] = null;
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            invalidMaintenance.ToJsonString()
        ));
    }

    [Fact]
    public void UsersCapAndExactUserIdIdentityAreLocked() {
        using var fixture = new RootConfigFixture();

        JsonObject emptyRoot = ParseRoot(MinimalV13);
        emptyRoot["characters"] = new JsonArray();
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            emptyRoot.ToJsonString()
        ));

        GalateaConfig maximum = fixture.Load(ConfigWithCharacters(256));
        Assert.Equal(256, maximum.Characters.Count);
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            ConfigWithCharacters(257)
        ));

        JsonObject duplicate = ParseRoot(MinimalV13);
        duplicate["characters"] = new JsonArray(
            CharacterNode("alice", "sessions/first"),
            CharacterNode("alice", "sessions/second")
        );
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            duplicate.ToJsonString()
        ));

        JsonObject ordinalDistinct = ParseRoot(MinimalV13);
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
            JsonObject root = ParseRoot(MinimalV13);
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
            JsonObject root = ParseRoot(MinimalV13);
            CharacterObject(root)["name"] = invalid;
            Assert.Throws<InvalidOperationException>(() => fixture.Load(
                root.ToJsonString()
            ));
        }

        foreach (string valid in new[] {
                     "👩‍🚀",
                     new string('a', 128)
                 }) {
            JsonObject root = ParseRoot(MinimalV13);
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
        JsonObject root = ParseRoot(MinimalV13);
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
            JsonObject root = ParseRoot(MinimalV13);
            PlayerObject(root)["name"] = invalid;
            Assert.Throws<InvalidOperationException>(() => fixture.Load(
                root.ToJsonString()
            ));
        }

        foreach (string valid in new[] {
                     "🧑‍🚀",
                     new string('a', 128)
                 }) {
            JsonObject root = ParseRoot(MinimalV13);
            PlayerObject(root)["name"] = valid;
            GalateaConfig loaded = fixture.Load(root.ToJsonString());
            Assert.Equal(valid, Assert.Single(loaded.Players).Name.Value);
            Assert.Equal(Assert.Single(fixture.Load(MinimalV13).Characters).SystemPrompt,
                Assert.Single(loaded.Characters).SystemPrompt);
        }
    }

    [Fact]
    public void LegacyPromptFieldsRemainUnknownInV13() {
        using var fixture = new RootConfigFixture();

        foreach (string oldField in new[] {
                     "systemPrompt",
                     "systemPromptFile",
                     "systemPromptTemplate",
                     "systemPromptTemplateFile"
                 }) {
            JsonObject root = ParseRoot(MinimalV13);
            CharacterObject(root)[oldField] = "legacy";
            Assert.Throws<InvalidDataException>(() => fixture.Load(
                root.ToJsonString()
            ));
        }
    }

    [Fact]
    public void ListenUrlsCapAllowsDuplicateOpaqueNonblankValues() {
        using var fixture = new RootConfigFixture();

        JsonObject emptyRoot = ParseRoot(MinimalV13);
        RuntimeObject(emptyRoot)["listenUrls"] = new JsonArray();
        Assert.Empty(fixture.Load(emptyRoot.ToJsonString()).ListenUrls!);

        JsonObject maximumRoot = ParseRoot(MinimalV13);
        RuntimeObject(maximumRoot)["listenUrls"] = StringArray(
            Enumerable.Repeat("opaque-listener", 256)
        );
        GalateaConfig maximum = fixture.Load(maximumRoot.ToJsonString());
        Assert.Equal(256, maximum.ListenUrls!.Count);
        Assert.All(maximum.ListenUrls,
            static value => Assert.Equal("opaque-listener", value));

        JsonObject overRoot = ParseRoot(MinimalV13);
        RuntimeObject(overRoot)["listenUrls"] = StringArray(
            Enumerable.Repeat("opaque-listener", 257)
        );
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            overRoot.ToJsonString()
        ));

        JsonObject blankRoot = ParseRoot(MinimalV13);
        RuntimeObject(blankRoot)["listenUrls"] = new JsonArray(" ");
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            blankRoot.ToJsonString()
        ));

        JsonObject nullItemRoot = ParseRoot(MinimalV13);
        var nullItem = new JsonArray();
        nullItem.Add((JsonNode?)null);
        RuntimeObject(nullItemRoot)["listenUrls"] = nullItem;
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            nullItemRoot.ToJsonString()
        ));

        JsonObject nonStringRoot = ParseRoot(MinimalV13);
        var nonString = new JsonArray();
        nonString.Add(17);
        RuntimeObject(nonStringRoot)["listenUrls"] = nonString;
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            nonStringRoot.ToJsonString()
        ));
    }

    [Fact]
    public void CharacterContextInlineAndFileLanguageIsExact() {
        using var fixture = new RootConfigFixture();

        JsonObject missingInline = ParseRoot(MinimalV13);
        CharacterObject(missingInline).Remove("characterContextTemplate");
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            missingInline.ToJsonString()
        ));

        JsonObject nullInline = ParseRoot(MinimalV13);
        CharacterObject(nullInline)["characterContextTemplate"] = null;
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            nullInline.ToJsonString()
        ));

        JsonObject blankInline = ParseRoot(MinimalV13);
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
            JsonObject invalid = ParseRoot(MinimalV13);
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
            JsonObject noFile = ParseRoot(MinimalV13);
            CharacterObject(noFile)["characterContextTemplateFile"] =
                absentFile?.DeepClone();
            Assert.Equal(
                "inline ${characterName}",
                Assert.Single(fixture.Load(noFile.ToJsonString()).Characters)
                    .SystemPrompt.JsonValue.GetProperty("instructions")[1].GetProperty("source").GetString()
            );
        }
        JsonObject missingFileProperty = ParseRoot(MinimalV13);
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
        JsonObject root = ParseRoot(MinimalV13);
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
    public void RecapMaintenanceLocksExactShape() {
        using var fixture = new RootConfigFixture();

        GalateaConfig exact = fixture.Load(MinimalV13);
        Assert.Equal("test", exact.RecapGrid!.Maintenance.ConnectionId);
        Assert.Equal(1, exact.RecapGrid.Maintenance.MaximumConcurrency);
        Assert.Equal(TimeSpan.FromMilliseconds(900_000),
            exact.RecapGrid.Maintenance.DispatchTimeout);

        JsonObject blankConnection = ParseRoot(MinimalV13);
        RecapObject(blankConnection)["maintenance"]!["connectionId"] = "  ";
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            blankConnection.ToJsonString()
        ));

        JsonObject badConcurrency = ParseRoot(MinimalV13);
        RecapObject(badConcurrency)["maintenance"]!["maximumConcurrency"] = 0;
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            badConcurrency.ToJsonString()));
    }

    [Fact]
    public void CallLogPathsResolveRelativeAndAbsoluteAndRemainDisjoint() {
        using var fixture = new RootConfigFixture();

        JsonObject relativeRoot = ParseRoot(MinimalV13);
        RuntimeObject(relativeRoot)["callLogDir"] = "call-logs";
        Assert.Equal(
            Path.Combine(fixture.Root, "call-logs"),
            fixture.Load(relativeRoot.ToJsonString()).CallLogDir
        );

        string absolute = Path.Combine(fixture.Root, "absolute-logs");
        JsonObject absoluteRoot = ParseRoot(MinimalV13);
        RuntimeObject(absoluteRoot)["callLogDir"] = absolute;
        Assert.Equal(
            absolute,
            fixture.Load(absoluteRoot.ToJsonString()).CallLogDir
        );

        JsonObject nestedRoot = ParseRoot(MinimalV13);
        RuntimeObject(nestedRoot)["callLogDir"] = "sessions/alice/call-logs";
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            nestedRoot.ToJsonString()
        ));

        JsonObject blankRoot = ParseRoot(MinimalV13);
        RuntimeObject(blankRoot)["callLogDir"] = "  ";
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            blankRoot.ToJsonString()
        ));
    }

    [Fact]
    public void RootBytesRejectBomInvalidUtf8CommentTrailingCommaAndData() {
        using var fixture = new RootConfigFixture();
        byte[] valid = Encoding.UTF8.GetBytes(MinimalV13);
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

        string comment = MinimalV13.Replace(
            "\"v\":13,",
            "\"v\":13/*comment*/,",
            StringComparison.Ordinal
        );
        Assert.Throws<InvalidDataException>(() =>
            GalateaStrictConfigReader.ValidateRoot(
                Encoding.UTF8.GetBytes(comment)
            ));

        string trailingComma = MinimalV13[..^1] + ",}";
        Assert.Throws<InvalidDataException>(() =>
            GalateaStrictConfigReader.ValidateRoot(
                Encoding.UTF8.GetBytes(trailingComma)
            ));
        Assert.Throws<InvalidDataException>(() =>
            GalateaStrictConfigReader.ValidateRoot(
                Encoding.UTF8.GetBytes(MinimalV13 + "null")
            ));
    }

    private static string ConfigWithCharacters(int count) {
        JsonObject root = ParseRoot(MinimalV13);
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
        JsonObject root = ParseRoot(MinimalV13);
        CharacterObject(root)["characterContextTemplateFile"] = path;
        return root;
    }

    private static JsonObject CharacterNode(string id, string session) => new() {
        ["id"] = id,
        ["homeDir"] = $"/__TEST_HOME__/{id}",
        ["sessionDir"] = session,
        ["autonomyIntervalMinutes"] = 0,
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
        JsonObject root = ParseRoot(MinimalV13);
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
