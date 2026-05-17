namespace Atelia.TextAdv;

internal static class TextAdvRuntimeEnvironment {
    internal const string RepoDirEnv = "ATELIA_TEXTADV_REPO_DIR";
    internal const string ActorJournalDirEnv = "ATELIA_TEXTADV_ACTOR_JOURNAL_DIR";
    private const string DefaultRepoDir = "/tmp/atelia-textadv-game";

    internal static string GetRepoDir() {
        return GetEnvironmentOrDefault(RepoDirEnv, DefaultRepoDir);
    }

    internal static string GetActorJournalDir() {
        return GetEnvironmentOrDefault(ActorJournalDirEnv, Path.Combine(GetRepoDir(), "actor-journals"));
    }

    internal static string GetEnvironmentOrDefault(string key, string defaultValue) {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
    }

    internal static string? GetOptionalEnvironment(string key) {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    internal static int GetPositiveIntEnvironment(string key, int defaultValue) {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value)) { return defaultValue; }

        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : defaultValue;
    }

    internal static string RequireEnvironment(string key, string consumerDescription) {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value)) {
            throw new InvalidOperationException($"Environment variable '{key}' is required for {consumerDescription}.");
        }

        return value.Trim();
    }

    internal static string BuildProviderErrorMessage(
        IReadOnlyList<string> errors,
        string prefix,
        string defaultMessage
    ) {
        var message = string.Join(
            "; ",
            errors
                .Select(static error => error?.Trim())
                .Where(static error => !string.IsNullOrWhiteSpace(error))
        );

        if (string.IsNullOrWhiteSpace(message)) {
            message = defaultMessage;
        }

        return string.IsNullOrWhiteSpace(prefix) ? message : $"{prefix}{message}";
    }
}
