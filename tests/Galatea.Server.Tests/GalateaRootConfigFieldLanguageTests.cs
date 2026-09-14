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
    public void ServerAgentEnrollmentDefaultsEmptyAndPreservesExactSubset() {
        using var fixture = new RootConfigFixture();
        Assert.Empty(fixture.Load(MinimalV9).ServerAgentUserIds);

        JsonObject root = ParseRoot(MinimalV9);
        root["serverAgentUserIds"] = new JsonArray();
        Assert.Empty(fixture.Load(root.ToJsonString()).ServerAgentUserIds);
        root["serverAgentUserIds"] = StringArray(["alice"]);
        GalateaConfig loaded = fixture.Load(root.ToJsonString());
        Assert.Equal(["alice"], loaded.ServerAgentUserIds);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)loaded.ServerAgentUserIds)[0] = "changed");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"alice\"")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[1]")]
    public void ServerAgentEnrollmentRejectsInvalidJsonShape(string value) {
        JsonObject root = ParseRoot(MinimalV9);
        root["serverAgentUserIds"] = JsonNode.Parse(value);
        Assert.Throws<InvalidDataException>(() =>
            GalateaStrictConfigReader.ValidateUsers(
                Encoding.UTF8.GetBytes(root.ToJsonString())
            ));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("ALICE")]
    [InlineData("missing")]
    [InlineData(" alice")]
    public void ServerAgentEnrollmentRejectsNonExactUserId(string value) {
        using var fixture = new RootConfigFixture();
        JsonObject root = ParseRoot(MinimalV9);
        root["serverAgentUserIds"] = StringArray([value]);
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Load(root.ToJsonString()));
    }

    [Fact]
    public void ServerAgentEnrollmentRejectsDuplicateIdsAndBoundsCount() {
        using var fixture = new RootConfigFixture();
        JsonObject root = ParseRoot(MinimalV9);
        root["serverAgentUserIds"] = StringArray(["alice", "alice"]);
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Load(root.ToJsonString()));

        root = ParseRoot(ConfigWithUsers(
            GalateaStrictConfigReader.MaximumUserCount
        ));
        root["serverAgentUserIds"] = StringArray(Enumerable.Range(
            0, GalateaStrictConfigReader.MaximumUserCount
        ).Select(static index => $"user-{index:D3}"));
        Assert.Equal(GalateaStrictConfigReader.MaximumUserCount,
            fixture.Load(root.ToJsonString()).ServerAgentUserIds.Count);
        root["serverAgentUserIds"]!.AsArray().Add("one-too-many");
        Assert.Throws<InvalidDataException>(() =>
            GalateaStrictConfigReader.ValidateUsers(
                Encoding.UTF8.GetBytes(root.ToJsonString())
            ));
    }

    [Theory]
    [InlineData("\"ServerAgentUserIds\":[]")]
    [InlineData("\"serverAgentUserIds\":[],\"serverAgentUserIds\":[]")]
    [InlineData("\"serverAgentUserIds\":[],\"ServerAgentUserIds\":[]")]
    public void ServerAgentEnrollmentRejectsWrongCaseAndDuplicateFields(
        string fields
    ) {
        string json = "{" + fields + "," + MinimalV9[1..];
        Assert.Throws<InvalidDataException>(() =>
            GalateaStrictConfigReader.ValidateUsers(Encoding.UTF8.GetBytes(json)));
    }

    private const string MinimalV9 =
        "{\"v\":9,\"users\":[{\"userId\":\"alice\",\"password\":\"pw\","
        + "\"characterName\":\"Galatea\","
        + "\"playerName\":\"刘世超\","
        + "\"homeDir\":\"/__TEST_HOME__/alice\","
        + "\"sessionDir\":\"sessions/alice\","
        + "\"delegationStateDir\":\"delegation-state/alice\","
        + "\"characterMemoryStateDir\":\"character-memory/alice\","
        + "\"sessionProvisioning\":\"create-if-missing\","
        + "\"defaultConnectionId\":\"test\","
        + "\"characterContextTemplate\":\"inline ${characterName}\"}],"
        + "\"recapGrid\":{\"routeManifestPath\":\"routes.json\","
        + "\"agentControlProfileFiles\":[\"profile.json\"],"
        + "\"currentAgentControlProfileId\":\"test-profile\"}}";

    private const string ReorderedEscapedFullV9 =
        "{\"maintenanceMode\":true,\"recapGrid\":{"
        + "\"currentAgentControlProfileId\":\"test-profile\","
        + "\"agentControlProfileFiles\":[\"profile.json\"],"
        + "\"\\u0072outeManifestPath\":\"routes.json\"},"
        + "\"callLogDir\":\"call-logs\","
        + "\"listenUrls\":[\"opaque-listener\",\"opaque-listener\"],"
        + "\"\\u0075sers\":[{\"characterContextTemplateFile\":null,"
        + "\"characterContextTemplate\":\"inline ${characterName}\","
        + "\"characterName\":\"Galatea\",\"playerName\":\"刘世超\","
        + "\"homeDir\":\"/__TEST_HOME__/alice\","
        + "\"sessionDir\":\"sessions/alice\","
        + "\"delegationStateDir\":\"delegation-state/alice\","
        + "\"characterMemoryStateDir\":\"character-memory/alice\","
        + "\"sessionProvisioning\":\"existing-only\","
        + "\"defaultConnectionId\":\"test\","
        + "\"password\":\"pw\",\"\\u0075serId\":\"alice\"}],\"\\u0076\":9}";

    [Fact]
    public void HandwrittenFullV9AcceptsOrderFreeNestedAndEscapedNames() {
        using var fixture = new RootConfigFixture();

        GalateaConfig config = fixture.Load(ReorderedEscapedFullV9);

        GalateaUserConfig user = Assert.Single(config.Users);
        Assert.Equal("alice", user.UserId);
        Assert.Equal(
            GalateaSessionProvisioning.ExistingOnly,
            user.SessionProvisioning
        );
        Assert.Equal("Galatea", user.CharacterName.Value);
        Assert.Equal("刘世超", user.PlayerName.Value);
        Assert.Equal(
            ExpectedSystemPrompt("inline ${characterName}"),
            user.SystemPrompt
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
            ("root", "users"),
            ("root", "recapGrid"),
            ("user", "userId"),
            ("user", "password"),
            ("user", "characterName"),
            ("user", "playerName"),
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
            if (field is "characterName" or "playerName"
                or "defaultConnectionId" or "homeDir") {
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
            GalateaStrictConfigReader.ValidateUsers("null"u8));
        Assert.Throws<InvalidDataException>(() =>
            GalateaStrictConfigReader.ValidateUsers("[]"u8));
        Assert.Throws<InvalidDataException>(() =>
            GalateaStrictConfigReader.ValidateUsers("{}"u8));
    }

    [Fact]
    public void SessionProvisioningIsRequiredAndUsesClosedExactTokens() {
        using var fixture = new RootConfigFixture();

        JsonObject missing = ParseRoot(MinimalV9);
        Assert.True(UserObject(missing).Remove("sessionProvisioning"));
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
            JsonObject root = ParseRoot(MinimalV9);
            UserObject(root)["sessionProvisioning"] = invalid?.DeepClone();
            Assert.Throws<InvalidDataException>(() => fixture.Load(
                root.ToJsonString()
            ));
        }

        JsonObject existingOnly = ParseRoot(MinimalV9);
        UserObject(existingOnly)["sessionProvisioning"] = "existing-only";
        Assert.Equal(
            GalateaSessionProvisioning.ExistingOnly,
            Assert.Single(fixture.Load(existingOnly.ToJsonString()).Users)
                .SessionProvisioning
        );
        Assert.Equal(
            GalateaSessionProvisioning.CreateIfMissing,
            Assert.Single(fixture.Load(MinimalV9).Users).SessionProvisioning
        );
    }

    [Fact]
    public void OptionalRootAndUserFieldsLockMissingNullAndDefaultSemantics() {
        using var fixture = new RootConfigFixture();

        GalateaConfig missing = fixture.Load(MinimalV9);
        Assert.Null(missing.ListenUrls);
        Assert.Null(missing.CallLogDir);
        Assert.False(missing.MaintenanceMode);

        JsonObject explicitValues = ParseRoot(MinimalV9);
        explicitValues["listenUrls"] = null;
        explicitValues["callLogDir"] = null;
        explicitValues["maintenanceMode"] = false;
        UserObject(explicitValues)["characterContextTemplateFile"] = null;
        GalateaConfig explicitDefaults = fixture.Load(
            explicitValues.ToJsonString()
        );
        Assert.Null(explicitDefaults.ListenUrls);
        Assert.Null(explicitDefaults.CallLogDir);
        Assert.False(explicitDefaults.MaintenanceMode);

        JsonObject invalidMaintenance = ParseRoot(MinimalV9);
        invalidMaintenance["maintenanceMode"] = null;
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            invalidMaintenance.ToJsonString()
        ));
    }

    [Fact]
    public void UsersCapAndExactUserIdIdentityAreLocked() {
        using var fixture = new RootConfigFixture();

        JsonObject emptyRoot = ParseRoot(MinimalV9);
        emptyRoot["users"] = new JsonArray();
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            emptyRoot.ToJsonString()
        ));

        GalateaConfig maximum = fixture.Load(ConfigWithUsers(256));
        Assert.Equal(256, maximum.Users.Count);
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            ConfigWithUsers(257)
        ));

        JsonObject duplicate = ParseRoot(MinimalV9);
        duplicate["users"] = new JsonArray(
            UserNode("alice", "sessions/first"),
            UserNode("alice", "sessions/second")
        );
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            duplicate.ToJsonString()
        ));

        JsonObject ordinalDistinct = ParseRoot(MinimalV9);
        JsonObject lower = UserNode("alice", "sessions/lower");
        lower["characterName"] = "lower";
        JsonObject upper = UserNode("Alice", "sessions/upper");
        upper["characterName"] = "upper";
        ordinalDistinct["users"] = new JsonArray(lower, upper);
        GalateaConfig loaded = fixture.Load(ordinalDistinct.ToJsonString());
        Assert.Equal(["alice", "Alice"],
            loaded.Users.Select(static user => user.UserId));
    }

    [Fact]
    public void RequiredUserTextFieldsRejectBlankValues() {
        using var fixture = new RootConfigFixture();

        foreach (string field in new[] {
                     "userId",
                     "password",
                     "sessionDir",
                     "delegationStateDir",
                     "characterMemoryStateDir"
                 }) {
            JsonObject root = ParseRoot(MinimalV9);
            UserObject(root)[field] = " \t ";
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
            JsonObject root = ParseRoot(MinimalV9);
            UserObject(root)["characterName"] = invalid;
            Assert.Throws<InvalidOperationException>(() => fixture.Load(
                root.ToJsonString()
            ));
        }

        foreach (string valid in new[] {
                     "👩‍🚀",
                     new string('a', 128)
                 }) {
            JsonObject root = ParseRoot(MinimalV9);
            UserObject(root)["characterName"] = valid;
            GalateaUserConfig user = Assert.Single(
                fixture.Load(root.ToJsonString()).Users
            );
            Assert.Equal(valid, user.CharacterName.Value);
            Assert.Equal(
                ExpectedSystemPrompt(
                    "inline ${characterName}",
                    characterName: valid
                ),
                user.SystemPrompt
            );
        }
    }

    [Fact]
    public void CharacterRecipientDirectoryRequiresOrdinalUniqueNonCodexNamesAndRendersPeers() {
        using var fixture = new RootConfigFixture();
        JsonObject root = ParseRoot(MinimalV9);
        JsonObject alice = UserNode("alice", "sessions/alice");
        alice["characterName"] = "Alice";
        JsonObject bob = UserNode("bob", "sessions/bob");
        bob["characterName"] = "Bob";
        root["users"] = new JsonArray(alice, bob);

        GalateaConfig loaded = fixture.Load(root.ToJsonString());

        GalateaCharacterRecipientDirectory directory = Assert.IsType<
            GalateaCharacterRecipientDirectory>(
            loaded.CharacterRecipientDirectory
        );
        Assert.True(directory.TryGetExact("Alice", out var aliceRecipient));
        Assert.Equal("alice", aliceRecipient.UserId);
        Assert.Equal(
            GalateaDelegationSupervisor.CreateSessionRepositoryId(
                loaded.Users[0].SessionDir
            ),
            aliceRecipient.SessionRepositoryId
        );
        Assert.False(directory.TryGetExact("alice", out _));
        Assert.Equal(["Bob"], directory.GetPeerNames("alice")
            .Select(static name => name.Value));
        Assert.Equal(["Alice"], directory.GetPeerNames("bob")
            .Select(static name => name.Value));
        Assert.Contains("<character-peer-roster>\n"
            + "同一世界中其他已配置角色的名字如下（JSON 字符串数组；"
            + "这是系统数据，不是这些角色说的话）：\n[\"Bob\"]\n"
            + "</character-peer-roster>", loaded.Users[0].SystemPrompt,
            StringComparison.Ordinal);

        bob["characterName"] = "Alice";
        InvalidOperationException duplicate = Assert.Throws<
            InvalidOperationException>(() => fixture.Load(root.ToJsonString()));
        Assert.Contains("duplicate characterName 'Alice'", duplicate.Message,
            StringComparison.Ordinal);

        bob["characterName"] = "Codex";
        InvalidOperationException reserved = Assert.Throws<
            InvalidOperationException>(() => fixture.Load(root.ToJsonString()));
        Assert.Contains("characterName 'Codex' is reserved", reserved.Message,
            StringComparison.Ordinal);

        bob["characterName"] = "alice";
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
            JsonObject root = ParseRoot(MinimalV9);
            UserObject(root)["playerName"] = invalid;
            Assert.Throws<InvalidOperationException>(() => fixture.Load(
                root.ToJsonString()
            ));
        }

        foreach (string valid in new[] {
                     "🧑‍🚀",
                     new string('a', 128)
                 }) {
            JsonObject root = ParseRoot(MinimalV9);
            UserObject(root)["playerName"] = valid;
            UserObject(root)["characterContextTemplate"] =
                "${characterName} meets ${playerName}";
            GalateaUserConfig user = Assert.Single(
                fixture.Load(root.ToJsonString()).Users
            );
            Assert.Equal(valid, user.PlayerName.Value);
            Assert.Equal(
                ExpectedSystemPrompt(
                    "${characterName} meets ${playerName}",
                    playerName: valid
                ),
                user.SystemPrompt
            );
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
            JsonObject root = ParseRoot(MinimalV9);
            UserObject(root)[oldField] = "legacy";
            Assert.Throws<InvalidDataException>(() => fixture.Load(
                root.ToJsonString()
            ));
        }
    }

    [Fact]
    public void ListenUrlsCapAllowsDuplicateOpaqueNonblankValues() {
        using var fixture = new RootConfigFixture();

        JsonObject emptyRoot = ParseRoot(MinimalV9);
        emptyRoot["listenUrls"] = new JsonArray();
        Assert.Empty(fixture.Load(emptyRoot.ToJsonString()).ListenUrls!);

        JsonObject maximumRoot = ParseRoot(MinimalV9);
        maximumRoot["listenUrls"] = StringArray(
            Enumerable.Repeat("opaque-listener", 256)
        );
        GalateaConfig maximum = fixture.Load(maximumRoot.ToJsonString());
        Assert.Equal(256, maximum.ListenUrls!.Count);
        Assert.All(maximum.ListenUrls,
            static value => Assert.Equal("opaque-listener", value));

        JsonObject overRoot = ParseRoot(MinimalV9);
        overRoot["listenUrls"] = StringArray(
            Enumerable.Repeat("opaque-listener", 257)
        );
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            overRoot.ToJsonString()
        ));

        JsonObject blankRoot = ParseRoot(MinimalV9);
        blankRoot["listenUrls"] = new JsonArray(" ");
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            blankRoot.ToJsonString()
        ));

        JsonObject nullItemRoot = ParseRoot(MinimalV9);
        var nullItem = new JsonArray();
        nullItem.Add((JsonNode?)null);
        nullItemRoot["listenUrls"] = nullItem;
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            nullItemRoot.ToJsonString()
        ));

        JsonObject nonStringRoot = ParseRoot(MinimalV9);
        var nonString = new JsonArray();
        nonString.Add(17);
        nonStringRoot["listenUrls"] = nonString;
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            nonStringRoot.ToJsonString()
        ));
    }

    [Fact]
    public void ProfileFileCountAndResolvedIdentityAreExact() {
        using var fixture = new RootConfigFixture();
        JsonObject one = ParseRoot(MinimalV9);
        Assert.Equal(
            "test-profile",
            fixture.Load(one.ToJsonString()).RecapGrid!
                .CurrentAgentControlProfileId
        );

        JsonObject zero = ParseRoot(MinimalV9);
        RecapObject(zero)["agentControlProfileFiles"] = new JsonArray();
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            zero.ToJsonString()
        ));

        JsonObject missingPath = ParseRoot(MinimalV9);
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
        JsonObject maximumRoot = ParseRoot(MinimalV9);
        JsonObject maximumRecap = RecapObject(maximumRoot);
        maximumRecap["agentControlProfileFiles"] = StringArray(paths);
        maximumRecap["currentAgentControlProfileId"] = "profile-000";
        GalateaConfig maximum = fixture.Load(maximumRoot.ToJsonString());
        Assert.Equal(
            "profile-000",
            maximum.RecapGrid!.CurrentAgentControlProfileId
        );

        JsonObject overRoot = ParseRoot(MinimalV9);
        JsonObject overRecap = RecapObject(overRoot);
        overRecap["agentControlProfileFiles"] = StringArray(
            paths.Append("profiles/not-read-257.json")
        );
        overRecap["currentAgentControlProfileId"] = "profile-000";
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            overRoot.ToJsonString()
        ));

        JsonObject duplicateRoot = ParseRoot(MinimalV9);
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
        JsonObject duplicateProfileRoot = ParseRoot(MinimalV9);
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
        JsonObject duplicateRuntimeRoot = ParseRoot(MinimalV9);
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

        JsonObject missingInline = ParseRoot(MinimalV9);
        UserObject(missingInline).Remove("characterContextTemplate");
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            missingInline.ToJsonString()
        ));

        JsonObject nullInline = ParseRoot(MinimalV9);
        UserObject(nullInline)["characterContextTemplate"] = null;
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            nullInline.ToJsonString()
        ));

        JsonObject blankInline = ParseRoot(MinimalV9);
        UserObject(blankInline)["characterContextTemplate"] = " \t ";
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            blankInline.ToJsonString()
        ));

        foreach (string invalidTemplate in new[] {
                     "plain prompt",
                     "${CharacterName}",
                     "${characterName} ${other}",
                     "${"
                 }) {
            JsonObject invalid = ParseRoot(MinimalV9);
            UserObject(invalid)["characterContextTemplate"] =
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
            JsonObject noFile = ParseRoot(MinimalV9);
            UserObject(noFile)["characterContextTemplateFile"] =
                absentFile?.DeepClone();
            Assert.Equal(
                ExpectedSystemPrompt("inline ${characterName}"),
                Assert.Single(fixture.Load(noFile.ToJsonString()).Users)
                    .SystemPrompt
            );
        }
        JsonObject missingFileProperty = ParseRoot(MinimalV9);
        Assert.False(UserObject(missingFileProperty)
            .ContainsKey("characterContextTemplateFile"));
        Assert.Equal(
            ExpectedSystemPrompt("inline ${characterName}"),
            Assert.Single(fixture.Load(
                missingFileProperty.ToJsonString()
            ).Users).SystemPrompt
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
            ExpectedSystemPrompt("file wins ${characterName}"),
            Assert.Single(fileWins.Users).SystemPrompt
        );

        JsonObject missingInlineWithFile = ConfigWithPromptFile("prompt.txt");
        Assert.True(UserObject(missingInlineWithFile).Remove(
            "characterContextTemplate"
        ));
        Assert.Equal(
            ExpectedSystemPrompt("file wins ${characterName}"),
            Assert.Single(fixture.Load(
                missingInlineWithFile.ToJsonString()
            ).Users).SystemPrompt
        );

        JsonObject blankInlineWithFile = ConfigWithPromptFile("prompt.txt");
        UserObject(blankInlineWithFile)["characterContextTemplate"] =
            " \t ";
        Assert.Equal(
            ExpectedSystemPrompt("file wins ${characterName}"),
            Assert.Single(fixture.Load(
                blankInlineWithFile.ToJsonString()
            ).Users).SystemPrompt
        );

        JsonObject nullInlineWithFile = ConfigWithPromptFile("prompt.txt");
        UserObject(nullInlineWithFile)["characterContextTemplate"] = null;
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
            "Hello ${playerName}, meet ${characterName}."u8.ToArray()
        );
        JsonObject root = ParseRoot(MinimalV9);
        JsonObject alice = UserNode("alice", "sessions/alice");
        alice["characterName"] = "Alice";
        alice["playerName"] = "Alex";
        alice["characterContextTemplateFile"] = "shared.md";
        JsonObject bob = UserNode("bob", "sessions/bob");
        bob["characterName"] = "鲍勃";
        bob["playerName"] = "小白";
        bob["characterContextTemplateFile"] = "shared.md";
        root["users"] = new JsonArray(alice, bob);

        GalateaConfig loaded = fixture.Load(root.ToJsonString());

        Assert.Equal(
            [
                ExpectedSystemPrompt(
                    "Hello ${playerName}, meet ${characterName}.",
                    "Alice",
                    "Alex",
                    ["鲍勃"]
                ),
                ExpectedSystemPrompt(
                    "Hello ${playerName}, meet ${characterName}.",
                    "鲍勃",
                    "小白",
                    ["Alice"]
                )
            ],
            loaded.Users.Select(static user => user.SystemPrompt)
        );
    }

    [Fact]
    public void RecapPathsAndCurrentProfileIdLockBlankAndExactMatch() {
        using var fixture = new RootConfigFixture();

        GalateaConfig exact = fixture.Load(MinimalV9);
        Assert.Equal(
            Path.Combine(fixture.Root, "routes.json"),
            exact.RecapGrid!.RouteManifestPath
        );
        Assert.Equal(
            "test-profile",
            exact.RecapGrid.CurrentAgentControlProfileId
        );

        JsonObject blankRoute = ParseRoot(MinimalV9);
        RecapObject(blankRoute)["routeManifestPath"] = "  ";
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            blankRoute.ToJsonString()
        ));

        JsonObject blankProfile = ParseRoot(MinimalV9);
        RecapObject(blankProfile)["agentControlProfileFiles"] =
            new JsonArray(" ");
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            blankProfile.ToJsonString()
        ));

        JsonObject blankCurrent = ParseRoot(MinimalV9);
        RecapObject(blankCurrent)["currentAgentControlProfileId"] = " ";
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            blankCurrent.ToJsonString()
        ));

        JsonObject wrongCase = ParseRoot(MinimalV9);
        RecapObject(wrongCase)["currentAgentControlProfileId"] =
            "Test-Profile";
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            wrongCase.ToJsonString()
        ));
    }

    [Fact]
    public void CallLogPathsResolveRelativeAndAbsoluteAndRemainDisjoint() {
        using var fixture = new RootConfigFixture();

        JsonObject relativeRoot = ParseRoot(MinimalV9);
        relativeRoot["callLogDir"] = "call-logs";
        Assert.Equal(
            Path.Combine(fixture.Root, "call-logs"),
            fixture.Load(relativeRoot.ToJsonString()).CallLogDir
        );

        string absolute = Path.Combine(fixture.Root, "absolute-logs");
        JsonObject absoluteRoot = ParseRoot(MinimalV9);
        absoluteRoot["callLogDir"] = absolute;
        Assert.Equal(
            absolute,
            fixture.Load(absoluteRoot.ToJsonString()).CallLogDir
        );

        JsonObject nestedRoot = ParseRoot(MinimalV9);
        nestedRoot["callLogDir"] = "sessions/alice/call-logs";
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            nestedRoot.ToJsonString()
        ));

        JsonObject blankRoot = ParseRoot(MinimalV9);
        blankRoot["callLogDir"] = "  ";
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            blankRoot.ToJsonString()
        ));
    }

    [Fact]
    public void RootBytesRejectBomInvalidUtf8CommentTrailingCommaAndData() {
        using var fixture = new RootConfigFixture();
        byte[] valid = Encoding.UTF8.GetBytes(MinimalV9);
        byte[] bom = [.. Encoding.UTF8.GetPreamble(), .. valid];
        Assert.Throws<InvalidDataException>(() =>
            GalateaStrictConfigReader.ValidateUsers(bom));

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

        string comment = MinimalV9.Replace(
            "\"v\":9,",
            "\"v\":9/*comment*/,",
            StringComparison.Ordinal
        );
        Assert.Throws<InvalidDataException>(() =>
            GalateaStrictConfigReader.ValidateUsers(
                Encoding.UTF8.GetBytes(comment)
            ));

        string trailingComma = MinimalV9[..^1] + ",}";
        Assert.Throws<InvalidDataException>(() =>
            GalateaStrictConfigReader.ValidateUsers(
                Encoding.UTF8.GetBytes(trailingComma)
            ));
        Assert.Throws<InvalidDataException>(() =>
            GalateaStrictConfigReader.ValidateUsers(
                Encoding.UTF8.GetBytes(MinimalV9 + "null")
            ));
    }

    private static string ConfigWithUsers(int count) {
        JsonObject root = ParseRoot(MinimalV9);
        var users = new JsonArray();
        for (int index = 0; index < count; index++) {
            JsonObject user = UserNode(
                $"user-{index:D3}",
                $"sessions/user-{index:D3}"
            );
            user["characterName"] = $"character-{index:D3}";
            users.Add(user);
        }
        root["users"] = users;
        return root.ToJsonString();
    }

    private static JsonObject ConfigWithPromptFile(string path) {
        JsonObject root = ParseRoot(MinimalV9);
        UserObject(root)["characterContextTemplateFile"] = path;
        return root;
    }

    private static JsonObject UserNode(string id, string session) => new() {
        ["userId"] = id,
        ["password"] = "pw",
        ["homeDir"] = $"/__TEST_HOME__/{id}",
        ["sessionDir"] = session,
        ["delegationStateDir"] = $"delegation-state/{id}",
        ["characterMemoryStateDir"] = $"character-memory/{id}",
        ["sessionProvisioning"] = "existing-only",
        ["defaultConnectionId"] = "test",
        ["characterName"] = "Galatea",
        ["playerName"] = "刘世超",
        ["characterContextTemplate"] = "inline ${characterName}"
    };

    private static JsonArray StringArray(IEnumerable<string> values) {
        var array = new JsonArray();
        foreach (string value in values) { array.Add(value); }
        return array;
    }

    private static string ExpectedSystemPrompt(
        string characterContextTemplate,
        string characterName = "Galatea",
        string playerName = "刘世超",
        IReadOnlyList<string>? peerNames = null
    ) => GalateaSystemPromptComposer.Compose(
        characterContextTemplate,
        new GalateaCharacterName(characterName),
        new GalateaPlayerName(playerName),
        false,
        false,
        GalateaStrictConfigReader.MaximumSystemPromptUtf8Bytes,
        homeDir: "/galatea-homes/test",
        characterPeerNames: (peerNames ?? Array.Empty<string>())
            .Select(static value => new GalateaCharacterName(value))
            .ToArray()
    );

    private static string MutateRequired(
        string scope,
        string field,
        RequiredMutation mutation
    ) {
        JsonObject root = ParseRoot(MinimalV9);
        JsonObject target = scope switch {
            "root" => root,
            "user" => UserObject(root),
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

    private static JsonObject UserObject(JsonObject root) =>
        root["users"]!.AsArray()[0]!.AsObject();

    private static JsonObject RecapObject(JsonObject root) =>
        root["recapGrid"]!.AsObject();

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
