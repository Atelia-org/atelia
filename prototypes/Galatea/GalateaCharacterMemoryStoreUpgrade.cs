using Atelia.Galatea.Server.CharacterMemory;

namespace Atelia.Galatea.Server;

/// <summary>Explicit, provider-free offline maintenance of one configured Character's memory ledger.</summary>
internal static class GalateaCharacterMemoryStoreUpgrade {
    internal const string CommandName = "upgrade-character-memory-store";
    internal static bool IsInvocation(string[] args) => args.Length >= 2
        && args[0] == "operator" && args[1] == CommandName;

    internal static int Run(string[] args, TextWriter output, TextWriter error) {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        try {
            (string configPath, string characterId, bool apply) = Parse(args);
            GalateaConfig config = GalateaConfigLoader.Load(configPath);
            GalateaCharacterConfig character = config.Characters.SingleOrDefault(value => value.CharacterId == characterId)
                ?? throw new InvalidDataException("The requested Character is not configured.");
            var owner = new CharacterMemoryStoreOwner(character.CharacterId,
                CharacterMemorySessionComposition.CreateSessionRepositoryId(character.SessionDir));
            CharacterMemoryStoreUpgradeResult result = CharacterMemorySqliteStore.UpgradeExisting(
                character.CharacterMemoryStateDir, owner, apply);
            output.WriteLine($"Character Memory upgrade: characterId={character.CharacterId}, outcome={result.Outcome}, sourceVersion={result.SourceVersion}, targetVersion={result.TargetVersion}.");
            if (result.BackupPath is not null) { output.WriteLine("Backup: " + result.BackupPath); }
            return 0;
        }
        catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) {
            error.WriteLine("Character Memory upgrade failed: " + exception.Message);
            return 2;
        }
    }

    private static (string ConfigPath, string CharacterId, bool Apply) Parse(string[] args) {
        if (!IsInvocation(args)) { throw Usage(); }
        string? config = null;
        string? character = null;
        bool apply = false;
        for (int i = 2; i < args.Length; i++) {
            switch (args[i]) {
                case "--config" when config is null && i + 1 < args.Length: config = args[++i]; break;
                case "--character" when character is null && i + 1 < args.Length: character = args[++i]; break;
                case "--apply" when !apply: apply = true; break;
                default: throw Usage();
            }
        }
        if (string.IsNullOrWhiteSpace(config) || !Path.IsPathFullyQualified(config) || string.IsNullOrWhiteSpace(character)) { throw Usage(); }
        return (config, character, apply);
    }

    private static InvalidDataException Usage() => new(
        "Usage: Galatea.Server operator upgrade-character-memory-store --config <absolute-path> --character <characterId> [--apply]. The default is dry-run.");
}
