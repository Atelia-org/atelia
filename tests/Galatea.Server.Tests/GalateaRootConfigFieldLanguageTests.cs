using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion;
using Atelia.SessionJournal;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.AgentControl;
using Atelia.SessionJournal.RecapGrid.Control;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaRootConfigFieldLanguageTests {
    private const string MinimalV3 =
        "{\"v\":3,\"users\":[{\"userId\":\"alice\",\"password\":\"pw\","
        + "\"sessionDir\":\"sessions/alice\","
        + "\"delegationStateDir\":\"delegation-state/alice\","
        + "\"sessionProvisioning\":\"create-if-missing\","
        + "\"systemPrompt\":\"inline\"}],"
        + "\"recapGrid\":{\"routeManifestPath\":\"routes.json\","
        + "\"agentControlProfileFiles\":[\"profile.json\"],"
        + "\"currentAgentControlProfileId\":\"test-profile\"}}";

    private const string ReorderedEscapedFullV3 =
        "{\"maintenanceMode\":true,\"recapGrid\":{"
        + "\"currentAgentControlProfileId\":\"test-profile\","
        + "\"agentControlProfileFiles\":[\"profile.json\"],"
        + "\"\\u0072outeManifestPath\":\"routes.json\"},"
        + "\"callLogDir\":\"call-logs\","
        + "\"listenUrls\":[\"opaque-listener\",\"opaque-listener\"],"
        + "\"\\u0075sers\":[{\"systemPromptFile\":null,"
        + "\"systemPrompt\":\"inline\",\"sessionDir\":\"sessions/alice\","
        + "\"delegationStateDir\":\"delegation-state/alice\","
        + "\"sessionProvisioning\":\"existing-only\","
        + "\"password\":\"pw\",\"\\u0075serId\":\"alice\"}],\"\\u0076\":3}";

    [Fact]
    public void HandwrittenFullV3AcceptsOrderFreeNestedAndEscapedNames() {
        using var fixture = new RootConfigFixture();

        GalateaConfig config = fixture.Load(ReorderedEscapedFullV3);

        GalateaUserConfig user = Assert.Single(config.Users);
        Assert.Equal("alice", user.UserId);
        Assert.Equal(
            GalateaSessionProvisioning.ExistingOnly,
            user.SessionProvisioning
        );
        Assert.Equal("inline", user.SystemPrompt);
        Assert.Null(user.SystemPromptFile);
        Assert.Equal(
            Path.Combine(fixture.Root, "delegation-state", "alice"),
            user.DelegationStateDir
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
            ("user", "sessionDir"),
            ("user", "delegationStateDir"),
            ("recap", "routeManifestPath"),
            ("recap", "agentControlProfileFiles"),
            ("recap", "currentAgentControlProfileId")
        ];

        foreach ((string scope, string field) in required) {
            Assert.Throws<InvalidOperationException>(() => fixture.Load(
                MutateRequired(scope, field, RequiredMutation.Remove)
            ));
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

        JsonObject missing = ParseRoot(MinimalV3);
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
            JsonObject root = ParseRoot(MinimalV3);
            UserObject(root)["sessionProvisioning"] = invalid?.DeepClone();
            Assert.Throws<InvalidDataException>(() => fixture.Load(
                root.ToJsonString()
            ));
        }

        JsonObject existingOnly = ParseRoot(MinimalV3);
        UserObject(existingOnly)["sessionProvisioning"] = "existing-only";
        Assert.Equal(
            GalateaSessionProvisioning.ExistingOnly,
            Assert.Single(fixture.Load(existingOnly.ToJsonString()).Users)
                .SessionProvisioning
        );
        Assert.Equal(
            GalateaSessionProvisioning.CreateIfMissing,
            Assert.Single(fixture.Load(MinimalV3).Users).SessionProvisioning
        );
    }

    [Fact]
    public void OptionalRootAndUserFieldsLockMissingNullAndDefaultSemantics() {
        using var fixture = new RootConfigFixture();

        GalateaConfig missing = fixture.Load(MinimalV3);
        Assert.Null(missing.ListenUrls);
        Assert.Null(missing.CallLogDir);
        Assert.False(missing.MaintenanceMode);
        Assert.Null(Assert.Single(missing.Users).SystemPromptFile);

        JsonObject explicitValues = ParseRoot(MinimalV3);
        explicitValues["listenUrls"] = null;
        explicitValues["callLogDir"] = null;
        explicitValues["maintenanceMode"] = false;
        UserObject(explicitValues)["systemPromptFile"] = null;
        GalateaConfig explicitDefaults = fixture.Load(
            explicitValues.ToJsonString()
        );
        Assert.Null(explicitDefaults.ListenUrls);
        Assert.Null(explicitDefaults.CallLogDir);
        Assert.False(explicitDefaults.MaintenanceMode);
        Assert.Null(Assert.Single(explicitDefaults.Users).SystemPromptFile);

        JsonObject invalidMaintenance = ParseRoot(MinimalV3);
        invalidMaintenance["maintenanceMode"] = null;
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            invalidMaintenance.ToJsonString()
        ));
    }

    [Fact]
    public void UsersCapAndExactUserIdIdentityAreLocked() {
        using var fixture = new RootConfigFixture();

        JsonObject emptyRoot = ParseRoot(MinimalV3);
        emptyRoot["users"] = new JsonArray();
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            emptyRoot.ToJsonString()
        ));

        GalateaConfig maximum = fixture.Load(ConfigWithUsers(256));
        Assert.Equal(256, maximum.Users.Count);
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            ConfigWithUsers(257)
        ));

        JsonObject duplicate = ParseRoot(MinimalV3);
        duplicate["users"] = new JsonArray(
            UserNode("alice", "sessions/first"),
            UserNode("alice", "sessions/second")
        );
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            duplicate.ToJsonString()
        ));

        JsonObject ordinalDistinct = ParseRoot(MinimalV3);
        ordinalDistinct["users"] = new JsonArray(
            UserNode("alice", "sessions/lower"),
            UserNode("Alice", "sessions/upper")
        );
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
                     "delegationStateDir"
                 }) {
            JsonObject root = ParseRoot(MinimalV3);
            UserObject(root)[field] = " \t ";
            Assert.Throws<InvalidOperationException>(() => fixture.Load(
                root.ToJsonString()
            ));
        }
    }

    [Fact]
    public void ListenUrlsCapAllowsDuplicateOpaqueNonblankValues() {
        using var fixture = new RootConfigFixture();

        JsonObject emptyRoot = ParseRoot(MinimalV3);
        emptyRoot["listenUrls"] = new JsonArray();
        Assert.Empty(fixture.Load(emptyRoot.ToJsonString()).ListenUrls!);

        JsonObject maximumRoot = ParseRoot(MinimalV3);
        maximumRoot["listenUrls"] = StringArray(
            Enumerable.Repeat("opaque-listener", 256)
        );
        GalateaConfig maximum = fixture.Load(maximumRoot.ToJsonString());
        Assert.Equal(256, maximum.ListenUrls!.Count);
        Assert.All(maximum.ListenUrls,
            static value => Assert.Equal("opaque-listener", value));

        JsonObject overRoot = ParseRoot(MinimalV3);
        overRoot["listenUrls"] = StringArray(
            Enumerable.Repeat("opaque-listener", 257)
        );
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            overRoot.ToJsonString()
        ));

        JsonObject blankRoot = ParseRoot(MinimalV3);
        blankRoot["listenUrls"] = new JsonArray(" ");
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            blankRoot.ToJsonString()
        ));

        JsonObject nullItemRoot = ParseRoot(MinimalV3);
        var nullItem = new JsonArray();
        nullItem.Add((JsonNode?)null);
        nullItemRoot["listenUrls"] = nullItem;
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            nullItemRoot.ToJsonString()
        ));

        JsonObject nonStringRoot = ParseRoot(MinimalV3);
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
        JsonObject one = ParseRoot(MinimalV3);
        Assert.Equal(
            "test-profile",
            fixture.Load(one.ToJsonString()).RecapGrid!
                .CurrentAgentControlProfileId
        );

        JsonObject zero = ParseRoot(MinimalV3);
        RecapObject(zero)["agentControlProfileFiles"] = new JsonArray();
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            zero.ToJsonString()
        ));

        JsonObject missingPath = ParseRoot(MinimalV3);
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
        JsonObject maximumRoot = ParseRoot(MinimalV3);
        JsonObject maximumRecap = RecapObject(maximumRoot);
        maximumRecap["agentControlProfileFiles"] = StringArray(paths);
        maximumRecap["currentAgentControlProfileId"] = "profile-000";
        GalateaConfig maximum = fixture.Load(maximumRoot.ToJsonString());
        Assert.Equal(
            "profile-000",
            maximum.RecapGrid!.CurrentAgentControlProfileId
        );

        JsonObject overRoot = ParseRoot(MinimalV3);
        JsonObject overRecap = RecapObject(overRoot);
        overRecap["agentControlProfileFiles"] = StringArray(
            paths.Append("profiles/not-read-257.json")
        );
        overRecap["currentAgentControlProfileId"] = "profile-000";
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            overRoot.ToJsonString()
        ));

        JsonObject duplicateRoot = ParseRoot(MinimalV3);
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
        JsonObject duplicateProfileRoot = ParseRoot(MinimalV3);
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
        JsonObject duplicateRuntimeRoot = ParseRoot(MinimalV3);
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
    public void PromptInlineAndFileLanguageIsExact() {
        using var fixture = new RootConfigFixture();

        JsonObject missingInline = ParseRoot(MinimalV3);
        UserObject(missingInline).Remove("systemPrompt");
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            missingInline.ToJsonString()
        ));

        JsonObject nullInline = ParseRoot(MinimalV3);
        UserObject(nullInline)["systemPrompt"] = null;
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            nullInline.ToJsonString()
        ));

        JsonObject blankInline = ParseRoot(MinimalV3);
        UserObject(blankInline)["systemPrompt"] = " \t ";
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            blankInline.ToJsonString()
        ));

        foreach (JsonNode? absentFile in new JsonNode?[] {
                     null,
                     JsonValue.Create(""),
                     JsonValue.Create("  ")
                 }) {
            JsonObject noFile = ParseRoot(MinimalV3);
            UserObject(noFile)["systemPromptFile"] = absentFile?.DeepClone();
            Assert.Equal(
                "inline",
                Assert.Single(fixture.Load(noFile.ToJsonString()).Users)
                    .SystemPrompt
            );
        }
        JsonObject missingFileProperty = ParseRoot(MinimalV3);
        Assert.False(UserObject(missingFileProperty)
            .ContainsKey("systemPromptFile"));
        Assert.Equal(
            "inline",
            Assert.Single(fixture.Load(
                missingFileProperty.ToJsonString()
            ).Users).SystemPrompt
        );

        JsonObject absentPath = ConfigWithPromptFile("missing-prompt.txt");
        Assert.Throws<FileNotFoundException>(() => fixture.Load(
            absentPath.ToJsonString()
        ));

        fixture.WriteBytes("prompt.txt", " \n file wins \n "u8.ToArray());
        GalateaConfig fileWins = fixture.Load(
            ConfigWithPromptFile("prompt.txt").ToJsonString()
        );
        Assert.Equal("file wins", Assert.Single(fileWins.Users).SystemPrompt);

        JsonObject missingInlineWithFile = ConfigWithPromptFile("prompt.txt");
        Assert.True(UserObject(missingInlineWithFile).Remove("systemPrompt"));
        Assert.Equal(
            "file wins",
            Assert.Single(fixture.Load(
                missingInlineWithFile.ToJsonString()
            ).Users).SystemPrompt
        );

        JsonObject blankInlineWithFile = ConfigWithPromptFile("prompt.txt");
        UserObject(blankInlineWithFile)["systemPrompt"] = " \t ";
        Assert.Equal(
            "file wins",
            Assert.Single(fixture.Load(
                blankInlineWithFile.ToJsonString()
            ).Users).SystemPrompt
        );

        JsonObject nullInlineWithFile = ConfigWithPromptFile("prompt.txt");
        UserObject(nullInlineWithFile)["systemPrompt"] = null;
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
    public void RecapPathsAndCurrentProfileIdLockBlankAndExactMatch() {
        using var fixture = new RootConfigFixture();

        GalateaConfig exact = fixture.Load(MinimalV3);
        Assert.Equal(
            Path.Combine(fixture.Root, "routes.json"),
            exact.RecapGrid!.RouteManifestPath
        );
        Assert.Equal(
            "test-profile",
            exact.RecapGrid.CurrentAgentControlProfileId
        );

        JsonObject blankRoute = ParseRoot(MinimalV3);
        RecapObject(blankRoute)["routeManifestPath"] = "  ";
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            blankRoute.ToJsonString()
        ));

        JsonObject blankProfile = ParseRoot(MinimalV3);
        RecapObject(blankProfile)["agentControlProfileFiles"] =
            new JsonArray(" ");
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            blankProfile.ToJsonString()
        ));

        JsonObject blankCurrent = ParseRoot(MinimalV3);
        RecapObject(blankCurrent)["currentAgentControlProfileId"] = " ";
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            blankCurrent.ToJsonString()
        ));

        JsonObject wrongCase = ParseRoot(MinimalV3);
        RecapObject(wrongCase)["currentAgentControlProfileId"] =
            "Test-Profile";
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            wrongCase.ToJsonString()
        ));
    }

    [Fact]
    public void CallLogPathsResolveRelativeAndAbsoluteAndRemainDisjoint() {
        using var fixture = new RootConfigFixture();

        JsonObject relativeRoot = ParseRoot(MinimalV3);
        relativeRoot["callLogDir"] = "call-logs";
        Assert.Equal(
            Path.Combine(fixture.Root, "call-logs"),
            fixture.Load(relativeRoot.ToJsonString()).CallLogDir
        );

        string absolute = Path.Combine(fixture.Root, "absolute-logs");
        JsonObject absoluteRoot = ParseRoot(MinimalV3);
        absoluteRoot["callLogDir"] = absolute;
        Assert.Equal(
            absolute,
            fixture.Load(absoluteRoot.ToJsonString()).CallLogDir
        );

        JsonObject nestedRoot = ParseRoot(MinimalV3);
        nestedRoot["callLogDir"] = "sessions/alice/call-logs";
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            nestedRoot.ToJsonString()
        ));

        JsonObject blankRoot = ParseRoot(MinimalV3);
        blankRoot["callLogDir"] = "  ";
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            blankRoot.ToJsonString()
        ));
    }

    [Fact]
    public void RootBytesRejectBomInvalidUtf8CommentTrailingCommaAndData() {
        using var fixture = new RootConfigFixture();
        byte[] valid = Encoding.UTF8.GetBytes(MinimalV3);
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
        Assert.DoesNotContain("systemPrompt", invalidUtf8Failure.Message);

        string comment = MinimalV3.Replace(
            "\"v\":3,",
            "\"v\":3/*comment*/,",
            StringComparison.Ordinal
        );
        Assert.Throws<InvalidDataException>(() =>
            GalateaStrictConfigReader.ValidateUsers(
                Encoding.UTF8.GetBytes(comment)
            ));

        string trailingComma = MinimalV3[..^1] + ",}";
        Assert.Throws<InvalidDataException>(() =>
            GalateaStrictConfigReader.ValidateUsers(
                Encoding.UTF8.GetBytes(trailingComma)
            ));
        Assert.Throws<InvalidDataException>(() =>
            GalateaStrictConfigReader.ValidateUsers(
                Encoding.UTF8.GetBytes(MinimalV3 + "null")
            ));
    }

    private static string ConfigWithUsers(int count) {
        JsonObject root = ParseRoot(MinimalV3);
        var users = new JsonArray();
        for (int index = 0; index < count; index++) {
            users.Add(UserNode(
                $"user-{index:D3}",
                $"sessions/user-{index:D3}"
            ));
        }
        root["users"] = users;
        return root.ToJsonString();
    }

    private static JsonObject ConfigWithPromptFile(string path) {
        JsonObject root = ParseRoot(MinimalV3);
        UserObject(root)["systemPromptFile"] = path;
        return root;
    }

    private static JsonObject UserNode(string id, string session) => new() {
        ["userId"] = id,
        ["password"] = "pw",
        ["sessionDir"] = session,
        ["delegationStateDir"] = $"delegation-state/{id}",
        ["sessionProvisioning"] = "existing-only",
        ["systemPrompt"] = "inline"
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
        JsonObject root = ParseRoot(MinimalV3);
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
