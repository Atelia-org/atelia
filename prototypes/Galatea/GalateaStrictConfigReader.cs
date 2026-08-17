using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Atelia.Galatea.Server;

internal static class GalateaStrictConfigReader {
    internal const int CurrentConfigVersion = 1;
    internal const int MaximumConfigUtf8Bytes = 1024 * 1024;
    internal const int MaximumSystemPromptUtf8Bytes = 1024 * 1024;
    internal const int MaximumUserCount = 256;
    private const int MaximumDepth = 32;
    private const int OpenReadOnly = 0;
    private const int OpenNonBlocking = 0x800;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const uint LinuxFileTypeMask = 0xF000;
    private const uint LinuxRegularFileType = 0x8000;

    internal static byte[] ReadUsersAndValidate(string path) {
        byte[] bytes = ReadBoundedRegularFile(
            path,
            MaximumConfigUtf8Bytes,
            "Galatea config"
        );
        ValidateUsers(bytes);
        return bytes;
    }

    internal static byte[] ReadBoundedRegularFile(
        string path,
        int maximumBytes,
        string kind
    ) {
        if (!OperatingSystem.IsLinux()) {
            throw new PlatformNotSupportedException(
                "Galatea strict file loading V1 requires Linux no-follow file semantics."
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
                "Galatea strict file loading V1 requires Linux no-follow file semantics."
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

    internal static void ValidateUsers(ReadOnlySpan<byte> bytes) {
        try {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumDepth
            });
            RequireRead(ref reader, JsonTokenType.StartObject, "Galatea config");
            ValidateUsersObject(ref reader);
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
    }

    private static void ValidateUsersObject(ref Utf8JsonReader reader) {
        var seen = NewPropertySet();
        while (ReadProperty(ref reader, seen, "config", out string property)) {
            RequireReadValue(ref reader, property);
            switch (property) {
                case "v":
                    RequireExactConfigVersion(ref reader);
                    break;
                case "users":
                    ValidateObjectArray(
                        ref reader,
                        MaximumUserCount,
                        "users",
                        ValidateUserObject
                    );
                    break;
                case "listenUrls":
                    ValidateStringArrayOrNull(ref reader, 256, property);
                    break;
                case "callLogDir":
                    RequireStringOrNull(reader.TokenType, property);
                    break;
                case "maintenanceMode":
                    RequireToken(reader.TokenType, JsonTokenType.True,
                        JsonTokenType.False, property);
                    break;
                case "recapGrid":
                    RequireToken(reader.TokenType, JsonTokenType.StartObject,
                        property);
                    ValidateRecapGridObject(ref reader);
                    break;
                default:
                    throw Unknown("config", property);
            }
        }
        if (!seen.Contains("v")) {
            throw UnsupportedConfigVersion();
        }
    }

    private static void RequireExactConfigVersion(
        ref Utf8JsonReader reader
    ) {
        if (reader.TokenType != JsonTokenType.Number
            || reader.HasValueSequence
            || !reader.ValueSpan.SequenceEqual("1"u8)) {
            throw UnsupportedConfigVersion();
        }
    }

    private static InvalidDataException UnsupportedConfigVersion() => new(
        "Galatea config requires exact integer version 'v': 1; "
        + "migrate the config before retrying."
    );

    private static void ValidateUserObject(ref Utf8JsonReader reader) {
        var seen = NewPropertySet();
        while (ReadProperty(ref reader, seen, "user", out string property)) {
            RequireReadValue(ref reader, property);
            switch (property) {
                case "userId":
                case "password":
                case "sessionDir":
                case "systemPrompt":
                    RequireToken(reader.TokenType, JsonTokenType.String, property);
                    break;
                case "systemPromptFile":
                    RequireStringOrNull(reader.TokenType, property);
                    break;
                default:
                    throw Unknown("user", property);
            }
        }
    }

    private static void ValidateRecapGridObject(ref Utf8JsonReader reader) {
        var seen = NewPropertySet();
        while (ReadProperty(ref reader, seen, "recapGrid", out string property)) {
            RequireReadValue(ref reader, property);
            switch (property) {
                case "routeManifestPath":
                case "currentAgentControlProfileId":
                    RequireToken(reader.TokenType, JsonTokenType.String, property);
                    break;
                case "agentControlProfileFiles":
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
