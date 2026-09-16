using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Atelia.Galatea.Server;

internal static class GalateaStrictConfigReader {
    internal const int CurrentConfigVersion = 12;
    internal const int MaximumConfigUtf8Bytes = 1024 * 1024;
    internal const int MaximumSystemPromptUtf8Bytes = 1024 * 1024;
    internal const int MaximumCharacterCount = 256;
    private const int MaximumDepth = 32;
    private const int OpenReadOnly = 0;
    private const int OpenNonBlocking = 0x800;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const uint LinuxFileTypeMask = 0xF000;
    private const uint LinuxRegularFileType = 0x8000;

    internal static byte[] ReadAndValidate(string path) {
        byte[] bytes = ReadBoundedRegularFile(
            path,
            MaximumConfigUtf8Bytes,
            "Galatea config"
        );
        ValidateRoot(bytes);
        return bytes;
    }

    internal static byte[] ReadBoundedRegularFile(
        string path,
        int maximumBytes,
        string kind
    ) {
        if (!OperatingSystem.IsLinux()) {
            throw new PlatformNotSupportedException(
                "Galatea strict file loading requires Linux no-follow file semantics."
            );
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string resolved = Path.GetFullPath(path);
        RequireExistingAncestorsNoReparse(resolved, kind);
        var info = new FileInfo(resolved);
        if (!info.Exists) {
            throw new FileNotFoundException(
                $"{kind} file was not found: {resolved}",
                resolved
            );
        }
        if (info.Length is < 1 || info.Length > maximumBytes) {
            throw new InvalidDataException(
                $"{kind} bytes are empty or exceed the code-owned cap."
            );
        }
        int descriptor = Open(
            resolved,
            OpenReadOnly | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec
        );
        if (descriptor < 0) {
            throw new InvalidDataException(
                $"{kind} must be a no-follow regular file."
            );
        }
        try {
            if (ReadDescriptorFileType(descriptor) != LinuxRegularFileType) {
                throw new InvalidDataException(
                    $"{kind} must be a regular file."
                );
            }
            var handle = new SafeFileHandle(
                new IntPtr(descriptor),
                ownsHandle: true
            );
            descriptor = -1;
            using var stream = new FileStream(
                handle,
                FileAccess.Read,
                bufferSize: 64 * 1024,
                isAsync: false
            );
            if (stream.Length is < 1 || stream.Length > maximumBytes) {
                throw new InvalidDataException(
                    $"{kind} bytes changed or exceed the code-owned cap."
                );
            }
            int length = checked((int)stream.Length);
            byte[] bytes = GC.AllocateUninitializedArray<byte>(length);
            stream.ReadExactly(bytes);
            if (stream.Position != stream.Length || stream.Length != length) {
                throw new InvalidDataException(
                    $"{kind} changed during its bounded read."
                );
            }
            return bytes;
        }
        finally {
            if (descriptor >= 0) {
                _ = Close(descriptor);
            }
        }
    }

    internal static void RequireExistingAncestorsNoReparse(
        string path,
        string kind
    ) {
        if (!OperatingSystem.IsLinux()) {
            throw new PlatformNotSupportedException(
                "Galatea strict file loading requires Linux no-follow file semantics."
            );
        }
        string? current = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path)
        );
        while (!string.IsNullOrEmpty(current)) {
            try {
                if ((File.GetAttributes(current)
                        & FileAttributes.ReparsePoint) != 0) {
                    throw new InvalidDataException(
                        $"{kind} path contains a symlink or reparse point: {current}"
                    );
                }
            }
            catch (Exception exception) when (
                exception is FileNotFoundException
                    or DirectoryNotFoundException) {
                // Missing suffixes are allowed for bootstrap/deferred files;
                // every existing ancestor is still inspected.
            }
            string? parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.Ordinal)) {
                break;
            }
            current = parent;
        }
    }

    internal static void RequireExistingRegularFileNoFollow(
        string path,
        string kind
    ) {
        if (!OperatingSystem.IsLinux()) {
            throw new PlatformNotSupportedException(
                "Galatea strict file loading requires Linux no-follow file semantics."
            );
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string resolved = Path.GetFullPath(path);
        RequireExistingAncestorsNoReparse(resolved, kind);
        int descriptor = Open(
            resolved,
            OpenReadOnly | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec
        );
        if (descriptor < 0) {
            throw new InvalidDataException(
                $"{kind} must be an existing no-follow regular file."
            );
        }
        try {
            if (ReadDescriptorFileType(descriptor) != LinuxRegularFileType) {
                throw new InvalidDataException(
                    $"{kind} must be a regular file."
                );
            }
        }
        finally {
            _ = Close(descriptor);
        }
    }

    /// <summary>
    /// Verifies only the final file's no-follow regular-file metadata and
    /// bounded non-empty length. It deliberately does not read file content;
    /// callers that later consume bytes must use <see cref="ReadBoundedRegularFile"/>
    /// again to close the time-of-check/time-of-use boundary.
    /// </summary>
    internal static void RequireBoundedRegularFileNoFollow(
        string path,
        int maximumBytes,
        string kind
    ) {
        if (maximumBytes < 1) {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }
        RequireExistingRegularFileNoFollow(path, kind);
        string resolved = Path.GetFullPath(path);
        int descriptor = Open(
            resolved,
            OpenReadOnly | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec
        );
        if (descriptor < 0) {
            throw new InvalidDataException(
                $"{kind} must be an existing no-follow regular file."
            );
        }
        try {
            if (ReadDescriptorFileType(descriptor) != LinuxRegularFileType) {
                throw new InvalidDataException($"{kind} must be a regular file.");
            }
            var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
            descriptor = -1;
            using var stream = new FileStream(handle, FileAccess.Read,
                bufferSize: 1, isAsync: false);
            if (stream.Length < 1 || stream.Length > maximumBytes) {
                throw new InvalidDataException(
                    $"{kind} bytes are empty or exceed the code-owned cap."
                );
            }
        }
        finally {
            if (descriptor >= 0) {
                _ = Close(descriptor);
            }
        }
    }

    internal static void ValidateRoot(ReadOnlySpan<byte> bytes) {
        try {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumDepth
            });
            RequireRead(ref reader, JsonTokenType.StartObject, "Galatea config");
            ValidateRootObject(ref reader);
            if (reader.Read()) {
                throw new InvalidDataException(
                    "Galatea config JSON contains trailing data."
                );
            }
        }
        catch (JsonException exception) {
            throw new InvalidDataException(
                "Galatea config JSON is not strict valid UTF-8 JSON.",
                exception
            );
        }
        catch (InvalidOperationException exception) when (
            exception.InnerException is DecoderFallbackException) {
            throw new InvalidDataException(
                "Galatea config JSON is not strict valid UTF-8 JSON.",
                new JsonException(
                    "Galatea config JSON contains invalid UTF-8 text.",
                    exception
                )
            );
        }
    }

    private static void ValidateRootObject(ref Utf8JsonReader reader) {
        var seen = NewPropertySet();
        while (ReadProperty(ref reader, seen, "config", out string property)) {
            RequireReadValue(ref reader, property);
            switch (property) {
                case "v":
                    RequireExactConfigVersion(ref reader);
                    break;
                case "characters":
                    ValidateObjectArray(
                        ref reader,
                        MaximumCharacterCount,
                        "characters",
                        ValidateCharacterObject
                    );
                    break;
                case "players":
                    ValidateObjectArray(ref reader, MaximumCharacterCount,
                        "players", ValidatePlayerObject);
                    break;
                case "runtime":
                    RequireToken(reader.TokenType, JsonTokenType.StartObject,
                        property);
                    ValidateRuntimeObject(ref reader);
                    break;
                default:
                    throw Unknown("config", property);
            }
        }
        if (!seen.Contains("v")) {
            throw UnsupportedConfigVersion();
        }
        foreach (string field in new[] { "characters", "players", "runtime" }) {
            if (!seen.Contains(field)) { throw new InvalidDataException("Galatea config requires '" + field + "'."); }
        }
    }

    private static void RequireExactConfigVersion(
        ref Utf8JsonReader reader
    ) {
        if (reader.TokenType != JsonTokenType.Number
            || reader.HasValueSequence
            || !reader.ValueSpan.SequenceEqual("12"u8)) {
            throw UnsupportedConfigVersion();
        }
    }

    private static InvalidDataException UnsupportedConfigVersion() => new(
        "Galatea config requires exact integer version 'v': 12; "
        + "migrate the config before retrying."
    );

    private static void ValidateCharacterObject(ref Utf8JsonReader reader) {
        var seen = NewPropertySet();
        while (ReadProperty(ref reader, seen, "character", out string property)) {
            RequireReadValue(ref reader, property);
            switch (property) {
                case "id":
                case "name":
                case "homeDir":
                case "sessionDir":
                case "delegationStateDir":
                case "characterMemoryStateDir":
                case "defaultConnectionId":
                case "characterContextTemplate":
                    RequireToken(reader.TokenType, JsonTokenType.String, property);
                    break;
                case "sessionProvisioning":
                    RequireExactSessionProvisioning(ref reader);
                    break;
                case "characterContextTemplateFile":
                    RequireStringOrNull(reader.TokenType, property);
                    break;
                case "autonomyIntervalMinutes":
                    RequireAutonomyIntervalMinutes(ref reader);
                    break;
                default:
                    throw Unknown("character", property);
            }
        }
        if (!seen.Contains("homeDir")) {
            throw new InvalidDataException("character requires string field 'homeDir'.");
        }
        if (!seen.Contains("sessionProvisioning")) {
            throw new InvalidDataException(
                "character requires string field 'sessionProvisioning'."
            );
        }
        foreach (string field in new[] { "id", "name", "sessionDir", "delegationStateDir", "characterMemoryStateDir" }) {
            if (!seen.Contains(field)) { throw new InvalidDataException("character requires string field '" + field + "'."); }
        }
        if (!seen.Contains("defaultConnectionId")) {
            throw new InvalidDataException(
                "character requires string field 'defaultConnectionId'."
            );
        }
        if (!seen.Contains("autonomyIntervalMinutes")) {
            throw new InvalidDataException(
                "character requires integer field 'autonomyIntervalMinutes'."
            );
        }
    }

    private static void RequireAutonomyIntervalMinutes(
        ref Utf8JsonReader reader
    ) {
        if (reader.TokenType != JsonTokenType.Number
            || !reader.TryGetInt32(out int minutes)
            || minutes is < 0 or > GalateaConfigValidation.MaximumAutonomyIntervalMinutes) {
            throw new InvalidDataException(
                "autonomyIntervalMinutes must be an integer from 0 to "
                + GalateaConfigValidation.MaximumAutonomyIntervalMinutes
                + "."
            );
        }
    }

    private static void ValidatePlayerObject(ref Utf8JsonReader reader) {
        var seen = NewPropertySet();
        while (ReadProperty(ref reader, seen, "player", out string property)) {
            RequireReadValue(ref reader, property);
            if (property is not ("id" or "name" or "password")) { throw Unknown("player", property); }
            RequireToken(reader.TokenType, JsonTokenType.String, property);
        }
        foreach (string field in new[] { "id", "name", "password" }) {
            if (!seen.Contains(field)) { throw new InvalidDataException("player requires string field '" + field + "'."); }
        }
    }

    private static void ValidateRuntimeObject(ref Utf8JsonReader reader) {
        var seen = NewPropertySet();
        while (ReadProperty(ref reader, seen, "runtime", out string property)) {
            RequireReadValue(ref reader, property);
            switch (property) {
                case "listenUrls":
                    ValidateStringArrayOrNull(ref reader, 256, property);
                    break;
                case "callLogDir":
                    RequireStringOrNull(reader.TokenType, property);
                    break;
                case "maintenanceMode":
                    RequireToken(reader.TokenType, JsonTokenType.True, JsonTokenType.False, property);
                    break;
                case "completionAttemptTimeoutSeconds":
                    RequireToken(reader.TokenType, JsonTokenType.StartObject, property);
                    var timeoutKeys = NewPropertySet();
                    while (ReadProperty(ref reader, timeoutKeys, property, out string connection)) {
                        RequireReadValue(ref reader, connection);
                        if (string.IsNullOrWhiteSpace(connection)
                            || reader.TokenType != JsonTokenType.Number
                            || !reader.TryGetInt32(out int seconds)
                            || seconds is < 1 or > 86400) {
                            throw new InvalidDataException("Completion attempt timeout must be 1..86400 seconds per exact connection id.");
                        }
                    }
                    break;
                case "recapGrid":
                    RequireToken(reader.TokenType, JsonTokenType.StartObject, property);
                    ValidateRecapGridObject(ref reader);
                    break;
                default:
                    throw Unknown("runtime", property);
            }
        }
    }

    private static void RequireExactSessionProvisioning(
        ref Utf8JsonReader reader
    ) {
        RequireToken(
            reader.TokenType,
            JsonTokenType.String,
            "sessionProvisioning"
        );
        string? value = reader.GetString();
        if (!string.Equals(
                value,
                "existing-only",
                StringComparison.Ordinal
            )
            && !string.Equals(
                value,
                "create-if-missing",
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "sessionProvisioning must be exactly 'existing-only' or "
                + "'create-if-missing'."
            );
        }
    }

    private static void ValidateRecapGridObject(ref Utf8JsonReader reader) {
        var seen = NewPropertySet();
        while (ReadProperty(ref reader, seen, "recapGrid", out string property)) {
            RequireReadValue(ref reader, property);
            switch (property) {
                case "maintenance":
                    RequireToken(reader.TokenType, JsonTokenType.StartObject,
                        property);
                    ValidateRecapGridMaintenanceObject(ref reader);
                    break;
                case "historicalAgentControlProfileFiles":
                    ValidateStringArrayOrNull(
                        ref reader,
                        256,
                        property,
                        allowNull: false
                    );
                    break;
                default:
                    throw Unknown("recapGrid", property);
            }
        }
        foreach (string field in new[] {
                     "maintenance", "historicalAgentControlProfileFiles"
                 }) {
            if (!seen.Contains(field)) {
                throw new InvalidDataException(
                    "recapGrid requires '" + field + "'."
                );
            }
        }
    }

    private static void ValidateRecapGridMaintenanceObject(
        ref Utf8JsonReader reader
    ) {
        var seen = NewPropertySet();
        while (ReadProperty(ref reader, seen, "recapGrid.maintenance",
                   out string property)) {
            RequireReadValue(ref reader, property);
            switch (property) {
                case "connectionId":
                    RequireToken(reader.TokenType, JsonTokenType.String,
                        property);
                    break;
                case "maximumConcurrency":
                    if (reader.TokenType != JsonTokenType.Number
                        || !reader.TryGetInt32(out int concurrency)
                        || concurrency is < 1 or > 1_024) {
                        throw new InvalidDataException(
                            "recapGrid.maintenance.maximumConcurrency must "
                            + "be an integer from 1 to 1024."
                        );
                    }
                    break;
                case "dispatchTimeoutMilliseconds":
                    if (reader.TokenType != JsonTokenType.Number
                        || !reader.TryGetInt64(out long milliseconds)
                        || milliseconds is < 1 or > 86_400_000) {
                        throw new InvalidDataException(
                            "recapGrid.maintenance.dispatchTimeoutMilliseconds "
                            + "must be an integer from 1 to 86400000."
                        );
                    }
                    break;
                default:
                    throw Unknown("recapGrid.maintenance", property);
            }
        }
        foreach (string field in new[] {
                     "connectionId", "maximumConcurrency",
                     "dispatchTimeoutMilliseconds"
                 }) {
            if (!seen.Contains(field)) {
                throw new InvalidDataException(
                    "recapGrid.maintenance requires '" + field + "'."
                );
            }
        }
    }

    private static void ValidateObjectArray(
        ref Utf8JsonReader reader,
        int maximumCount,
        string field,
        ReaderAction validateObject
    ) {
        RequireToken(reader.TokenType, JsonTokenType.StartArray, field);
        int count = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) {
            if (++count > maximumCount) {
                throw new InvalidDataException($"{field} exceeds its count cap.");
            }
            RequireToken(reader.TokenType, JsonTokenType.StartObject, field);
            validateObject(ref reader);
        }
        if (reader.TokenType != JsonTokenType.EndArray) {
            throw new InvalidDataException($"{field} array is incomplete.");
        }
    }

    private static void ValidateStringArrayOrNull(
        ref Utf8JsonReader reader,
        int maximumCount,
        string field,
        bool allowNull = true
    ) {
        if (reader.TokenType == JsonTokenType.Null && allowNull) { return; }
        RequireToken(reader.TokenType, JsonTokenType.StartArray, field);
        int count = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) {
            if (++count > maximumCount) {
                throw new InvalidDataException($"{field} exceeds its count cap.");
            }
            RequireToken(reader.TokenType, JsonTokenType.String, field);
        }
        if (reader.TokenType != JsonTokenType.EndArray) {
            throw new InvalidDataException($"{field} array is incomplete.");
        }
    }

    private static bool ReadProperty(
        ref Utf8JsonReader reader,
        HashSet<string> seen,
        string scope,
        out string property
    ) {
        if (!reader.Read()) {
            throw new InvalidDataException($"{scope} object is incomplete.");
        }
        if (reader.TokenType == JsonTokenType.EndObject) {
            property = string.Empty;
            return false;
        }
        RequireToken(reader.TokenType, JsonTokenType.PropertyName, scope);
        property = reader.GetString()
            ?? throw new InvalidDataException($"{scope} property is null.");
        if (!seen.Add(property)) {
            throw new InvalidDataException(
                $"{scope} contains a duplicate property '{property}'."
            );
        }
        return true;
    }

    private static void RequireReadValue(
        ref Utf8JsonReader reader,
        string field
    ) {
        if (!reader.Read()) {
            throw new InvalidDataException($"{field} has no value.");
        }
    }

    private static void RequireRead(
        ref Utf8JsonReader reader,
        JsonTokenType token,
        string field
    ) {
        if (!reader.Read()) {
            throw new InvalidDataException($"{field} JSON is empty.");
        }
        RequireToken(reader.TokenType, token, field);
    }

    private static void RequireStringOrNull(JsonTokenType token, string field)
        => RequireToken(token, JsonTokenType.String, JsonTokenType.Null, field);

    private static void RequireToken(
        JsonTokenType actual,
        JsonTokenType expected,
        string field
    ) {
        if (actual != expected) {
            throw new InvalidDataException(
                $"{field} has invalid JSON token {actual}."
            );
        }
    }

    private static void RequireToken(
        JsonTokenType actual,
        JsonTokenType first,
        JsonTokenType second,
        string field
    ) {
        if (actual != first && actual != second) {
            throw new InvalidDataException(
                $"{field} has invalid JSON token {actual}."
            );
        }
    }

    private static HashSet<string> NewPropertySet()
        => new(StringComparer.OrdinalIgnoreCase);

    private static InvalidDataException Unknown(string scope, string property)
        => new($"{scope} contains unknown property '{property}'.");

    private static uint ReadDescriptorFileType(int descriptor) {
        IntPtr buffer = Marshal.AllocHGlobal(256);
        try {
            if (Fstat(descriptor, buffer) != 0) {
                throw new IOException("Failed to inspect opened config file.");
            }
            int modeOffset = RuntimeInformation.ProcessArchitecture switch {
                Architecture.X64 => 24,
                Architecture.Arm64 => 16,
                _ => throw new PlatformNotSupportedException(
                    "Unsupported Linux stat ABI."
                )
            };
            uint mode = unchecked((uint)Marshal.ReadInt32(buffer, modeOffset));
            return mode & LinuxFileTypeMask;
        }
        finally {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private delegate void ReaderAction(ref Utf8JsonReader reader);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int Fstat(int descriptor, IntPtr value);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);
}
