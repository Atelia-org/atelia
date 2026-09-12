namespace Atelia.Galatea.Server;

/// <summary>Offline format maintenance; never constructs the host or a provider.</summary>
internal static class GalateaDelegationStoreUpgrade {
    internal const string CommandName = "upgrade-delegation-store";

    internal static bool IsInvocation(string[] args) => args.Length >= 2
        && args[0] == "operator" && args[1] == CommandName;

    internal static int Run(string[] args, TextWriter output, TextWriter error) {
        try {
            (string configPath, string userId, bool apply) = Parse(args);
            GalateaConfig config = GalateaConfigLoader.Load(configPath);
            GalateaUserConfig user = config.Users.SingleOrDefault(value => value.UserId == userId)
                ?? throw new InvalidDataException("The requested user is not configured.");
            var owner = new GalateaDelegationStoreOwner(
                user.UserId,
                GalateaDelegationSupervisor.CreateSessionRepositoryId(user.SessionDir)
            );
            GalateaDelegationStoreUpgradeResult result = GalateaDelegationSqliteStore.UpgradeExisting(
                user.DelegationStateDir,
                owner,
                GalateaDelegationSupervisor.CreateLimits(config.Delegates.CodexRoute),
                apply
            );
            output.WriteLine($"Delegation store upgrade: userId={user.UserId}, outcome={result.Outcome}.");
            if (result.BackupPath is not null) { output.WriteLine($"Backup: {result.BackupPath}"); }
            return 0;
        }
        catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) {
            error.WriteLine("Delegation store upgrade failed: " + exception.Message);
            return 2;
        }
    }

    private static (string ConfigPath, string UserId, bool Apply) Parse(string[] args) {
        if (!IsInvocation(args)) { throw Usage(); }
        string? configPath = null;
        string? userId = null;
        bool apply = false;
        for (int index = 2; index < args.Length; index++) {
            switch (args[index]) {
                case "--config" when configPath is null && index + 1 < args.Length:
                    configPath = args[++index];
                    break;
                case "--user" when userId is null && index + 1 < args.Length:
                    userId = args[++index];
                    break;
                case "--apply" when !apply:
                    apply = true;
                    break;
                default:
                    throw Usage();
            }
        }
        if (string.IsNullOrWhiteSpace(configPath) || !Path.IsPathFullyQualified(configPath)
            || string.IsNullOrWhiteSpace(userId)) { throw Usage(); }
        return (configPath, userId, apply);
    }

    private static InvalidDataException Usage() => new(
        "Usage: Galatea.Server operator upgrade-delegation-store "
        + "--config <absolute-path> --user <userId> [--apply]. The default is dry-run."
    );
}
