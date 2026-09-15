using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Galatea.Prompts;

namespace Atelia.Galatea.Server;

internal sealed record GalateaConfigV9PlayerSelection(string Id, string Name, string PasswordFromUser);
internal sealed record GalateaConfigV9File(string Path, byte[] Before, byte[] After, UnixFileMode Mode);
internal sealed record GalateaConfigV9Input(string Path, string Sha256);
internal sealed record GalateaConfigV9Plan(string ConfigPath, GalateaConfig Config,
    IReadOnlyList<GalateaConfigV9File> Files, IReadOnlyList<GalateaConfigV9Input> Dependencies);

/// <summary>The V9 reader exists only inside this one-time offline conversion.</summary>
internal static class GalateaConfigV9Conversion {
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal const int MaximumFileBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static GalateaConfigV9Plan BuildPlan(string configPath, GalateaConfigV9PlayerSelection selection) {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Offline configuration upgrade requires Linux."); }
        ArgumentNullException.ThrowIfNull(selection);
        string path = Path.GetFullPath(configPath);
        byte[] original = Read(path, GalateaStrictConfigReader.MaximumConfigUtf8Bytes);
        JsonObject root = ReadV9(original);
        string directory = Path.GetDirectoryName(path)!;
        JsonArray users = root["users"]!.AsArray();
        var heartbeat = new HashSet<string>(StringComparer.Ordinal);
        if (root["serverAgentUserIds"] is JsonArray enabled) {
            foreach (JsonNode? item in enabled) {
                string id = item?.GetValue<string>() ?? throw Invalid();
                if (!heartbeat.Add(id)) { throw Invalid(); }
            }
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var characters = new JsonArray();
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var templates = new Dictionary<string, GalateaConfigV9File>(StringComparer.Ordinal);
        string? selectedPassword = null;
        foreach (JsonNode? item in users) {
            JsonObject user = item!.AsObject();
            string id = Text(user, "userId");
            if (!ids.Add(id)) { throw Invalid(); }
            string password = Text(user, "password");
            if (id == selection.PasswordFromUser) { selectedPassword = password; }
            var characterName = new GalateaCharacterName(Text(user, "characterName"));
            var playerName = new GalateaPlayerName(Text(user, "playerName"));
            var character = user.DeepClone().AsObject();
            foreach (string removed in new[] { "userId", "characterName", "password", "playerName" }) { character.Remove(removed); }
            character["id"] = id;
            character["name"] = characterName.Value;
            character["heartbeatEnabled"] = heartbeat.Contains(id);
            string? file = user["characterContextTemplateFile"]?.GetValue<string>();
            string source;
            if (!string.IsNullOrWhiteSpace(file)) {
                string templatePath = Path.GetFullPath(file, directory);
                if (templatePath == path) { throw Invalid(); }
                byte[] before = Read(templatePath, GalateaStrictConfigReader.MaximumSystemPromptUtf8Bytes);
                string raw = Utf8.GetString(before);
                source = raw.Trim();
                string converted = ConvertSource(source, characterName, playerName);
                // Preserve original leading/trailing whitespace and line endings in the operator's file.
                byte[] after = Utf8.GetBytes(raw.Replace(GalateaPromptTemplate.PlayerNameToken, playerName.Value, StringComparison.Ordinal));
                if (after.Length > GalateaStrictConfigReader.MaximumSystemPromptUtf8Bytes) { throw Invalid(); }
                var change = new GalateaConfigV9File(templatePath, before, after, File.GetUnixFileMode(templatePath));
                if (templates.TryGetValue(templatePath, out var previous)
                    && !previous.After.AsSpan().SequenceEqual(after)) {
                    throw new InvalidDataException("Shared template requires different per-character conversions; split it explicitly before upgrading.");
                }
                templates[templatePath] = change;
                sources.Add(id, converted);
            }
            else {
                source = user["characterContextTemplate"]?.GetValue<string>() ?? "";
                string converted = ConvertSource(source, characterName, playerName);
                character["characterContextTemplate"] = converted;
                sources.Add(id, converted);
            }
            characters.Add(character);
        }
        if (heartbeat.Any(id => !ids.Contains(id)) || selectedPassword is null) { throw Invalid(); }
        var runtime = new JsonObject();
        foreach (string field in new[] { "listenUrls", "callLogDir", "maintenanceMode", "recapGrid" }) {
            if (root.TryGetPropertyValue(field, out JsonNode? value)) { runtime[field] = value?.DeepClone(); }
        }
        var candidate = new JsonObject {
            ["v"] = 10, ["characters"] = characters,
            ["players"] = new JsonArray(new JsonObject {
                ["id"] = selection.Id, ["name"] = selection.Name, ["password"] = selectedPassword
            }), ["runtime"] = runtime
        };
        byte[] afterConfig = Utf8.GetBytes(candidate.ToJsonString(JsonOptions) + "\n");
        if (afterConfig.Length > GalateaStrictConfigReader.MaximumConfigUtf8Bytes) { throw Invalid(); }
        var dependencies = new List<GalateaConfigV9Input>();
        foreach (string dependency in new[] {
            Path.Combine(directory, GalateaConfigLoader.ConnectionsFileName),
            Path.Combine(directory, GalateaConfigLoader.DelegatesFileName)
        }.Concat(runtime["recapGrid"]!["agentControlProfileFiles"]!.AsArray()
            .Select(node => Path.GetFullPath(node!.GetValue<string>(), directory)))) {
            if (templates.ContainsKey(dependency) || dependency == path) { throw Invalid(); }
            dependencies.Add(new(dependency, Digest(Read(dependency))));
        }
        GalateaConfig config = GalateaConfigLoader.ValidateConfigUpgradeCandidate(path, afterConfig, sources);
        foreach (var dependency in dependencies) {
            if (Digest(Read(dependency.Path)) != dependency.Sha256) { throw Invalid(); }
        }
        var files = templates.Values.OrderBy(file => file.Path, StringComparer.Ordinal).ToList();
        // Config is the final publication point: the V10-only server refuses the old root before this.
        files.Add(new(path, original, afterConfig, UnixFileMode.UserRead | UnixFileMode.UserWrite));
        return new(path, config, files.AsReadOnly(), dependencies.AsReadOnly());
    }

    internal static string ConvertSource(string source, GalateaCharacterName character, GalateaPlayerName player) {
        string oldRendering = GalateaPromptTemplate.Render(source, character, player,
            GalateaStrictConfigReader.MaximumSystemPromptUtf8Bytes);
        string converted = source.Replace(GalateaPromptTemplate.PlayerNameToken, player.Value, StringComparison.Ordinal);
        GalateaSystemInstructionContent.ValidateInstructionSource(converted, requireCharacterName: true);
        string newRendering = GalateaPromptTemplate.Render(converted, character,
            GalateaStrictConfigReader.MaximumSystemPromptUtf8Bytes);
        if (!string.Equals(oldRendering, newRendering, StringComparison.Ordinal)) {
            throw new InvalidDataException("Template conversion cannot preserve non-recursive binding semantics.");
        }
        return converted;
    }

    internal static byte[] Read(string path, int maximumBytes = MaximumFileBytes)
        => GalateaStrictConfigReader.ReadBoundedRegularFile(path, maximumBytes, "configuration upgrade input");
    internal static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static InvalidDataException Invalid() => new("Invalid or unsupported V9 configuration upgrade input.");

    private static string Text(JsonObject value, string field) {
        string text = value[field]?.GetValue<string>() ?? throw Invalid();
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return text;
    }

    private static JsonObject ReadV9(byte[] bytes) {
        _ = Utf8.GetString(bytes);
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        CheckDuplicates(document.RootElement);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("v", out var version) || version.GetRawText() != "9") { throw Invalid(); }
        JsonObject root = JsonNode.Parse(bytes)!.AsObject();
        RequireFields(root, ["v", "users", "listenUrls", "callLogDir", "maintenanceMode", "recapGrid", "serverAgentUserIds"], ["v", "users"]);
        if (root["users"] is not JsonArray users || users.Count is < 1 or > GalateaStrictConfigReader.MaximumCharacterCount) { throw Invalid(); }
        foreach (JsonNode? item in users) {
            if (item is not JsonObject user) { throw Invalid(); }
            string[] required = ["userId", "password", "characterName", "playerName", "sessionDir", "delegationStateDir",
                "characterMemoryStateDir", "homeDir", "sessionProvisioning", "defaultConnectionId"];
            RequireFields(user, [.. required, "characterContextTemplate", "characterContextTemplateFile"], required);
            foreach (var field in user) {
                if (field.Key == "characterContextTemplateFile" && field.Value is null) { continue; }
                _ = field.Value?.GetValue<string>() ?? throw Invalid();
            }
        }
        if (root.ContainsKey("serverAgentUserIds") && root["serverAgentUserIds"] is not JsonArray) { throw Invalid(); }
        return root;
    }

    internal static void CheckDuplicates(JsonElement value) {
        if (value.ValueKind == JsonValueKind.Object) {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) {
                if (!names.Add(property.Name)) { throw Invalid(); }
                CheckDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) {
            foreach (var item in value.EnumerateArray()) { CheckDuplicates(item); }
        }
        else if (value.ValueKind == JsonValueKind.String) { _ = value.GetString(); }
    }

    private static void RequireFields(JsonObject value, string[] allowed, string[] required) {
        if (value.Any(field => !allowed.Contains(field.Key, StringComparer.Ordinal))
            || required.Any(field => !value.ContainsKey(field))) { throw Invalid(); }
    }
}
