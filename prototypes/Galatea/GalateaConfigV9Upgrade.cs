using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server;

internal sealed record GalateaConfigV9UpgradeHooks(Action<string, int>? Checkpoint = null);
internal sealed record GalateaConfigV9BackupFile(string Path, string BeforeSha256, string AfterSha256, int Mode);
internal sealed record GalateaConfigV9Backup(int V, string ConfigPath, GalateaConfigV9BackupFile[] Files,
    GalateaConfigV9Input[] Dependencies);

/// <summary>One-time offline config/template publication, with config installed last.</summary>
internal static class GalateaConfigV9Upgrade {
    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };
    internal static bool IsInvocation(string[] args) => args.Length >= 2
        && args[0] == "operator" && args[1] == "upgrade-config-v9";

    internal static int Run(string[] args, TextWriter output, TextWriter error) {
        try {
            var options = Parse(args);
            if (options.Resume is not null) {
                Resume(options.Resume);
                output.WriteLine("Configuration upgrade completed from its private backup.");
                return 0;
            }
            var plan = GalateaConfigV9Conversion.BuildPlan(options.Config!,
                new(options.PlayerId!, options.PlayerName!, options.PasswordFromUser!));
            if (options.Apply) { Apply(plan, options.Backup!); }
            else { Preview(plan); }
            output.WriteLine(options.Apply ? "Configuration upgrade applied." : "Configuration upgrade dry-run ready; no files changed.");
            output.WriteLine($"Characters: {plan.Config.Characters.Count}; Players: {plan.Config.Players.Count}; template files: {plan.Files.Count - 1}.");
            // Deliberately do not print source strings, names, paths, JSON, passwords or hashes of credentials.
            return 0;
        }
        catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) {
            // Parser and loader exception messages can contain operator-controlled values.
            error.WriteLine("Configuration upgrade failed. Keep the server stopped; inspect inputs or resume the private backup. "
                + "No credentials are included in this diagnostic. Failure type: " + exception.GetType().Name);
            return 2;
        }
    }

    internal static void Preview(GalateaConfigV9Plan plan) {
        using var locks = AcquireOfflineLocks(plan.Config);
        VerifyDependencies(plan);
        VerifyFiles(plan, allowAfter: false);
    }

    internal static void Apply(GalateaConfigV9Plan plan, string backupDirectory,
        GalateaConfigV9UpgradeHooks? hooks = null, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        using var locks = AcquireOfflineLocks(plan.Config);
        VerifyDependencies(plan);
        VerifyFiles(plan, allowAfter: false);
        string backup = Path.GetFullPath(backupDirectory);
        RequireBackupLocation(plan, backup);
        GalateaDelegationDurableFiles.CreateDirectoryNew(backup);
        for (int index = 0; index < plan.Files.Count; index++) {
            cancellationToken.ThrowIfCancellationRequested();
            WriteNew(Path.Combine(backup, $"{index}.before"), plan.Files[index].Before);
            WriteNew(Path.Combine(backup, $"{index}.after"), plan.Files[index].After);
        }
        var manifest = new GalateaConfigV9Backup(1, plan.ConfigPath, plan.Files.Select(file => new GalateaConfigV9BackupFile(
            file.Path, GalateaConfigV9Conversion.Digest(file.Before), GalateaConfigV9Conversion.Digest(file.After), (int)file.Mode)).ToArray(),
            plan.Dependencies.ToArray());
        WriteNew(Path.Combine(backup, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
        GalateaDelegationDurableFiles.FlushDirectory(backup);
        GalateaDelegationDurableFiles.FlushDirectory(Path.GetDirectoryName(backup)!);
        hooks?.Checkpoint?.Invoke("backup-ready", -1);
        Publish(plan, hooks, cancellationToken);
    }

    internal static void Resume(string backupDirectory, GalateaConfigV9UpgradeHooks? hooks = null,
        CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        string backup = Path.GetFullPath(backupDirectory);
        byte[] manifestBytes = GalateaConfigV9Conversion.Read(Path.Combine(backup, "manifest.json"));
        using (JsonDocument parsed = JsonDocument.Parse(manifestBytes)) { GalateaConfigV9Conversion.CheckDuplicates(parsed.RootElement); }
        var manifest = JsonSerializer.Deserialize<GalateaConfigV9Backup>(manifestBytes, JsonOptions)
            ?? throw GalateaConfigV9Conversion.Invalid();
        if (manifest.V != 1 || manifest.Files is not { Length: > 0 and <= 257 }
            || manifest.Dependencies is null || !Path.IsPathFullyQualified(manifest.ConfigPath)) { throw GalateaConfigV9Conversion.Invalid(); }
        var files = new List<GalateaConfigV9File>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < manifest.Files.Length; index++) {
            var entry = manifest.Files[index];
            if (entry is null || !Path.IsPathFullyQualified(entry.Path) || Path.GetFullPath(entry.Path) != entry.Path || !seen.Add(entry.Path)
                || entry.Mode < 0 || (entry.Mode & ~0x1FF) != 0) { throw GalateaConfigV9Conversion.Invalid(); }
            byte[] before = GalateaConfigV9Conversion.Read(Path.Combine(backup, $"{index}.before"));
            byte[] after = GalateaConfigV9Conversion.Read(Path.Combine(backup, $"{index}.after"));
            if (GalateaConfigV9Conversion.Digest(before) != entry.BeforeSha256
                || GalateaConfigV9Conversion.Digest(after) != entry.AfterSha256) { throw GalateaConfigV9Conversion.Invalid(); }
            files.Add(new(entry.Path, before, after, (UnixFileMode)entry.Mode));
        }
        if (files[^1].Path != manifest.ConfigPath) { throw GalateaConfigV9Conversion.Invalid(); }
        byte[] candidate = files[^1].After;
        GalateaStrictConfigReader.ValidateRoot(candidate);
        using JsonDocument document = JsonDocument.Parse(candidate);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var usedTemplates = new HashSet<string>(StringComparer.Ordinal);
        string configDirectory = Path.GetDirectoryName(manifest.ConfigPath)!;
        foreach (JsonElement character in document.RootElement.GetProperty("characters").EnumerateArray()) {
            string id = character.GetProperty("id").GetString()!;
            string? template = character.TryGetProperty("characterContextTemplateFile", out var file) && file.ValueKind != JsonValueKind.Null
                ? file.GetString() : null;
            if (string.IsNullOrWhiteSpace(template)) {
                sources.Add(id, character.GetProperty("characterContextTemplate").GetString()!);
            }
            else {
                string path = Path.GetFullPath(template, configDirectory);
                var entry = files.Single(value => value.Path == path && value.Path != manifest.ConfigPath);
                sources.Add(id, GalateaConfigV9Conversion.Utf8.GetString(entry.After).Trim());
                usedTemplates.Add(path);
            }
        }
        if (usedTemplates.Count != files.Count - 1) { throw GalateaConfigV9Conversion.Invalid(); }
        GalateaConfig config = GalateaConfigLoader.ValidateConfigUpgradeCandidate(manifest.ConfigPath, candidate, sources);
        var plan = new GalateaConfigV9Plan(manifest.ConfigPath, config, files, manifest.Dependencies);
        using var locks = AcquireOfflineLocks(config);
        VerifyDependencies(plan);
        VerifyFiles(plan, allowAfter: true);
        // A V10 root is only valid with all of this plan's templates already installed.
        if (MatchesCurrent(files[^1], after: true) && files.Any(file => !MatchesCurrent(file, after: true))) {
            throw new InvalidDataException("Committed configuration no longer matches its converted templates.");
        }
        Publish(plan, hooks, cancellationToken);
    }

    private static void Publish(GalateaConfigV9Plan plan, GalateaConfigV9UpgradeHooks? hooks, CancellationToken token) {
        // Cancellation is observed only before the first destination change. Once publication starts,
        // finish the finite file set; an I/O failure leaves the durable backup available to Resume.
        token.ThrowIfCancellationRequested();
        VerifyDependencies(plan);
        VerifyFiles(plan, allowAfter: true);
        for (int index = 0; index < plan.Files.Count; index++) {
            GalateaConfigV9File file = plan.Files[index];
            string directory = Path.GetDirectoryName(file.Path)!;
            if (MatchesCurrent(file, after: true)) {
                // Also settles a rename that happened before a crashed directory fsync.
                GalateaDelegationDurableFiles.FlushDirectory(directory);
                continue;
            }
            if (!MatchesCurrent(file, after: false)) { throw GalateaConfigV9Conversion.Invalid(); }
            string temporary = Path.Combine(directory, ".galatea-v9-upgrade-" + Guid.NewGuid().ToString("N"));
            WriteNew(temporary, file.After, file.Mode);
            hooks?.Checkpoint?.Invoke("before-replace", index);
            // Recheck the authoritative destination after the fallible preparation seam.
            if (!MatchesCurrent(file, after: false)) { throw GalateaConfigV9Conversion.Invalid(); }
            File.Move(temporary, file.Path, overwrite: true);
            hooks?.Checkpoint?.Invoke("after-replace", index);
            GalateaDelegationDurableFiles.FlushDirectory(directory);
        }
        // No cancellation or fallible callback after the final config's proven publication.
    }

    private static bool MatchesCurrent(GalateaConfigV9File file, bool after)
        => GalateaConfigV9Conversion.Read(file.Path).AsSpan().SequenceEqual(after ? file.After : file.Before);
    private static void VerifyFiles(GalateaConfigV9Plan plan, bool allowAfter) {
        foreach (var file in plan.Files) {
            if (!MatchesCurrent(file, after: false) && !(allowAfter && MatchesCurrent(file, after: true))) {
                throw new InvalidDataException("An upgrade input has changed; refusing to overwrite it.");
            }
        }
    }
    private static void VerifyDependencies(GalateaConfigV9Plan plan) {
        foreach (var dependency in plan.Dependencies) {
            if (GalateaConfigV9Conversion.Digest(GalateaConfigV9Conversion.Read(dependency.Path)) != dependency.Sha256) {
                throw new InvalidDataException("A configuration dependency changed during the upgrade.");
            }
        }
    }

    private static void WriteNew(string path, byte[] bytes,
        UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite) {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Offline configuration upgrade requires Linux."); }
        GalateaStrictConfigReader.RequireExistingAncestorsNoReparse(path, "configuration upgrade output");
        using var stream = new FileStream(path, new FileStreamOptions {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            UnixCreateMode = mode
        });
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void RequireBackupLocation(GalateaConfigV9Plan plan, string backup) {
        GalateaStrictConfigReader.RequireExistingAncestorsNoReparse(backup, "configuration upgrade backup");
        foreach (var character in plan.Config.Characters) {
            foreach (string state in new[] { character.SessionDir, character.DelegationStateDir, character.CharacterMemoryStateDir, character.HomeDir }) {
                if (backup == state || backup.StartsWith(state + Path.DirectorySeparatorChar, StringComparison.Ordinal)) {
                    throw new InvalidDataException("Backup must be outside character state and home directories.");
                }
            }
        }
    }

    private static OfflineLocks AcquireOfflineLocks(GalateaConfig config) {
        var locks = new OfflineLocks();
        try {
            foreach (var character in config.Characters.OrderBy(value => value.CharacterId, StringComparer.Ordinal)) {
                foreach (var entry in new[] {
                    (character.DelegationStateDir, GalateaDelegationSqliteStore.LockFileName),
                    (character.CharacterMemoryStateDir, CharacterMemory.CharacterMemorySqliteStore.LockFileName)
                }) {
                    if (!Directory.Exists(entry.Item1)) { continue; }
                    string path = Path.Combine(entry.Item1, entry.Item2);
                    GalateaStrictConfigReader.RequireExistingAncestorsNoReparse(path, "existing store lock");
                    locks.Items.Add(new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
                }
                if (Directory.Exists(character.SessionDir)) {
                    // The installed storage reader holds shared RBF handles that exclude its exclusive writer;
                    // this path never invokes recovery, truncation or provisioning.
                    locks.Items.Add(SessionJournalEngine.OpenReadOnly(character.SessionDir));
                }
                else if (character.SessionProvisioning == GalateaSessionProvisioning.ExistingOnly) {
                    throw new InvalidDataException("An existing-only session is missing.");
                }
            }
            return locks;
        }
        catch { locks.Dispose(); throw; }
    }

    private sealed class OfflineLocks : IDisposable {
        internal List<IDisposable> Items { get; } = [];
        public void Dispose() {
            for (int index = Items.Count - 1; index >= 0; index--) { Items[index].Dispose(); }
        }
    }

    private sealed record Options(string? Config, string? PlayerId, string? PlayerName,
        string? PasswordFromUser, string? Backup, string? Resume, bool Apply);
    private static Options Parse(string[] args) {
        if (!IsInvocation(args)) { throw GalateaConfigV9Conversion.Invalid(); }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        bool apply = false;
        for (int index = 2; index < args.Length; index++) {
            string key = args[index];
            if (key == "--apply" && !apply) { apply = true; continue; }
            if (key is not ("--config" or "--player-id" or "--player-name" or "--password-from-user" or "--backup-dir" or "--resume")
                || index + 1 >= args.Length || !values.TryAdd(key, args[++index])) { throw GalateaConfigV9Conversion.Invalid(); }
        }
        string? Get(string key) => values.GetValueOrDefault(key);
        if (Get("--resume") is string resume) {
            if (!apply || values.Count != 1 || !Path.IsPathFullyQualified(resume)) { throw GalateaConfigV9Conversion.Invalid(); }
        }
        else {
            foreach (string key in new[] { "--config", "--player-id", "--player-name", "--password-from-user" }) {
                if (string.IsNullOrWhiteSpace(Get(key))) { throw GalateaConfigV9Conversion.Invalid(); }
            }
            if (!Path.IsPathFullyQualified(Get("--config")!) || (apply != (Get("--backup-dir") is not null))
                || (Get("--backup-dir") is string backup && !Path.IsPathFullyQualified(backup))) { throw GalateaConfigV9Conversion.Invalid(); }
        }
        return new(Get("--config"), Get("--player-id"), Get("--player-name"), Get("--password-from-user"), Get("--backup-dir"), Get("--resume"), apply);
    }
}
