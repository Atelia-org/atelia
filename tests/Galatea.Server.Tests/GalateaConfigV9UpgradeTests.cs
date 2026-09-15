using System.Text;
using System.Text.Json.Nodes;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Galatea.Prompts;
using Atelia.SessionJournal;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaConfigV9UpgradeTests {
    private const string PasswordCanary = "SYNTHETIC-SECRET-v9-NOT-FOR-OUTPUT";
    private static GalateaConfigV9PlayerSelection Selection => new("admin", "Operator", "alice");

    [Fact]
    public async Task DryRunKeepsAllBytesAndDoesNotProvisionMissingState() {
        await using var fixture = new Fixture();
        var before = Snapshot(fixture.Host.RootDirectory);
        var plan = GalateaConfigV9Conversion.BuildPlan(fixture.Path, Selection);
        GalateaConfigV9Upgrade.Preview(plan);
        Assert.Equal(before, Snapshot(fixture.Host.RootDirectory));
        Assert.False(Directory.Exists(fixture.Host.SessionDirectory));
        Assert.False(Directory.Exists(fixture.Host.DelegationStateDirectory));
        Assert.False(Directory.Exists(fixture.Host.CharacterMemoryStateDirectory));
        Assert.Equal("alice", Assert.Single(plan.Config.Characters).CharacterId);
        Assert.Equal(PasswordCanary, Assert.Single(plan.Config.Players).Password);
        Assert.True(plan.Config.Characters[0].HeartbeatEnabled);
    }

    [Fact]
    public async Task ApplyPreservesPathsRuntimeAndOldRelationsWithoutInferringPlayerIdentity() {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
        await using var fixture = new Fixture(fileTemplate: true, twoUsers: true);
        var old = fixture.Root.DeepClone();
        byte[] originalTemplate = File.ReadAllBytes(fixture.TemplatePath);
        var plan = GalateaConfigV9Conversion.BuildPlan(fixture.Path, Selection);
        GalateaConfigV9Upgrade.Apply(plan, fixture.Backup);
        GalateaConfig loaded = GalateaConfigLoader.Load(fixture.Path);
        Assert.Equal(2, loaded.Characters.Count);
        var player = Assert.Single(loaded.Players);
        Assert.Equal("admin", player.PlayerId);
        Assert.Equal("Operator", player.Name.Value);
        Assert.Equal(PasswordCanary, player.Password);
        Assert.Equal("OldVisitor", fixture.Root["users"]![1]!["playerName"]!.GetValue<string>());
        var current = JsonNode.Parse(File.ReadAllText(fixture.Path))!;
        for (int index = 0; index < 2; index++) {
            foreach (string key in new[] { "sessionDir", "delegationStateDir", "characterMemoryStateDir", "homeDir", "defaultConnectionId", "sessionProvisioning", "characterContextTemplateFile" }) {
                Assert.True(JsonNode.DeepEquals(old!["users"]![index]![key], current["characters"]![index]![key]));
            }
            Assert.Equal(old!["users"]![index]!["userId"]!.GetValue<string>(), current["characters"]![index]!["id"]!.GetValue<string>());
            Assert.Equal(old["users"]![index]!["characterName"]!.GetValue<string>(), current["characters"]![index]!["name"]!.GetValue<string>());
        }
        foreach (string field in new[] { "listenUrls", "callLogDir", "maintenanceMode", "recapGrid" }) {
            Assert.True(JsonNode.DeepEquals(old![field], current["runtime"]![field]));
        }
        Assert.Equal(" \r\n${characterName} remembers OldVisitor.\r\n ", File.ReadAllText(fixture.TemplatePath));
        Assert.Equal(originalTemplate, File.ReadAllBytes(System.IO.Path.Combine(fixture.Backup, "0.before")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(System.IO.Path.Combine(fixture.Backup, "1.after")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(fixture.Backup));
    }

    [Fact]
    public async Task SharedTemplateWithDifferentOldPlayerNamesFailsWithoutWrites() {
        await using var fixture = new Fixture(fileTemplate: true, twoUsers: true);
        fixture.Root["users"]![1]!["playerName"] = "OtherVisitor";
        fixture.Save();
        var before = Snapshot(fixture.Host.RootDirectory);
        Assert.Throws<InvalidDataException>(() => GalateaConfigV9Conversion.BuildPlan(fixture.Path, Selection));
        Assert.Equal(before, Snapshot(fixture.Host.RootDirectory));
    }

    [Theory]
    [InlineData("bad-version")]
    [InlineData("duplicate-field")]
    [InlineData("unknown-field")]
    [InlineData("unknown-heartbeat")]
    [InlineData("unknown-player-source")]
    [InlineData("malformed-template")]
    [InlineData("nested-name-token")]
    public async Task InvalidOldInputsAreRejectedWithoutPublishing(string scenario) {
        await using var fixture = new Fixture();
        var selection = Selection;
        switch (scenario) {
            case "bad-version": fixture.Root["v"] = 8; break;
            case "unknown-field": fixture.Root["unknown"] = true; break;
            case "unknown-heartbeat": fixture.Root["serverAgentUserIds"] = new JsonArray("missing"); break;
            case "unknown-player-source": selection = selection with { PasswordFromUser = "missing" }; break;
            case "malformed-template": fixture.Root["users"]![0]!["characterContextTemplate"] = "${characterName} ${playerNam}"; break;
            case "nested-name-token": fixture.Root["users"]![0]!["playerName"] = "${characterName}"; break;
        }
        fixture.Save();
        if (scenario == "duplicate-field") {
            File.WriteAllText(fixture.Path, File.ReadAllText(fixture.Path).Replace("\"v\": 9", "\"v\": 9, \"v\": 9", StringComparison.Ordinal));
        }
        byte[] before = File.ReadAllBytes(fixture.Path);
        Assert.ThrowsAny<Exception>(() => GalateaConfigV9Conversion.BuildPlan(fixture.Path, selection));
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        Assert.False(Directory.Exists(fixture.Backup));
    }

    [Theory]
    [InlineData("before-replace", 0)]
    [InlineData("after-replace", 0)]
    [InlineData("before-replace", 1)]
    [InlineData("after-replace", 1)]
    public async Task PartialPublicationResumesFromDurableOldNewImages(string stage, int fileIndex) {
        await using var fixture = new Fixture(fileTemplate: true);
        var plan = GalateaConfigV9Conversion.BuildPlan(fixture.Path, Selection);
        Assert.Throws<IOException>(() => GalateaConfigV9Upgrade.Apply(plan, fixture.Backup,
            new((at, index) => { if (at == stage && index == fileIndex) { throw new IOException("Injected interruption."); } })));
        if (!(stage == "after-replace" && fileIndex == 1)) {
            Assert.Equal(9, JsonNode.Parse(File.ReadAllText(fixture.Path))!["v"]!.GetValue<int>());
            Assert.Throws<InvalidDataException>(() => GalateaConfigLoader.Load(fixture.Path));
        }
        GalateaConfigV9Upgrade.Resume(fixture.Backup);
        var loaded = GalateaConfigLoader.Load(fixture.Path);
        Assert.Equal("admin", Assert.Single(loaded.Players).PlayerId);
        foreach (var file in plan.Files) { Assert.Equal(file.After, File.ReadAllBytes(file.Path)); }
        GalateaConfigV9Upgrade.Resume(fixture.Backup); // A completed backup can settle the same exact result.
    }

    [Fact]
    public async Task ResumeRefusesThirdPartyEditsAndCorruptBackup() {
        await using var fixture = new Fixture(fileTemplate: true);
        var plan = GalateaConfigV9Conversion.BuildPlan(fixture.Path, Selection);
        Assert.Throws<IOException>(() => GalateaConfigV9Upgrade.Apply(plan, fixture.Backup,
            new((stage, _) => { if (stage == "backup-ready") { throw new IOException(); } })));
        File.WriteAllText(fixture.TemplatePath, "operator changed ${characterName}");
        Assert.Throws<InvalidDataException>(() => GalateaConfigV9Upgrade.Resume(fixture.Backup));
        Assert.Equal("operator changed ${characterName}", File.ReadAllText(fixture.TemplatePath));
        File.WriteAllBytes(fixture.TemplatePath, plan.Files[0].Before);
        File.AppendAllText(System.IO.Path.Combine(fixture.Backup, "0.after"), "corruption");
        Assert.Throws<InvalidDataException>(() => GalateaConfigV9Upgrade.Resume(fixture.Backup));
    }

    [Fact]
    public async Task CancellationBeforePublicationLeavesOldFilesAndResumableBackup() {
        await using var fixture = new Fixture(fileTemplate: true);
        var plan = GalateaConfigV9Conversion.BuildPlan(fixture.Path, Selection);
        using var cancellation = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => GalateaConfigV9Upgrade.Apply(plan, fixture.Backup,
            new((stage, _) => { if (stage == "backup-ready") { cancellation.Cancel(); } }), cancellation.Token));
        foreach (var file in plan.Files) { Assert.Equal(file.Before, File.ReadAllBytes(file.Path)); }
        GalateaConfigV9Upgrade.Resume(fixture.Backup);
        Assert.Equal("admin", Assert.Single(GalateaConfigLoader.Load(fixture.Path).Players).PlayerId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task CancellationAfterPublicationStartsDoesNotReverseSuccessfulResult(int indexToCancel) {
        await using var fixture = new Fixture(fileTemplate: true);
        var plan = GalateaConfigV9Conversion.BuildPlan(fixture.Path, Selection);
        using var cancellation = new CancellationTokenSource();
        GalateaConfigV9Upgrade.Apply(plan, fixture.Backup,
            new((stage, index) => { if (stage == "after-replace" && index == indexToCancel) { cancellation.Cancel(); } }), cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        foreach (var file in plan.Files) { Assert.Equal(file.After, File.ReadAllBytes(file.Path)); }
    }

    [Fact]
    public async Task StateReaderExcludesAnExistingWriterAndChangesNoJournalBytes() {
        await using var fixture = new Fixture();
        using (var engine = SessionJournalEngine.Create(fixture.Host.SessionDirectory,
            new SessionCreateOptions("model-a", "system", "openai-chat/strict"))) { }
        var before = Snapshot(fixture.Host.SessionDirectory);
        var plan = GalateaConfigV9Conversion.BuildPlan(fixture.Path, Selection);
        using (var writer = SessionJournalEngine.Open(fixture.Host.SessionDirectory)) {
            Assert.ThrowsAny<IOException>(() => GalateaConfigV9Upgrade.Preview(plan));
        }
        bool checkedWriter = false;
        GalateaConfigV9Upgrade.Apply(plan, fixture.Backup, new((stage, _) => {
            if (stage == "backup-ready") {
                Assert.ThrowsAny<IOException>(() => SessionJournalEngine.Open(fixture.Host.SessionDirectory));
                checkedWriter = true;
            }
        }));
        Assert.True(checkedWriter);
        Assert.Equal(before, Snapshot(fixture.Host.SessionDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingLifetimeLocksExcludeUpgrade(bool characterMemory) {
        await using var fixture = new Fixture();
        string state = characterMemory ? fixture.Host.CharacterMemoryStateDirectory : fixture.Host.DelegationStateDirectory;
        Directory.CreateDirectory(state);
        string lockPath = System.IO.Path.Combine(state, characterMemory ? "character-memory.lock" : "delegation-state.lock");
        using var occupied = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var plan = GalateaConfigV9Conversion.BuildPlan(fixture.Path, Selection);
        Assert.Throws<IOException>(() => GalateaConfigV9Upgrade.Preview(plan));
        Assert.False(Directory.Exists(fixture.Backup));
    }

    [Fact]
    public async Task SymlinkAndChangedDependencyRejectBeforeBackupOrPublication() {
        await using var fixture = new Fixture(fileTemplate: true);
        var plan = GalateaConfigV9Conversion.BuildPlan(fixture.Path, Selection);
        string connection = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fixture.Path)!, "connections.json");
        File.AppendAllText(connection, " ");
        Assert.Throws<InvalidDataException>(() => GalateaConfigV9Upgrade.Apply(plan, fixture.Backup));
        Assert.False(Directory.Exists(fixture.Backup));
        File.Delete(fixture.TemplatePath);
        File.CreateSymbolicLink(fixture.TemplatePath, fixture.Path);
        Assert.Throws<InvalidDataException>(() => GalateaConfigV9Conversion.BuildPlan(fixture.Path, Selection));
    }

    [Fact]
    public async Task CliRequiresExplicitPlayerChoiceAndNeverPrintsCredentials() {
        await using var fixture = new Fixture();
        using var output = new StringWriter();
        using var error = new StringWriter();
        string[] args = ["operator", "upgrade-config-v9", "--config", fixture.Path,
            "--player-id", "admin", "--player-name", "Operator", "--password-from-user", "alice"];
        Assert.Equal(0, GalateaConfigV9Upgrade.Run(args, output, error));
        Assert.Equal(2, GalateaConfigV9Upgrade.Run(args[..^2], output, error));
        fixture.Root["users"]![0]!["characterName"] = PasswordCanary + "${";
        fixture.Save();
        Assert.Equal(2, GalateaConfigV9Upgrade.Run(args, output, error));
        Assert.DoesNotContain(PasswordCanary, output.ToString() + error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("test-key", output.ToString() + error.ToString(), StringComparison.Ordinal);
    }

    private static string[] Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => System.IO.Path.GetRelativePath(root, path) + ":" + GalateaConfigV9Conversion.Digest(File.ReadAllBytes(path))).ToArray();

    private sealed class Fixture : IAsyncDisposable {
        internal GalateaTestHost Host { get; } = GalateaTestHost.CreateMissingSession(new NoCalls(), DisabledGalateaUserMessageNormalizer.Instance);
        internal string Path => Host.ConfigPath;
        internal string TemplatePath => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, "context.md");
        internal string Backup => System.IO.Path.Combine(Host.RootDirectory, "config-backup");
        internal JsonObject Root { get; }
        internal Fixture(bool fileTemplate = false, bool twoUsers = false) {
            var current = JsonNode.Parse(File.ReadAllText(Path))!.AsObject();
            var character = current["characters"]![0]!.DeepClone().AsObject();
            character["userId"] = character["id"]!.DeepClone();
            character["characterName"] = character["name"]!.DeepClone();
            character.Remove("id"); character.Remove("name"); character.Remove("heartbeatEnabled");
            character["playerName"] = "OldVisitor";
            character["password"] = PasswordCanary;
            character["characterContextTemplate"] = "${characterName} remembers ${playerName}.";
            if (fileTemplate) {
                character["characterContextTemplateFile"] = "context.md";
                File.WriteAllText(TemplatePath, " \r\n${characterName} remembers ${playerName}.\r\n ", new UTF8Encoding(false));
            }
            var users = new JsonArray(character);
            if (twoUsers) {
                var second = character.DeepClone().AsObject();
                second["userId"] = "bob";
                second["characterName"] = "Bob";
                second["password"] = "different-secret";
                foreach (string field in new[] { "sessionDir", "delegationStateDir", "characterMemoryStateDir" }) {
                    second[field] = second[field]!.GetValue<string>() + "-bob";
                }
                string home = second["homeDir"]!.GetValue<string>() + "-bob";
                Directory.CreateDirectory(home);
                second["homeDir"] = home;
                users.Add(second);
            }
            Root = current["runtime"]!.DeepClone().AsObject();
            Root["v"] = 9;
            Root["users"] = users;
            Root["serverAgentUserIds"] = new JsonArray("alice");
            Save();
        }
        internal void Save() => File.WriteAllText(Path, Root.ToJsonString(new() { WriteIndented = true }));
        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }
    private sealed class NoCalls : ICompletionClientFactory {
        public ICompletionClient Create(CompletionConnectionConfig connection) => throw new InvalidOperationException("No provider is allowed in config conversion.");
    }
}
