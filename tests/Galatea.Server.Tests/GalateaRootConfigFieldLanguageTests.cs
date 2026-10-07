using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion;
using Atelia.Galatea.Prompts;
using Atelia.Galatea.Server.Mailbox;
using Atelia.SessionJournal;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaRootConfigFieldLanguageTests {
    [Theory]
    [InlineData(false, false, "blocked:alice:SMTP_DISABLED")]
    [InlineData(false, true, "blocked:alice:SMTP_DISABLED")]
    [InlineData(true, false, "blocked:alice:NO_SENDER_BINDING")]
    public void SmtpLoaderFreezesExactRolePolicy(bool enabled, bool hasAccount, string reference) {
        using var fixture = new RootConfigFixture();
        JsonObject root = ParseRoot(MinimalV15);
        root["runtime"]!["smtp"] = new JsonObject { ["enabled"] = enabled };
        CharacterObject(root)["email"] = hasAccount ? EmailNode() : null;

        GalateaConfig config = fixture.Load(root.ToJsonString());

        Assert.Equal(reference, Assert.Single(config.Characters).SmtpSenderAccountReference);
        Assert.Equal(60, config.Smtp.TimeoutSeconds);
        Assert.Equal(hasAccount ? 1 : 0, config.Smtp.Accounts.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SmtpMissingOrEmptyPolicyAndNullEmailDefaultToDisabled(bool emptyPolicy) {
        using var fixture = new RootConfigFixture();
        JsonObject root = ParseRoot(MinimalV15);
        CharacterObject(root)["email"] = null;
        if (emptyPolicy) { root["runtime"]!["smtp"] = new JsonObject(); }
        GalateaConfig loaded = fixture.Load(root.ToJsonString());
        Assert.False(loaded.Smtp.Enabled);
        Assert.Empty(loaded.Smtp.Accounts);
        Assert.Equal(60, loaded.Smtp.TimeoutSeconds);
        Assert.Equal("blocked:alice:SMTP_DISABLED", Assert.Single(loaded.Characters).SmtpSenderAccountReference);
    }

    [Fact]
    public void SmtpEmailSnapshotReusesValidatedObjectsAndMapsExactCharacters() {
        using var fixture = new RootConfigFixture();
        JsonObject root = ParseRoot(MinimalV15);
        CharacterObject(root)["email"] = EmailNode();
        JsonObject bob = CharacterNode("bob", "sessions/bob");
        bob["name"] = "Bob";
        bob["email"] = EmailNode("bob@Example.test", "another-synthetic-code");
        root["characters"]!.AsArray().Add(bob);
        root["runtime"]!["smtp"] = new JsonObject { ["enabled"] = true, ["timeoutSeconds"] = 17 };

        GalateaRootFileConfig file = JsonSerializer.Deserialize<GalateaRootFileConfig>(
            root.ToJsonString(), GalateaJsonContext.Default.GalateaRootFileConfig)!;
        GalateaSmtpConfig settings = GalateaSmtpConfig.Resolve(file.Runtime.Smtp, file.Characters);
        Assert.Same(file.Characters[0].Email, settings.Accounts["alice"]);
        Assert.Same(file.Characters[1].Email, settings.Accounts["bob"]);
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, GalateaEmailAccount>)settings.Accounts).Clear());

        GalateaConfig loaded = fixture.Load(root.ToJsonString());
        Assert.Equal(17, loaded.Smtp.TimeoutSeconds);
        Assert.Equal("host@Example.test", loaded.Smtp.Accounts["alice"].Address);
        Assert.Equal("bob@Example.test", loaded.Smtp.Accounts["bob"].Address);
        Assert.Equal(loaded.Smtp.ReferenceFor("alice"), loaded.Characters[0].SmtpSenderAccountReference);
        Assert.Equal(loaded.Smtp.ReferenceFor("bob"), loaded.Characters[1].SmtpSenderAccountReference);
        Assert.NotEqual(loaded.Characters[0].SmtpSenderAccountReference,
            loaded.Characters[1].SmtpSenderAccountReference);
    }

    [Fact]
    public void SmtpBindingIdentityFreezesLiteralDescriptorAndAllowsCodeRotation() {
        var account = new GalateaEmailAccount("host@Example.test", "first-code", "smtp.example.test", 465, "implicit");
        string reference = GalateaSmtpConfig.AccountReference("alice", account);
        Assert.Equal("smtp:alice:ca7fc12cc6704682616a20058d7bc07fbc41de74b2e37987affbe8e50b180cc9", reference);
        Assert.Equal(reference, GalateaSmtpConfig.AccountReference("alice",
            new("host@Example.test", "rotated-code", "smtp.example.test", 465, "implicit")));
        foreach (var changed in new GalateaEmailAccount[] {
                     new("host@example.test", "first-code", "smtp.example.test", 465, "implicit"),
                     new("host@Example.test", "first-code", "SMTP.example.test", 465, "implicit"),
                     new("host@Example.test", "first-code", "smtp.example.test", 587, "implicit"),
                     new("host@Example.test", "first-code", "smtp.example.test", 465, "starttls")
                 }) {
            Assert.NotEqual(reference, GalateaSmtpConfig.AccountReference("alice", changed));
        }
        Assert.NotEqual(reference, GalateaSmtpConfig.AccountReference("Alice", account));
        Assert.DoesNotContain("first-code", reference, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SMTP_DISABLED")]
    [InlineData("NO_SENDER_BINDING")]
    [InlineData("SENDER_BINDING_DISABLED")]
    public void RemovedConfigurationModesPreserveHistoricalReferenceGrammar(string reason) {
        string reference = "blocked:alice:" + reason;
        Assert.Equal(reason, GalateaSmtpConfig.BlockedReason(reference, "alice"));
        Assert.True(GalateaSmtpConfig.IsReferenceFor(reference, "alice"));
        Assert.False(GalateaSmtpConfig.IsReferenceFor(reference, "bob"));
        Assert.True(GalateaSmtpConfig.IsReferenceFor("offline:alice", "alice"));
        Assert.True(GalateaSmtpConfig.IsReferenceFor("smtp:alice:old-manual-binding", "alice"));
    }

    [Theory]
    [InlineData("old-offline-mode")]
    [InlineData("old-sender-accounts")]
    [InlineData("unknown-policy")]
    [InlineData("duplicate-policy")]
    [InlineData("timeout-zero")]
    [InlineData("timeout-too-large")]
    [InlineData("partial-email")]
    [InlineData("unknown-email")]
    [InlineData("email-enabled")]
    [InlineData("duplicate-email")]
    [InlineData("bad-address")]
    [InlineData("address-whitespace")]
    [InlineData("empty-code")]
    [InlineData("code-control")]
    [InlineData("url-host")]
    [InlineData("unicode-host")]
    [InlineData("blank-host")]
    [InlineData("long-host")]
    [InlineData("invalid-dns-label")]
    [InlineData("port-zero")]
    [InlineData("port-too-large")]
    [InlineData("port-string")]
    [InlineData("port-fraction")]
    [InlineData("plaintext-tls")]
    [InlineData("wrong-case-tls")]
    public void SmtpRejectsInvalidConfigurationEvenWhenSendingDisabled(string mode) {
        using var fixture = new RootConfigFixture();
        JsonObject root = ParseRoot(MinimalV15);
        root["runtime"]!["smtp"] = new JsonObject { ["enabled"] = false, ["timeoutSeconds"] = 60 };
        CharacterObject(root)["email"] = EmailNode();
        JsonObject smtp = root["runtime"]!["smtp"]!.AsObject();
        JsonObject account = CharacterObject(root)["email"]!.AsObject();
        switch (mode) {
            case "old-offline-mode": smtp["offlineMode"] = true; break;
            case "old-sender-accounts": smtp["senderAccounts"] = new JsonArray(); break;
            case "unknown-policy": smtp["extra"] = 1; break;
            case "timeout-zero": smtp["timeoutSeconds"] = 0; break;
            case "timeout-too-large": smtp["timeoutSeconds"] = 301; break;
            case "partial-email": account.Remove("authorizationCode"); break;
            case "unknown-email": account["credentialPath"] = "/obsolete"; break;
            case "email-enabled": account["enabled"] = true; break;
            case "bad-address": account["address"] = "Display <host@Example.test>"; break;
            case "address-whitespace": account["address"] = " host@Example.test"; break;
            case "empty-code": account["authorizationCode"] = ""; break;
            case "code-control": account["authorizationCode"] = "synthetic\r\ncode"; break;
            case "url-host": account["smtpHost"] = "https://smtp.example.test/path"; break;
            case "unicode-host": account["smtpHost"] = "邮箱.example.test"; break;
            case "blank-host": account["smtpHost"] = " "; break;
            case "long-host": account["smtpHost"] = new string('a', 254); break;
            case "invalid-dns-label": account["smtpHost"] = "-invalid.example.test"; break;
            case "port-zero": account["smtpPort"] = 0; break;
            case "port-too-large": account["smtpPort"] = 65536; break;
            case "port-string": account["smtpPort"] = "465"; break;
            case "port-fraction": account["smtpPort"] = 465.5; break;
            case "plaintext-tls": account["tlsMode"] = "none"; break;
            case "wrong-case-tls": account["tlsMode"] = "Implicit"; break;
        }
        string json = root.ToJsonString();
        if (mode == "duplicate-policy") {
            json = json.Replace("\"timeoutSeconds\":60", "\"timeoutSeconds\":60,\"timeoutSeconds\":61", StringComparison.Ordinal);
        }
        if (mode == "duplicate-email") {
            json = json.Replace("\"smtpPort\":465", "\"smtpPort\":465,\"smtpPort\":587", StringComparison.Ordinal);
        }
        Assert.ThrowsAny<InvalidDataException>(() => fixture.Load(json));
    }

    [Theory]
    [InlineData("unknown-name")]
    [InlineData("duplicate-value")]
    [InlineData("wrong-type")]
    [InlineData("malformed-json")]
    [InlineData("truncated-unknown-name")]
    [InlineData("truncated-known-value")]
    [InlineData("invalid-value")]
    public void EmailFailuresNeverRevealSecretInExceptionChain(string mode) {
        const string Secret = "SMTP_AUTHORIZATION_SENTINEL_DO_NOT_EXPOSE";
        using var fixture = new RootConfigFixture();
        JsonObject root = ParseRoot(MinimalV15);
        CharacterObject(root)["email"] = EmailNode(authorizationCode: Secret);
        JsonObject email = CharacterObject(root)["email"]!.AsObject();
        if (mode is "unknown-name" or "truncated-unknown-name") { email[Secret] = Secret; }
        if (mode == "wrong-type") { email["authorizationCode"] = new JsonObject { [Secret] = Secret }; }
        if (mode == "invalid-value") { email["authorizationCode"] = Secret + "\r\n"; }
        string json = root.ToJsonString();
        if (mode == "duplicate-value") {
            json = json.Replace("\"authorizationCode\":\"" + Secret + "\"",
                "\"authorizationCode\":\"" + Secret + "\",\"authorizationCode\":\"" + Secret + "\"",
                StringComparison.Ordinal);
        }
        if (mode == "malformed-json") {
            json = json.Replace("\"authorizationCode\":\"" + Secret + "\"",
                "\"authorizationCode\":{\"" + Secret + "\" 1}", StringComparison.Ordinal);
        }
        if (mode == "truncated-unknown-name") {
            int field = json.IndexOf("\"" + Secret + "\":", StringComparison.Ordinal);
            Assert.True(field >= 0);
            json = json[..(field + Secret.Length + 2)];
        }
        if (mode == "truncated-known-value") {
            int field = json.IndexOf("\"authorizationCode\":", StringComparison.Ordinal);
            Assert.True(field >= 0);
            json = json[..(field + "\"authorizationCode\"".Length)];
        }

        Exception failure = Assert.ThrowsAny<InvalidDataException>(() => fixture.Load(json));
        Assert.DoesNotContain(Secret, failure.ToString(), StringComparison.Ordinal);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public void EmailSecretsAreExcludedFromPublicRuntimeAndRecordPrinting() {
        const string Secret = "SMTP_RUNTIME_SECRET_SENTINEL";
        using var fixture = new RootConfigFixture();
        JsonObject root = ParseRoot(MinimalV15);
        CharacterObject(root)["email"] = EmailNode(authorizationCode: Secret);
        root["runtime"]!["smtp"] = new JsonObject { ["enabled"] = true };
        GalateaConfig loaded = fixture.Load(root.ToJsonString());
        Assert.DoesNotContain(Secret, loaded.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, loaded.Smtp.ToString()!, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, loaded.Smtp.Accounts["alice"].ToString()!, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(loaded, GalateaJson.Options), StringComparison.Ordinal);
        Assert.Null(typeof(GalateaConfig).GetProperty("Smtp"));
        Assert.False(typeof(GalateaEmailAccount).IsPublic);
        Assert.DoesNotContain(typeof(GalateaEmailAccount), typeof(GalateaConfig).Assembly.GetExportedTypes());
        Assert.Null(typeof(GalateaCharacterConfig).GetProperty("Email"));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("smtp.example.test.")]
    [InlineData("smtp-1.example.test")]
    public void SmtpAllowsAsciiDnsAndIpHostLiterals(string host) {
        JsonObject root = ParseRoot(MinimalV15);
        CharacterObject(root)["email"] = EmailNode();
        CharacterObject(root)["email"]!["smtpHost"] = host;
        GalateaStrictConfigReader.ValidateRoot(Encoding.UTF8.GetBytes(root.ToJsonString()));
    }

    [Fact]
    public void SmtpDefaultTemplateIsStrictAndSendingDisabledWithoutAccounts() {
        GalateaRootFileConfig root = GalateaConfigTemplateFactory.CreateRootFile();
        Assert.Equal(15, root.Version);
        Assert.False(root.Runtime.Smtp!.Enabled);
        Assert.Equal(60, root.Runtime.Smtp.TimeoutSeconds);
        Assert.All(root.Characters, static character => Assert.Null(character.Email));
        GalateaStrictConfigReader.ValidateRoot(JsonSerializer.SerializeToUtf8Bytes(root, GalateaJson.Options));
    }

    private static JsonObject EmailNode(string address = "host@Example.test",
        string authorizationCode = "synthetic-authorization-code") => new() {
        ["address"] = address,
        ["authorizationCode"] = authorizationCode,
        ["smtpHost"] = "smtp.example.test",
        ["smtpPort"] = 465,
        ["tlsMode"] = "implicit"
    };


    [Fact]
    public void RuntimePolicySerializerRoundTripsDefaultBootstrapShapeWithoutNullOverride() {
        GalateaRootFileConfig root = JsonSerializer.Deserialize<GalateaRootFileConfig>(MinimalV15, GalateaJson.Options)!;
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
        JsonObject root = ParseRoot(MinimalV15);
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
        string root = MinimalV15.Replace("\"runtime\":{", "\"runtime\":{\"completionAttemptTimeoutSeconds\":" + value + ",", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => GalateaStrictConfigReader.ValidateRoot(Encoding.UTF8.GetBytes(root)));
    }

    [Fact]
    public void AutonomyEnrollmentComesOnlyFromCharactersAndAllowsZeroPlayers() {
        using var fixture = new RootConfigFixture();
        JsonObject root = ParseRoot(MinimalV15);
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
        JsonObject root = ParseRoot(MinimalV15);
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
        JsonObject root = ParseRoot(MinimalV15);
        CharacterObject(root)["autonomyIntervalMinutes"] = JsonNode.Parse(json);
        Assert.Throws<InvalidDataException>(() => GalateaStrictConfigReader.ValidateRoot(Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(525_601)]
    public void AutonomyIntervalRejectsOutOfRangeValues(int value) {
        JsonObject root = ParseRoot(MinimalV15);
        CharacterObject(root)["autonomyIntervalMinutes"] = value;
        Assert.Throws<InvalidDataException>(() => GalateaStrictConfigReader.ValidateRoot(Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    [Fact]
    public void AutonomyIntervalIsRequiredAndOldHeartbeatFieldIsUnknown() {
        JsonObject missing = ParseRoot(MinimalV15);
        Assert.True(CharacterObject(missing).Remove("autonomyIntervalMinutes"));
        Assert.Throws<InvalidDataException>(() => GalateaStrictConfigReader.ValidateRoot(Encoding.UTF8.GetBytes(missing.ToJsonString())));

        JsonObject old = ParseRoot(MinimalV15);
        CharacterObject(old)["heartbeatEnabled"] = true;
        Assert.Throws<InvalidDataException>(() => GalateaStrictConfigReader.ValidateRoot(Encoding.UTF8.GetBytes(old.ToJsonString())));
    }

    [Theory]
    [InlineData("serverAgentUserIds")]
    [InlineData("users")]
    [InlineData("Characters")]
    public void OldOrWrongCaseRootFieldsAreRejected(string field) {
        JsonObject root = ParseRoot(MinimalV15);
        root[field] = new JsonArray();
        Assert.Throws<InvalidDataException>(() => GalateaStrictConfigReader.ValidateRoot(Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    [Fact]
    public void PlayerAndCharacterIdsHaveIndependentNamespaces() {
        using var fixture = new RootConfigFixture();
        JsonObject root = ParseRoot(MinimalV15);
        PlayerObject(root)["id"] = "alice";
        GalateaConfig config = fixture.Load(root.ToJsonString());
        Assert.Equal("alice", Assert.Single(config.Players).PlayerId);
        Assert.Equal("alice", Assert.Single(config.Characters).CharacterId);
        root["players"]!.AsArray().Add(PlayerObject(root).DeepClone());
        Assert.Throws<InvalidOperationException>(() => fixture.Load(root.ToJsonString()));
    }

    private const string MinimalV15 = """
        {"v":15,"characters":[{"id":"alice","name":"Galatea","homeDir":"/__TEST_HOME__/alice","sessionDir":"sessions/alice","delegationStateDir":"delegation-state/alice","characterMemoryStateDir":"character-memory/alice","sessionProvisioning":"create-if-missing","defaultConnectionId":"test","connectionOptions":[{"connectionId":"test","name":"","trigger":""}],"characterContextTemplate":"inline ${characterName}","autonomyIntervalMinutes":0}],"players":[{"id":"player-main","name":"刘世超","password":"pw"}],"runtime":{"recapGrid":{"maintenance":{"connectionId":"test","maximumConcurrency":1,"dispatchTimeoutMilliseconds":900000}}}}
        """;

    private const string ReorderedEscapedFullV15 = """
        {"runtime":{"maintenanceMode":true,"recapGrid":{"maintenance":{"dispatchTimeoutMilliseconds":900000,"maximumConcurrency":1,"connectionId":"test"}},"callLogDir":"call-logs","listenUrls":["opaque-listener","opaque-listener"]},"players":[{"password":"pw","name":"刘世超","id":"player-main"}],"\u0063haracters":[{"characterContextTemplateFile":null,"characterContextTemplate":"inline ${characterName}","name":"Galatea","homeDir":"/__TEST_HOME__/alice","sessionDir":"sessions/alice","delegationStateDir":"delegation-state/alice","characterMemoryStateDir":"character-memory/alice","sessionProvisioning":"existing-only","defaultConnectionId":"test","connectionOptions":[{"connectionId":"test","name":"","trigger":""}],"autonomyIntervalMinutes":10,"\u0069d":"alice"}],"\u0076":15}
        """;

    [Fact]
    public void HandwrittenFullV15AcceptsOrderFreeNestedAndEscapedNames() {
        using var fixture = new RootConfigFixture();

        GalateaConfig config = fixture.Load(ReorderedEscapedFullV15);

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

        JsonObject missing = ParseRoot(MinimalV15);
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
            JsonObject root = ParseRoot(MinimalV15);
            CharacterObject(root)["sessionProvisioning"] = invalid?.DeepClone();
            Assert.Throws<InvalidDataException>(() => fixture.Load(
                root.ToJsonString()
            ));
        }

        JsonObject existingOnly = ParseRoot(MinimalV15);
        CharacterObject(existingOnly)["sessionProvisioning"] = "existing-only";
        Assert.Equal(
            GalateaSessionProvisioning.ExistingOnly,
            Assert.Single(fixture.Load(existingOnly.ToJsonString()).Characters)
                .SessionProvisioning
        );
        Assert.Equal(
            GalateaSessionProvisioning.CreateIfMissing,
            Assert.Single(fixture.Load(MinimalV15).Characters).SessionProvisioning
        );
    }

    [Fact]
    public void OptionalRootAndUserFieldsLockMissingNullAndDefaultSemantics() {
        using var fixture = new RootConfigFixture();

        GalateaConfig missing = fixture.Load(MinimalV15);
        Assert.Null(missing.ListenUrls);
        Assert.Null(missing.CallLogDir);
        Assert.False(missing.MaintenanceMode);

        JsonObject explicitValues = ParseRoot(MinimalV15);
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

        JsonObject invalidMaintenance = ParseRoot(MinimalV15);
        RuntimeObject(invalidMaintenance)["maintenanceMode"] = null;
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            invalidMaintenance.ToJsonString()
        ));
    }

    [Fact]
    public void UsersCapAndExactUserIdIdentityAreLocked() {
        using var fixture = new RootConfigFixture();

        JsonObject emptyRoot = ParseRoot(MinimalV15);
        emptyRoot["characters"] = new JsonArray();
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            emptyRoot.ToJsonString()
        ));

        GalateaConfig maximum = fixture.Load(ConfigWithCharacters(256));
        Assert.Equal(256, maximum.Characters.Count);
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            ConfigWithCharacters(257)
        ));

        JsonObject duplicate = ParseRoot(MinimalV15);
        duplicate["characters"] = new JsonArray(
            CharacterNode("alice", "sessions/first"),
            CharacterNode("alice", "sessions/second")
        );
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            duplicate.ToJsonString()
        ));

        JsonObject ordinalDistinct = ParseRoot(MinimalV15);
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
            JsonObject root = ParseRoot(MinimalV15);
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
            JsonObject root = ParseRoot(MinimalV15);
            CharacterObject(root)["name"] = invalid;
            Assert.Throws<InvalidOperationException>(() => fixture.Load(
                root.ToJsonString()
            ));
        }

        foreach (string valid in new[] {
                     "👩‍🚀",
                     new string('a', 128)
                 }) {
            JsonObject root = ParseRoot(MinimalV15);
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
        JsonObject root = ParseRoot(MinimalV15);
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
            JsonObject root = ParseRoot(MinimalV15);
            PlayerObject(root)["name"] = invalid;
            Assert.Throws<InvalidOperationException>(() => fixture.Load(
                root.ToJsonString()
            ));
        }

        foreach (string valid in new[] {
                     "🧑‍🚀",
                     new string('a', 128)
                 }) {
            JsonObject root = ParseRoot(MinimalV15);
            PlayerObject(root)["name"] = valid;
            GalateaConfig loaded = fixture.Load(root.ToJsonString());
            Assert.Equal(valid, Assert.Single(loaded.Players).Name.Value);
            Assert.Equal(Assert.Single(fixture.Load(MinimalV15).Characters).SystemPrompt,
                Assert.Single(loaded.Characters).SystemPrompt);
        }
    }

    [Fact]
    public void LegacyPromptFieldsRemainUnknownInV15() {
        using var fixture = new RootConfigFixture();

        foreach (string oldField in new[] {
                     "systemPrompt",
                     "systemPromptFile",
                     "systemPromptTemplate",
                     "systemPromptTemplateFile"
                 }) {
            JsonObject root = ParseRoot(MinimalV15);
            CharacterObject(root)[oldField] = "legacy";
            Assert.Throws<InvalidDataException>(() => fixture.Load(
                root.ToJsonString()
            ));
        }
    }

    [Fact]
    public void ListenUrlsCapAllowsDuplicateOpaqueNonblankValues() {
        using var fixture = new RootConfigFixture();

        JsonObject emptyRoot = ParseRoot(MinimalV15);
        RuntimeObject(emptyRoot)["listenUrls"] = new JsonArray();
        Assert.Empty(fixture.Load(emptyRoot.ToJsonString()).ListenUrls!);

        JsonObject maximumRoot = ParseRoot(MinimalV15);
        RuntimeObject(maximumRoot)["listenUrls"] = StringArray(
            Enumerable.Repeat("opaque-listener", 256)
        );
        GalateaConfig maximum = fixture.Load(maximumRoot.ToJsonString());
        Assert.Equal(256, maximum.ListenUrls!.Count);
        Assert.All(maximum.ListenUrls,
            static value => Assert.Equal("opaque-listener", value));

        JsonObject overRoot = ParseRoot(MinimalV15);
        RuntimeObject(overRoot)["listenUrls"] = StringArray(
            Enumerable.Repeat("opaque-listener", 257)
        );
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            overRoot.ToJsonString()
        ));

        JsonObject blankRoot = ParseRoot(MinimalV15);
        RuntimeObject(blankRoot)["listenUrls"] = new JsonArray(" ");
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            blankRoot.ToJsonString()
        ));

        JsonObject nullItemRoot = ParseRoot(MinimalV15);
        var nullItem = new JsonArray();
        nullItem.Add((JsonNode?)null);
        RuntimeObject(nullItemRoot)["listenUrls"] = nullItem;
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            nullItemRoot.ToJsonString()
        ));

        JsonObject nonStringRoot = ParseRoot(MinimalV15);
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

        JsonObject missingInline = ParseRoot(MinimalV15);
        CharacterObject(missingInline).Remove("characterContextTemplate");
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            missingInline.ToJsonString()
        ));

        JsonObject nullInline = ParseRoot(MinimalV15);
        CharacterObject(nullInline)["characterContextTemplate"] = null;
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            nullInline.ToJsonString()
        ));

        JsonObject blankInline = ParseRoot(MinimalV15);
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
            JsonObject invalid = ParseRoot(MinimalV15);
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
            JsonObject noFile = ParseRoot(MinimalV15);
            CharacterObject(noFile)["characterContextTemplateFile"] =
                absentFile?.DeepClone();
            Assert.Equal(
                "inline ${characterName}",
                Assert.Single(fixture.Load(noFile.ToJsonString()).Characters)
                    .SystemPrompt.JsonValue.GetProperty("instructions")[1].GetProperty("source").GetString()
            );
        }
        JsonObject missingFileProperty = ParseRoot(MinimalV15);
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
        JsonObject root = ParseRoot(MinimalV15);
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

        GalateaConfig exact = fixture.Load(MinimalV15);
        Assert.Equal("test", exact.RecapGrid!.Maintenance.ConnectionId);
        Assert.Equal(1, exact.RecapGrid.Maintenance.MaximumConcurrency);
        Assert.Equal(TimeSpan.FromMilliseconds(900_000),
            exact.RecapGrid.Maintenance.DispatchTimeout);

        JsonObject blankConnection = ParseRoot(MinimalV15);
        RecapObject(blankConnection)["maintenance"]!["connectionId"] = "  ";
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            blankConnection.ToJsonString()
        ));

        JsonObject badConcurrency = ParseRoot(MinimalV15);
        RecapObject(badConcurrency)["maintenance"]!["maximumConcurrency"] = 0;
        Assert.Throws<InvalidDataException>(() => fixture.Load(
            badConcurrency.ToJsonString()));
    }

    [Fact]
    public void CallLogPathsResolveRelativeAndAbsoluteAndRemainDisjoint() {
        using var fixture = new RootConfigFixture();

        JsonObject relativeRoot = ParseRoot(MinimalV15);
        RuntimeObject(relativeRoot)["callLogDir"] = "call-logs";
        Assert.Equal(
            Path.Combine(fixture.Root, "call-logs"),
            fixture.Load(relativeRoot.ToJsonString()).CallLogDir
        );

        string absolute = Path.Combine(fixture.Root, "absolute-logs");
        JsonObject absoluteRoot = ParseRoot(MinimalV15);
        RuntimeObject(absoluteRoot)["callLogDir"] = absolute;
        Assert.Equal(
            absolute,
            fixture.Load(absoluteRoot.ToJsonString()).CallLogDir
        );

        JsonObject nestedRoot = ParseRoot(MinimalV15);
        RuntimeObject(nestedRoot)["callLogDir"] = "sessions/alice/call-logs";
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            nestedRoot.ToJsonString()
        ));

        JsonObject blankRoot = ParseRoot(MinimalV15);
        RuntimeObject(blankRoot)["callLogDir"] = "  ";
        Assert.Throws<InvalidOperationException>(() => fixture.Load(
            blankRoot.ToJsonString()
        ));
    }

    [Fact]
    public void RootBytesRejectBomInvalidUtf8CommentTrailingCommaAndData() {
        using var fixture = new RootConfigFixture();
        byte[] valid = Encoding.UTF8.GetBytes(MinimalV15);
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
        Assert.Null(invalidUtf8Failure.InnerException);
        Assert.DoesNotContain(
            "characterContextTemplate",
            invalidUtf8Failure.Message
        );

        string comment = MinimalV15.Replace(
            "\"v\":15,",
            "\"v\":15/*comment*/,",
            StringComparison.Ordinal
        );
        Assert.Throws<InvalidDataException>(() =>
            GalateaStrictConfigReader.ValidateRoot(
                Encoding.UTF8.GetBytes(comment)
            ));

        string trailingComma = MinimalV15[..^1] + ",}";
        Assert.Throws<InvalidDataException>(() =>
            GalateaStrictConfigReader.ValidateRoot(
                Encoding.UTF8.GetBytes(trailingComma)
            ));
        Assert.Throws<InvalidDataException>(() =>
            GalateaStrictConfigReader.ValidateRoot(
                Encoding.UTF8.GetBytes(MinimalV15 + "null")
            ));
    }

    private static string ConfigWithCharacters(int count) {
        JsonObject root = ParseRoot(MinimalV15);
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
        JsonObject root = ParseRoot(MinimalV15);
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
        ["connectionOptions"] = new JsonArray(new JsonObject {
            ["connectionId"] = "test", ["name"] = "", ["trigger"] = ""
        }),
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
        JsonObject root = ParseRoot(MinimalV15);
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
                Connections
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
