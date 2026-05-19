using System.Text.Json;
using Atelia.StateJournal;

namespace Atelia.TextAdv;

internal sealed class TextAdvSessionState {
    internal const string DefaultBranchName = "main";

    public string CurrentBranchName { get; set; } = DefaultBranchName;
    public string HelpMode { get; set; } = TerminalHelpMode.Off.ToString();

    internal string GetNormalizedBranchName() {
        var branchName = string.IsNullOrWhiteSpace(CurrentBranchName) ? DefaultBranchName : CurrentBranchName.Trim();
        var branchError = Repository.ValidateBranchName(branchName);
        if (branchError is not null) {
            throw new InvalidDataException($"Session metadata contains an invalid branch name '{branchName}': {branchError}");
        }

        return branchName;
    }

    internal TerminalHelpMode GetNormalizedHelpMode() {
        return Enum.TryParse<TerminalHelpMode>(HelpMode, ignoreCase: true, out var parsed)
            ? parsed
            : TerminalHelpMode.Off;
    }

    internal void Normalize() {
        CurrentBranchName = GetNormalizedBranchName();
        HelpMode = GetNormalizedHelpMode().ToString();
    }
}

internal static class TextAdvSessionStore {
    private const string SessionFileName = ".textadv-session.json";
    private const string TempFileSuffix = ".tmp";

    internal static TextAdvSessionState Load(string repoDir) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);

        var sessionPath = GetSessionFilePath(repoDir);
        if (!File.Exists(sessionPath)) { return new TextAdvSessionState(); }

        var json = File.ReadAllText(sessionPath);
        var state = JsonSerializer.Deserialize<TextAdvSessionState>(json)
            ?? throw new InvalidDataException($"Session metadata '{sessionPath}' is empty or invalid.");
        state.Normalize();
        return state;
    }

    internal static void Save(string repoDir, TextAdvSessionState state) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);
        ArgumentNullException.ThrowIfNull(state);

        state.Normalize();
        Directory.CreateDirectory(repoDir);
        var sessionPath = GetSessionFilePath(repoDir);
        var tempPath = sessionPath + TempFileSuffix;
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        try {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, sessionPath, overwrite: true);
        }
        finally {
            if (File.Exists(tempPath)) {
                try { File.Delete(tempPath); }
                catch {
                    // best-effort cleanup
                }
            }
        }
    }

    internal static string GetSessionFilePath(string repoDir) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);
        return Path.Combine(repoDir, SessionFileName);
    }
}
