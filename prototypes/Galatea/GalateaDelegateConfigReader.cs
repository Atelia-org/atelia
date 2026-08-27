using System.Runtime.InteropServices;
using System.Text.Json;

namespace Atelia.Galatea.Server;

internal static class GalateaDelegateConfigReader {
    internal const int CurrentVersion = 1;
    internal const int MaximumInputUtf8Bytes = 256 * 1024;
    internal const string CanonicalRecipient = "Codex";
    internal const string CodexAppServerKind = "codex-app-server";

    private const int MaximumDepth = 8;
    private const int MaximumAllowedRoots = 64;
    private const int MinimumRpcTimeoutMs = 100;
    private const int MaximumRpcTimeoutMs = 300_000;
    private const int MinimumTurnTimeoutMs = 100;
    private const int MaximumTurnTimeoutMs = 86_400_000;
    private const int MinimumShutdownGraceMs = 10;
    private const int MaximumShutdownGraceMs = 30_000;
    private const int MinimumFrameUtf8Bytes = 1_024;
    private const int MaximumFrameUtf8Bytes = 1_048_576;
    private const int MaximumQueueCount = 4_096;
    private const int MaximumBodyUtf8Bytes = 1_048_576;
    private const int MaximumInboxCount = 4_096;
    private const int MaximumInboxUtf8Bytes = 64 * 1024 * 1024;
    private const int JsonEnvelopeReserveUtf8Bytes = 1_024;
    private const int MaximumJsonEscapeExpansion = 6;

    internal static GalateaDelegateConfig Read(string path) {
        byte[] utf8 = GalateaStrictConfigReader.ReadBoundedRegularFile(
            path,
            MaximumInputUtf8Bytes,
            "Galatea delegates"
        );
        ValidateClosedShape(utf8);

        using JsonDocument document = JsonDocument.Parse(utf8, new() {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaximumDepth
        });
        JsonElement root = document.RootElement;
        JsonElement sidecarElement = root.GetProperty("sidecar");
        JsonElement rootsElement = root.GetProperty("allowedRoots");
        JsonElement routesElement = root.GetProperty("routes");

        string nodeCommand = RequireCanonicalRegularFile(
            sidecarElement.GetProperty("nodeCommand").GetString(),
            "sidecar.nodeCommand",
            executable: true
        );
        string entryPoint = RequireCanonicalRegularFile(
            sidecarElement.GetProperty("entryPoint").GetString(),
            "sidecar.entryPoint",
            executable: false
        );
        string codexCommand = RequireCanonicalRegularFile(
            sidecarElement.GetProperty("codexCommand").GetString(),
            "sidecar.codexCommand",
            executable: true
        );
        int rpcTimeoutMs = ReadBoundedInteger(
            sidecarElement,
            "rpcTimeoutMs",
            MinimumRpcTimeoutMs,
            MaximumRpcTimeoutMs
        );
        int turnTimeoutMs = ReadBoundedInteger(
            sidecarElement,
            "turnTimeoutMs",
            MinimumTurnTimeoutMs,
            MaximumTurnTimeoutMs
        );
        int shutdownGraceMs = ReadBoundedInteger(
            sidecarElement,
            "shutdownGraceMs",
            MinimumShutdownGraceMs,
            MaximumShutdownGraceMs
        );
        int maximumFrameUtf8Bytes = ReadBoundedInteger(
            sidecarElement,
            "maximumFrameUtf8Bytes",
            MinimumFrameUtf8Bytes,
            MaximumFrameUtf8Bytes
        );

        var allowedRoots = new List<string>(rootsElement.GetArrayLength());
        var uniqueRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement value in rootsElement.EnumerateArray()) {
            string rootPath = RequireCanonicalDirectory(
                value.GetString(),
                "allowedRoots"
            );
            if (!uniqueRoots.Add(rootPath)) {
                throw new InvalidDataException(
                    $"delegates allowedRoots contains duplicate '{rootPath}'."
                );
            }
            allowedRoots.Add(rootPath);
        }

        JsonElement routeElement = routesElement[0];
        string recipient = RequireExactString(
            routeElement,
            "recipient",
            CanonicalRecipient
        );
        string kind = RequireExactString(
            routeElement,
            "kind",
            CodexAppServerKind
        );
        string cwd = RequireCanonicalDirectory(
            routeElement.GetProperty("cwd").GetString(),
            "routes[0].cwd"
        );
        if (!allowedRoots.Any(rootPath => IsContained(cwd, rootPath))) {
            throw new InvalidDataException(
                "routes[0].cwd must be contained in an allowedRoots entry."
            );
        }
        string modeText = routeElement.GetProperty("mode").GetString()
            ?? throw new InvalidDataException("routes[0].mode is null.");
        GalateaDelegateMode mode = modeText switch {
            "research" => GalateaDelegateMode.Research,
            "work" => GalateaDelegateMode.Work,
            _ => throw new InvalidDataException(
                "routes[0].mode must be exactly 'research' or 'work'."
            )
        };
        bool network = routeElement.GetProperty("network").GetBoolean();
        int maximumQueuedMails = ReadBoundedInteger(
            routeElement,
            "maximumQueuedMails",
            1,
            MaximumQueueCount
        );
        int maximumTaskUtf8Bytes = ReadBoundedInteger(
            routeElement,
            "maximumTaskUtf8Bytes",
            1,
            MaximumBodyUtf8Bytes
        );
        int maximumReplyUtf8Bytes = ReadBoundedInteger(
            routeElement,
            "maximumReplyUtf8Bytes",
            1,
            MaximumBodyUtf8Bytes
        );
        int maximumInboxReplies = ReadBoundedInteger(
            routeElement,
            "maximumInboxReplies",
            1,
            MaximumInboxCount
        );
        int maximumInboxBytes = ReadBoundedInteger(
            routeElement,
            "maximumInboxUtf8Bytes",
            1,
            MaximumInboxUtf8Bytes
        );
        RequireFrameCompatibility(
            maximumTaskUtf8Bytes,
            maximumFrameUtf8Bytes,
            "maximumTaskUtf8Bytes"
        );
        RequireFrameCompatibility(
            maximumReplyUtf8Bytes,
            maximumFrameUtf8Bytes,
            "maximumReplyUtf8Bytes"
        );
        if (maximumInboxBytes < maximumReplyUtf8Bytes) {
            throw new InvalidDataException(
                "maximumInboxUtf8Bytes must be at least maximumReplyUtf8Bytes."
            );
        }

        var result = new GalateaDelegateConfig(
            new GalateaDelegateSidecarConfig(
                nodeCommand,
                entryPoint,
                codexCommand,
                rpcTimeoutMs,
                turnTimeoutMs,
                shutdownGraceMs,
                maximumFrameUtf8Bytes
            ),
            allowedRoots.AsReadOnly(),
            Array.AsReadOnly([
                new GalateaDelegateRouteConfig(
                    recipient,
                    kind,
                    cwd,
                    mode,
                    network,
                    maximumQueuedMails,
                    maximumTaskUtf8Bytes,
                    maximumReplyUtf8Bytes,
                    maximumInboxReplies,
                    maximumInboxBytes
                )
            ])
        );
        return Validate(result);
    }

    internal static GalateaDelegateConfig Validate(
        GalateaDelegateConfig? config
    ) {
        if (config is null) {
            throw new InvalidOperationException(
                "Galatea requires strict delegate configuration."
            );
        }
        if (config.AllowedRoots is not { Count: > 0 and <= MaximumAllowedRoots }
            || config.Routes is not { Count: 1 }
            || config.Sidecar is null) {
            throw new InvalidOperationException(
                "Galatea delegate configuration is not a closed V1 configuration."
            );
        }
        GalateaDelegateSidecarConfig sidecar = config.Sidecar;
        string nodeCommand = RequireCanonicalRegularFile(
            sidecar.NodeCommand,
            "sidecar.nodeCommand",
            executable: true
        );
        string entryPoint = RequireCanonicalRegularFile(
            sidecar.EntryPoint,
            "sidecar.entryPoint",
            executable: false
        );
        string codexCommand = RequireCanonicalRegularFile(
            sidecar.CodexCommand,
            "sidecar.codexCommand",
            executable: true
        );
        RequireBoundedInteger(sidecar.RpcTimeoutMs, "rpcTimeoutMs",
            MinimumRpcTimeoutMs, MaximumRpcTimeoutMs);
        RequireBoundedInteger(sidecar.TurnTimeoutMs, "turnTimeoutMs",
            MinimumTurnTimeoutMs, MaximumTurnTimeoutMs);
        RequireBoundedInteger(sidecar.ShutdownGraceMs, "shutdownGraceMs",
            MinimumShutdownGraceMs, MaximumShutdownGraceMs);
        RequireBoundedInteger(
            sidecar.MaximumFrameUtf8Bytes,
            "maximumFrameUtf8Bytes",
            MinimumFrameUtf8Bytes,
            MaximumFrameUtf8Bytes
        );

        var roots = new List<string>(config.AllowedRoots.Count);
        var uniqueRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? configuredRoot in config.AllowedRoots) {
            string root = RequireCanonicalDirectory(
                configuredRoot,
                "allowedRoots"
            );
            if (!uniqueRoots.Add(root)) {
                throw new InvalidDataException(
                    $"delegates allowedRoots contains duplicate '{root}'."
                );
            }
            roots.Add(root);
        }

        GalateaDelegateRouteConfig route = config.Routes[0]
            ?? throw new InvalidOperationException(
                "Galatea delegate route must not be null."
            );
        if (!string.Equals(route.Recipient, CanonicalRecipient,
                StringComparison.Ordinal)
            || !string.Equals(route.Kind, CodexAppServerKind,
                StringComparison.Ordinal)) {
            throw new InvalidOperationException(
                "Galatea delegates require the exact Codex route."
            );
        }
        string cwd = RequireCanonicalDirectory(route.Cwd, "routes[0].cwd");
        if (!roots.Any(root => IsContained(cwd, root))) {
            throw new InvalidDataException(
                "routes[0].cwd must be contained in an allowedRoots entry."
            );
        }
        if (route.Mode is not (
                GalateaDelegateMode.Research or GalateaDelegateMode.Work)) {
            throw new InvalidDataException(
                "routes[0].mode must be research or work."
            );
        }
        RequireBoundedInteger(route.MaximumQueuedMails,
            "maximumQueuedMails", 1, MaximumQueueCount);
        RequireBoundedInteger(route.MaximumTaskUtf8Bytes,
            "maximumTaskUtf8Bytes", 1, MaximumBodyUtf8Bytes);
        RequireBoundedInteger(route.MaximumReplyUtf8Bytes,
            "maximumReplyUtf8Bytes", 1, MaximumBodyUtf8Bytes);
        RequireBoundedInteger(route.MaximumInboxReplies,
            "maximumInboxReplies", 1, MaximumInboxCount);
        RequireBoundedInteger(route.MaximumInboxUtf8Bytes,
            "maximumInboxUtf8Bytes", 1, MaximumInboxUtf8Bytes);
        RequireFrameCompatibility(
            route.MaximumTaskUtf8Bytes,
            sidecar.MaximumFrameUtf8Bytes,
            "maximumTaskUtf8Bytes"
        );
        RequireFrameCompatibility(
            route.MaximumReplyUtf8Bytes,
            sidecar.MaximumFrameUtf8Bytes,
            "maximumReplyUtf8Bytes"
        );
        if (route.MaximumInboxUtf8Bytes < route.MaximumReplyUtf8Bytes) {
            throw new InvalidDataException(
                "maximumInboxUtf8Bytes must be at least maximumReplyUtf8Bytes."
            );
        }

        return new GalateaDelegateConfig(
            new GalateaDelegateSidecarConfig(
                nodeCommand,
                entryPoint,
                codexCommand,
                sidecar.RpcTimeoutMs,
                sidecar.TurnTimeoutMs,
                sidecar.ShutdownGraceMs,
                sidecar.MaximumFrameUtf8Bytes
            ),
            roots.AsReadOnly(),
            Array.AsReadOnly([
                new GalateaDelegateRouteConfig(
                    CanonicalRecipient,
                    CodexAppServerKind,
                    cwd,
                    route.Mode,
                    route.Network,
                    route.MaximumQueuedMails,
                    route.MaximumTaskUtf8Bytes,
                    route.MaximumReplyUtf8Bytes,
                    route.MaximumInboxReplies,
                    route.MaximumInboxUtf8Bytes
                )
            ])
        );
    }

    internal static byte[] CreatePlaceholderTemplateUtf8() =>
        """
        {
          "v": 1,
          "sidecar": {
            "nodeCommand": "/REPLACE_WITH_CANONICAL_NODE_EXECUTABLE",
            "entryPoint": "/REPLACE_WITH_GALATEA_SIDECAR_ENTRY_POINT",
            "codexCommand": "/REPLACE_WITH_CANONICAL_CODEX_EXECUTABLE",
            "rpcTimeoutMs": 30000,
            "turnTimeoutMs": 1200000,
            "shutdownGraceMs": 5000,
            "maximumFrameUtf8Bytes": 1048576
          },
          "allowedRoots": [
            "/REPLACE_WITH_CANONICAL_ALLOWED_ROOT"
          ],
          "routes": [
            {
              "recipient": "Codex",
              "kind": "codex-app-server",
              "cwd": "/REPLACE_WITH_CANONICAL_CODEX_WORKING_DIRECTORY",
              "mode": "work",
              "network": false,
              "maximumQueuedMails": 128,
              "maximumTaskUtf8Bytes": 100000,
              "maximumReplyUtf8Bytes": 100000,
              "maximumInboxReplies": 128,
              "maximumInboxUtf8Bytes": 4194304
            }
          ]
        }
        """u8.ToArray();

    private static void ValidateClosedShape(ReadOnlySpan<byte> utf8) {
        try {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumDepth
            });
            RequireRead(ref reader, JsonTokenType.StartObject, "delegates");
            var root = ReadProperties(ref reader, "delegates", new() {
                ["v"] = static (ref Utf8JsonReader value) => {
                    if (value.TokenType != JsonTokenType.Number
                        || !value.TryGetInt32(out int version)
                        || version != CurrentVersion) {
                        throw new InvalidDataException(
                            "delegates requires exact integer version 'v': 1."
                        );
                    }
                },
                ["sidecar"] = static (ref Utf8JsonReader value) =>
                    ValidateSidecar(ref value),
                ["allowedRoots"] = static (ref Utf8JsonReader value) =>
                    ValidateStringArray(
                        ref value,
                        "allowedRoots",
                        MaximumAllowedRoots
                    ),
                ["routes"] = static (ref Utf8JsonReader value) =>
                    ValidateRoutes(ref value)
            });
            RequireExactProperties(root, "delegates", [
                "v", "sidecar", "allowedRoots", "routes"
            ]);
            if (reader.Read()) {
                throw new InvalidDataException(
                    "delegates JSON contains trailing data."
                );
            }
        }
        catch (JsonException exception) {
            throw new InvalidDataException(
                "delegates JSON is not strict valid UTF-8 JSON.",
                exception
            );
        }
    }

    private static void ValidateSidecar(ref Utf8JsonReader reader) {
        RequireToken(reader.TokenType, JsonTokenType.StartObject, "sidecar");
        var seen = ReadProperties(ref reader, "sidecar", new() {
            ["nodeCommand"] = RequireString,
            ["entryPoint"] = RequireString,
            ["codexCommand"] = RequireString,
            ["rpcTimeoutMs"] = RequireNumber,
            ["turnTimeoutMs"] = RequireNumber,
            ["shutdownGraceMs"] = RequireNumber,
            ["maximumFrameUtf8Bytes"] = RequireNumber
        });
        RequireExactProperties(seen, "sidecar", [
            "nodeCommand", "entryPoint", "codexCommand", "rpcTimeoutMs",
            "turnTimeoutMs", "shutdownGraceMs", "maximumFrameUtf8Bytes"
        ]);
    }

    private static void ValidateRoutes(ref Utf8JsonReader reader) {
        RequireToken(reader.TokenType, JsonTokenType.StartArray, "routes");
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) {
            throw new InvalidDataException(
                "delegates routes must contain exactly one object."
            );
        }
        var seen = ReadProperties(ref reader, "route", new() {
            ["recipient"] = RequireString,
            ["kind"] = RequireString,
            ["cwd"] = RequireString,
            ["mode"] = RequireString,
            ["network"] = RequireBoolean,
            ["maximumQueuedMails"] = RequireNumber,
            ["maximumTaskUtf8Bytes"] = RequireNumber,
            ["maximumReplyUtf8Bytes"] = RequireNumber,
            ["maximumInboxReplies"] = RequireNumber,
            ["maximumInboxUtf8Bytes"] = RequireNumber
        });
        RequireExactProperties(seen, "route", [
            "recipient", "kind", "cwd", "mode", "network",
            "maximumQueuedMails", "maximumTaskUtf8Bytes",
            "maximumReplyUtf8Bytes", "maximumInboxReplies",
            "maximumInboxUtf8Bytes"
        ]);
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) {
            throw new InvalidDataException(
                "delegates routes must contain exactly one object."
            );
        }
    }

    private static void ValidateStringArray(
        ref Utf8JsonReader reader,
        string name,
        int maximumCount
    ) {
        RequireToken(reader.TokenType, JsonTokenType.StartArray, name);
        int count = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) {
            if (++count > maximumCount) {
                throw new InvalidDataException($"{name} exceeds its count cap.");
            }
            RequireToken(reader.TokenType, JsonTokenType.String, name);
        }
        if (count == 0 || reader.TokenType != JsonTokenType.EndArray) {
            throw new InvalidDataException($"{name} must be a non-empty array.");
        }
    }

    private static HashSet<string> ReadProperties(
        ref Utf8JsonReader reader,
        string scope,
        Dictionary<string, ReaderAction> actions
    ) {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject) {
            RequireToken(reader.TokenType, JsonTokenType.PropertyName, scope);
            string property = reader.GetString()
                ?? throw new InvalidDataException($"{scope} property is null.");
            if (!seen.Add(property)) {
                throw new InvalidDataException(
                    $"{scope} contains a duplicate property '{property}'."
                );
            }
            if (!actions.TryGetValue(property, out ReaderAction? validate)) {
                throw new InvalidDataException(
                    $"{scope} contains unknown property '{property}'."
                );
            }
            if (!reader.Read()) {
                throw new InvalidDataException($"{property} has no value.");
            }
            validate(ref reader);
        }
        if (reader.TokenType != JsonTokenType.EndObject) {
            throw new InvalidDataException($"{scope} object is incomplete.");
        }
        return seen;
    }

    private static void RequireExactProperties(
        HashSet<string> seen,
        string scope,
        IReadOnlyList<string> required
    ) {
        foreach (string property in required) {
            if (!seen.Contains(property)) {
                throw new InvalidDataException(
                    $"{scope} requires field '{property}'."
                );
            }
        }
    }

    private static void RequireString(ref Utf8JsonReader reader) =>
        RequireToken(reader.TokenType, JsonTokenType.String, "string");

    private static void RequireNumber(ref Utf8JsonReader reader) =>
        RequireToken(reader.TokenType, JsonTokenType.Number, "number");

    private static void RequireBoolean(ref Utf8JsonReader reader) {
        if (reader.TokenType is not (
                JsonTokenType.True or JsonTokenType.False)) {
            throw new InvalidDataException("boolean has an invalid JSON token.");
        }
    }

    private static void RequireRead(
        ref Utf8JsonReader reader,
        JsonTokenType token,
        string scope
    ) {
        if (!reader.Read()) {
            throw new InvalidDataException($"{scope} JSON is empty.");
        }
        RequireToken(reader.TokenType, token, scope);
    }

    private static void RequireToken(
        JsonTokenType actual,
        JsonTokenType expected,
        string scope
    ) {
        if (actual != expected) {
            throw new InvalidDataException(
                $"{scope} has invalid JSON token {actual}."
            );
        }
    }

    private static int ReadBoundedInteger(
        JsonElement owner,
        string property,
        int minimum,
        int maximum
    ) {
        JsonElement element = owner.GetProperty(property);
        if (!element.TryGetInt32(out int value)
            || value < minimum
            || value > maximum) {
            throw new InvalidDataException(
                $"{property} must be an integer from {minimum} to {maximum}."
            );
        }
        return value;
    }

    private static void RequireBoundedInteger(
        int value,
        string property,
        int minimum,
        int maximum
    ) {
        if (value < minimum || value > maximum) {
            throw new InvalidDataException(
                $"{property} must be an integer from {minimum} to {maximum}."
            );
        }
    }

    private static string RequireExactString(
        JsonElement owner,
        string property,
        string expected
    ) {
        string? value = owner.GetProperty(property).GetString();
        if (!string.Equals(value, expected, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                $"{property} must be exactly '{expected}'."
            );
        }
        return value!;
    }

    private static void RequireFrameCompatibility(
        int bodyUtf8Bytes,
        int frameUtf8Bytes,
        string property
    ) {
        long worstCase = checked(
            (long)bodyUtf8Bytes * MaximumJsonEscapeExpansion
            + JsonEnvelopeReserveUtf8Bytes
        );
        if (worstCase > frameUtf8Bytes) {
            throw new InvalidDataException(
                $"{property} is incompatible with maximumFrameUtf8Bytes "
                + "under worst-case JSON escaping."
            );
        }
    }

    private static string RequireCanonicalRegularFile(
        string? configured,
        string field,
        bool executable
    ) {
        string path = RequireCanonicalPath(configured, field);
        GalateaStrictConfigReader.RequireExistingRegularFileNoFollow(
            path,
            field
        );
        if (executable) {
            if (!OperatingSystem.IsLinux()) {
                throw new PlatformNotSupportedException(
                    "Galatea delegate executable validation requires Linux."
                );
            }
            UnixFileMode mode = File.GetUnixFileMode(path);
            const UnixFileMode execute = UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherExecute;
            if ((mode & execute) == 0) {
                throw new InvalidDataException($"{field} must be executable.");
            }
        }
        return path;
    }

    private static string RequireCanonicalDirectory(
        string? configured,
        string field
    ) {
        string path = RequireCanonicalPath(configured, field);
        if (!Directory.Exists(path)) {
            throw new InvalidDataException(
                $"{field} must identify an existing directory."
            );
        }
        return Path.TrimEndingDirectorySeparator(path);
    }

    private static string RequireCanonicalPath(
        string? configured,
        string field
    ) {
        if (!OperatingSystem.IsLinux()) {
            throw new PlatformNotSupportedException(
                "Galatea delegate configuration requires Linux realpath semantics."
            );
        }
        if (string.IsNullOrWhiteSpace(configured)
            || !Path.IsPathFullyQualified(configured)) {
            throw new InvalidDataException(
                $"{field} must be a non-blank absolute path."
            );
        }
        string provided = Path.TrimEndingDirectorySeparator(configured);
        string lexical = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(configured)
        );
        if (!string.Equals(provided, lexical, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                $"{field} must store the normalized absolute path '{lexical}'."
            );
        }
        string canonical = RealPath(lexical, field);
        if (!string.Equals(lexical, canonical, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                $"{field} must store the canonical resolved path '{canonical}'; "
                + "configured symlinks are not followed implicitly."
            );
        }
        return canonical;
    }

    private static string RealPath(string path, string field) {
        IntPtr result = realpath(path, IntPtr.Zero);
        if (result == IntPtr.Zero) {
            int error = Marshal.GetLastPInvokeError();
            throw new InvalidDataException(
                $"{field} cannot be resolved to an existing canonical path "
                + $"(errno {error})."
            );
        }
        try {
            return Marshal.PtrToStringUTF8(result)
                ?? throw new InvalidDataException(
                    $"{field} canonical path is unavailable."
                );
        }
        finally {
            free(result);
        }
    }

    private static bool IsContained(string path, string root) =>
        string.Equals(path, root, StringComparison.Ordinal)
        || path.StartsWith(
            root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar,
            StringComparison.Ordinal
        );

    private delegate void ReaderAction(ref Utf8JsonReader reader);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr realpath(string path, IntPtr resolvedPath);

    [DllImport("libc")]
    private static extern void free(IntPtr pointer);
}
