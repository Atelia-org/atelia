using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion;
using Atelia.Galatea.Server.Mailbox;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaImapConfigTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrEmptyPolicyDefaultsToDisabledWithoutAnImapAccount(bool emptyPolicy) {
        using var fixture = new ConfigFixture();
        JsonObject root = fixture.CreateRoot();
        if (emptyPolicy) { root["runtime"]!["imap"] = new JsonObject(); }
        GalateaConfig loaded = fixture.Load(root);
        Assert.False(loaded.Imap.Enabled);
        Assert.Equal(60, loaded.Imap.PollIntervalSeconds);
        Assert.Equal(60, loaded.Imap.TimeoutSeconds);
        Assert.Empty(loaded.Imap.Accounts);
        Assert.Null(loaded.Imap.ReferenceFor("alice"));
    }

    [Fact]
    public void LoaderUsesOneImmutableEmailAccountForBothProtocolViews() {
        using var fixture = new ConfigFixture();
        JsonObject root = fixture.CreateRoot();
        root["characters"]![0]!["email"] = EmailNode();
        root["runtime"]!["imap"] = new JsonObject {
            ["enabled"] = true, ["pollIntervalSeconds"] = 21, ["timeoutSeconds"] = 17
        };
        GalateaConfig loaded = fixture.Load(root);
        Assert.True(loaded.Imap.Enabled);
        Assert.False(loaded.Smtp.Enabled);
        Assert.Equal(21, loaded.Imap.PollIntervalSeconds);
        Assert.Equal(17, loaded.Imap.TimeoutSeconds);
        GalateaEmailAccount account = loaded.Imap.Accounts["alice"];
        Assert.Same(loaded.Smtp.Accounts["alice"], account);
        Assert.Equal("imap.example.test", account.Imap!.Host);
        Assert.Equal(["Friend@Example.test"], account.Imap.AutoDisplaySenders);
        Assert.Equal(GalateaImapConfig.AccountReference("alice", account), loaded.Imap.ReferenceFor("alice"));
        Assert.True(GalateaImapConfig.IsReferenceFor(loaded.Imap.ReferenceFor("alice"), "alice"));
        Assert.False(GalateaImapConfig.IsReferenceFor(loaded.Imap.ReferenceFor("alice"), "bob"));
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, GalateaEmailAccount>)loaded.Imap.Accounts).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)account.Imap.AutoDisplaySenders).Clear());
        root["characters"]![0]!["email"]!["imap"]!["autoDisplaySenders"]![0] = "later@example.test";
        Assert.Equal("Friend@Example.test", Assert.Single(account.Imap.AutoDisplaySenders));
    }

    [Fact]
    public void ProgrammaticDescriptorCopiesSenderArrayAndBothViewsRetainTheSameAccount() {
        string[] senders = ["Friend@example.test"];
        var descriptor = new GalateaImapAccount("imap.example.test", 993, "implicit", senders);
        senders[0] = "changed@example.test";
        Assert.Equal("Friend@example.test", Assert.Single(descriptor.AutoDisplaySenders));
        var account = Account(imap: descriptor);
        var accounts = new Dictionary<string, GalateaEmailAccount> { ["alice"] = account };
        var smtp = new GalateaSmtpConfig(null, accounts);
        var imap = new GalateaImapConfig(null, accounts);
        accounts.Clear();
        Assert.Same(smtp.Accounts["alice"], imap.Accounts["alice"]);
    }

    [Fact]
    public void MissingOrNullDescriptorDisablesOnlyThatCharactersImap() {
        using var fixture = new ConfigFixture();
        JsonObject root = fixture.CreateRoot();
        root["runtime"]!["imap"] = new JsonObject { ["enabled"] = true };
        JsonObject email = EmailNode();
        email.Remove("imap");
        root["characters"]![0]!["email"] = email;
        Assert.Empty(fixture.Load(root).Imap.Accounts);
        email["imap"] = null;
        Assert.Empty(fixture.Load(root).Imap.Accounts);
        Assert.Single(fixture.Load(root).Smtp.Accounts);
    }

    [Fact]
    public void AddressComparisonKeepsLocalPartExactAndDoesNotFoldAliases() {
        Assert.True(GalateaExternalMailAddress.SameMailbox("Friend@EXAMPLE.test", "Friend@example.TEST"));
        Assert.False(GalateaExternalMailAddress.SameMailbox("Friend@example.test", "friend@example.test"));
        Assert.False(GalateaExternalMailAddress.SameMailbox("friend+tag@example.test", "friend@example.test"));
        Assert.False(GalateaExternalMailAddress.SameMailbox("friend@example.test", "friend@another.test"));
        Assert.False(GalateaExternalMailAddress.SameMailbox(" Friend@example.test", "Friend@example.test"));
        Assert.False(GalateaExternalMailAddress.SameMailbox(null, "Friend@example.test"));
        Assert.False(GalateaExternalMailAddress.SameMailbox("bad", "bad"));
    }

    [Fact]
    public void ImapNamespaceIgnoresSecretsListsAndSmtpButFreezesReceivingDescriptor() {
        GalateaEmailAccount account = Account();
        string reference = GalateaImapConfig.AccountReference("alice", account);
        Assert.StartsWith("imap:alice:", reference);
        Assert.Equal(reference, GalateaImapConfig.AccountReference("alice", Account(secret: "rotated-secret")));
        Assert.Equal(reference, GalateaImapConfig.AccountReference("alice", Account(
            imap: new("imap.example.test", 993, "implicit", ["another@example.test"]))));
        Assert.Equal(reference, GalateaImapConfig.AccountReference("alice", new(
            account.Address, account.AuthorizationCode, "another-smtp.example.test", 587,
            "starttls", account.Imap)));
        foreach (GalateaEmailAccount changed in new[] {
                     Account(address: "other@Example.test"),
                     Account(imap: new("another-imap.example.test", 993, "implicit")),
                     Account(imap: new("imap.example.test", 143, "implicit")),
                     Account(imap: new("imap.example.test", 993, "starttls"))
                 }) {
            Assert.NotEqual(reference, GalateaImapConfig.AccountReference("alice", changed));
        }
        Assert.NotEqual(reference, GalateaImapConfig.AccountReference("bob", account));
        Assert.Equal("INBOX", GalateaImapConfig.MailboxName);
        Assert.Equal("smtp:alice:ca7fc12cc6704682616a20058d7bc07fbc41de74b2e37987affbe8e50b180cc9",
            GalateaSmtpConfig.AccountReference("alice", account));
        Assert.Equal(GalateaSmtpConfig.AccountReference("alice", account),
            GalateaSmtpConfig.AccountReference("alice", Account(imap: new("other.example.test", 143, "starttls"))));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3600, 300)]
    public void StrictReaderAcceptsPolicyBoundaryValues(int interval, int timeout) {
        using var fixture = new ConfigFixture();
        JsonObject root = fixture.CreateRoot();
        root["characters"]![0]!["email"] = EmailNode();
        root["runtime"]!["imap"] = new JsonObject {
            ["enabled"] = true, ["pollIntervalSeconds"] = interval, ["timeoutSeconds"] = timeout
        };
        GalateaConfig loaded = fixture.Load(root);
        Assert.Equal(interval, loaded.Imap.PollIntervalSeconds);
        Assert.Equal(timeout, loaded.Imap.TimeoutSeconds);
    }

    [Theory]
    [InlineData("policy-null")]
    [InlineData("policy-unknown")]
    [InlineData("policy-enabled-null")]
    [InlineData("interval-zero")]
    [InlineData("interval-too-large")]
    [InlineData("interval-fraction")]
    [InlineData("timeout-zero")]
    [InlineData("timeout-too-large")]
    [InlineData("missing-host")]
    [InlineData("missing-port")]
    [InlineData("missing-tls")]
    [InlineData("host-null")]
    [InlineData("url-host")]
    [InlineData("unicode-host")]
    [InlineData("port-zero")]
    [InlineData("port-too-large")]
    [InlineData("port-string")]
    [InlineData("plaintext-tls")]
    [InlineData("unknown-account")]
    [InlineData("list-null")]
    [InlineData("list-nonarray")]
    [InlineData("list-null-entry")]
    [InlineData("list-invalid")]
    [InlineData("list-whitespace")]
    [InlineData("list-wildcard")]
    [InlineData("list-duplicate-domain-case")]
    [InlineData("list-too-large")]
    public void StrictReaderRejectsInvalidImapEvenWhileRuntimeDisabled(string mode) {
        using var fixture = new ConfigFixture();
        JsonObject root = fixture.CreateRoot();
        JsonObject email = EmailNode();
        root["characters"]![0]!["email"] = email;
        var policy = new JsonObject { ["enabled"] = false };
        root["runtime"]!["imap"] = policy;
        JsonObject imap = email["imap"]!.AsObject();
        switch (mode) {
            case "policy-null": root["runtime"]!["imap"] = null; break;
            case "policy-unknown": policy["unknown"] = 1; break;
            case "policy-enabled-null": policy["enabled"] = null; break;
            case "interval-zero": policy["pollIntervalSeconds"] = 0; break;
            case "interval-too-large": policy["pollIntervalSeconds"] = 3601; break;
            case "interval-fraction": policy["pollIntervalSeconds"] = 1.5; break;
            case "timeout-zero": policy["timeoutSeconds"] = 0; break;
            case "timeout-too-large": policy["timeoutSeconds"] = 301; break;
            case "missing-host": imap.Remove("host"); break;
            case "missing-port": imap.Remove("port"); break;
            case "missing-tls": imap.Remove("tlsMode"); break;
            case "host-null": imap["host"] = null; break;
            case "url-host": imap["host"] = "https://imap.example.test"; break;
            case "unicode-host": imap["host"] = "邮箱.example.test"; break;
            case "port-zero": imap["port"] = 0; break;
            case "port-too-large": imap["port"] = 65536; break;
            case "port-string": imap["port"] = "993"; break;
            case "plaintext-tls": imap["tlsMode"] = "none"; break;
            case "unknown-account": imap["folder"] = "INBOX"; break;
            case "list-null": imap["autoDisplaySenders"] = null; break;
            case "list-nonarray": imap["autoDisplaySenders"] = "friend@example.test"; break;
            case "list-null-entry": imap["autoDisplaySenders"] = new JsonArray((JsonNode?)null); break;
            case "list-invalid": imap["autoDisplaySenders"] = new JsonArray("Name <friend@example.test>"); break;
            case "list-whitespace": imap["autoDisplaySenders"] = new JsonArray(" friend@example.test"); break;
            case "list-wildcard": imap["autoDisplaySenders"] = new JsonArray("friend@*.example.test"); break;
            case "list-duplicate-domain-case": imap["autoDisplaySenders"] = new JsonArray("Friend@example.test", "Friend@EXAMPLE.test"); break;
            case "list-too-large": imap["autoDisplaySenders"] = StringArray(Enumerable.Range(0, 129).Select(i => $"friend{i}@example.test")); break;
        }
        Assert.Throws<InvalidDataException>(() => fixture.Load(root));
    }

    [Fact]
    public void SenderListAllowsExactLocalCaseAndTagsAndDefaultsToEmpty() {
        using var fixture = new ConfigFixture();
        JsonObject root = fixture.CreateRoot();
        JsonObject email = EmailNode();
        root["characters"]![0]!["email"] = email;
        email["imap"]!["autoDisplaySenders"] = new JsonArray("Friend@example.test", "friend@example.test", "friend+tag@example.test");
        Assert.Equal(3, fixture.Load(root).Imap.Accounts["alice"].Imap!.AutoDisplaySenders.Count);
        email["imap"]!.AsObject().Remove("autoDisplaySenders");
        Assert.Empty(fixture.Load(root).Imap.Accounts["alice"].Imap!.AutoDisplaySenders);
        email["imap"]!["autoDisplaySenders"] = StringArray(Enumerable.Range(0, 128).Select(i => $"friend{i}@example.test"));
        Assert.Equal(128, fixture.Load(root).Imap.Accounts["alice"].Imap!.AutoDisplaySenders.Count);
    }

    [Theory]
    [InlineData("account-unknown")]
    [InlineData("account-duplicate")]
    [InlineData("policy-unknown")]
    [InlineData("policy-duplicate")]
    [InlineData("invalid-host")]
    [InlineData("invalid-list")]
    [InlineData("malformed-json")]
    public void ConfigurationErrorsNeverIncludeSecretOrUnknownInputText(string mode) {
        const string Secret = "IMAP_CONFIGURATION_SECRET_SENTINEL";
        using var fixture = new ConfigFixture();
        JsonObject root = fixture.CreateRoot();
        JsonObject email = EmailNode();
        root["characters"]![0]!["email"] = email;
        email["authorizationCode"] = Secret;
        root["runtime"]!["imap"] = new JsonObject { ["enabled"] = false };
        JsonObject imap = email["imap"]!.AsObject();
        switch (mode) {
            case "account-unknown": imap[Secret] = Secret; break;
            case "policy-unknown": root["runtime"]!["imap"]![Secret] = Secret; break;
            case "invalid-host": imap["host"] = Secret + "/invalid"; break;
            case "invalid-list": imap["autoDisplaySenders"] = new JsonArray(Secret); break;
        }
        string json = root.ToJsonString();
        if (mode == "account-duplicate") { json = json.Replace("\"host\":\"imap.example.test\"", "\"host\":\"imap.example.test\",\"HOST\":\"" + Secret + "\"", StringComparison.Ordinal); }
        if (mode == "policy-duplicate") { json = json.Replace("\"enabled\":false", "\"enabled\":false,\"enabled\":\"" + Secret + "\"", StringComparison.Ordinal); }
        if (mode == "malformed-json") { json = json[..^1] + Secret; }
        InvalidDataException failure = Assert.Throws<InvalidDataException>(() => fixture.Load(json));
        Assert.DoesNotContain(Secret, failure.ToString(), StringComparison.Ordinal);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public void ImapSettingsRemainHostOnlyAndRecordPrintingDoesNotExpandSecretsOrLists() {
        const string Secret = "IMAP_RUNTIME_SECRET_SENTINEL";
        const string Sender = "unique-admitted-sender@example.test";
        using var fixture = new ConfigFixture();
        JsonObject root = fixture.CreateRoot();
        JsonObject email = EmailNode();
        email["authorizationCode"] = Secret;
        email["imap"]!["autoDisplaySenders"] = new JsonArray(Sender);
        root["characters"]![0]!["email"] = email;
        GalateaConfig loaded = fixture.Load(root);
        Assert.Null(typeof(GalateaConfig).GetProperty("Imap"));
        Assert.DoesNotContain(Secret, loaded.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Sender, loaded.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, loaded.Imap.Accounts["alice"].ToString()!, StringComparison.Ordinal);
        Assert.DoesNotContain(Sender, loaded.Imap.Accounts["alice"].Imap!.ToString()!, StringComparison.Ordinal);
        string publicJson = JsonSerializer.Serialize(loaded, GalateaJson.Options);
        Assert.DoesNotContain(Secret, publicJson, StringComparison.Ordinal);
        Assert.DoesNotContain(Sender, publicJson, StringComparison.Ordinal);
    }

    [Fact]
    public void BootstrapWritesCurrentVersionWithReceivingDisabled() {
        GalateaRootFileConfig root = GalateaConfigTemplateFactory.CreateRootFile();
        Assert.Equal(16, root.Version);
        Assert.False(root.Runtime.Imap!.Enabled);
        Assert.Equal(60, root.Runtime.Imap.PollIntervalSeconds);
        Assert.Equal(60, root.Runtime.Imap.TimeoutSeconds);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(root, GalateaJsonContext.Default.GalateaRootFileConfig);
        GalateaStrictConfigReader.ValidateRoot(bytes);
    }

    private static GalateaEmailAccount Account(string address = "host@Example.test",
        string secret = "synthetic-code", GalateaImapAccount? imap = null) =>
        new(address, secret, "smtp.example.test", 465, "implicit",
            imap ?? new("imap.example.test", 993, "implicit"));

    private static JsonObject EmailNode() => new() {
        ["address"] = "host@Example.test", ["authorizationCode"] = "synthetic-code",
        ["smtpHost"] = "smtp.example.test", ["smtpPort"] = 465, ["tlsMode"] = "implicit",
        ["imap"] = new JsonObject {
            ["host"] = "imap.example.test", ["port"] = 993, ["tlsMode"] = "implicit",
            ["autoDisplaySenders"] = new JsonArray("Friend@Example.test")
        }
    };

    private static JsonArray StringArray(IEnumerable<string> values) =>
        new(values.Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray());

    private sealed class ConfigFixture : IDisposable {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "atelia-imap-config-tests", Guid.NewGuid().ToString("N"));
        internal ConfigFixture() {
            Directory.CreateDirectory(_directory);
            GalateaTestHost.WriteDelegatesFile(_directory);
            GalateaTestHost.WriteConnectionsFile(Path.Combine(_directory, GalateaConfigLoader.ConnectionsFileName), [
                new CompletionConnectionConfig("test", "openai-chat", "model-a", "openai-chat/strict", "http://localhost:8000/", ApiKey: "test-key")
            ]);
        }

        internal JsonObject CreateRoot() {
            var root = new GalateaRootFileConfig(GalateaStrictConfigReader.CurrentConfigVersion,
                [new GalateaCharacterFileConfig("alice", "Alice", "sessions/alice", "delegation-state/alice", "character-memory/alice",
                    Directory.CreateDirectory(Path.Combine(_directory, "homes/alice")).FullName, GalateaSessionProvisioning.ExistingOnly,
                    "test", [new GalateaCharacterConnectionOption("test", "", "")], CharacterContextTemplate: "inline ${characterName}")],
                [new GalateaPlayerFileConfig("player", "Player", "pw")],
                new GalateaRuntimeFileConfig(RecapGrid: new GalateaRecapGridFileConfig(
                    new GalateaRecapGridMaintenanceFileConfig("test", 1, 900_000))));
            return JsonSerializer.SerializeToNode(root, GalateaJsonContext.Default.GalateaRootFileConfig)!.AsObject();
        }

        internal GalateaConfig Load(JsonObject root) => Load(root.ToJsonString());
        internal GalateaConfig Load(string json) {
            string path = Path.Combine(_directory, "config.json");
            File.WriteAllText(path, json, new UTF8Encoding(false));
            return GalateaConfigLoader.Load(path);
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
