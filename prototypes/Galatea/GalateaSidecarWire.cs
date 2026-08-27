using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Atelia.Galatea.Server;

/// <summary>
/// Protocol-neutral strict JSON helpers shared by the exact V1 and V2
/// Galatea sidecar clients. Version and business-frame languages remain
/// owned by each protocol client.
/// </summary>
internal static partial class GalateaSidecarWire {
    internal const int MaximumIdentifierUtf8Bytes = 200;

    internal static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    internal static Dictionary<string, JsonElement> ReadStrictProperties(
        JsonElement root
    ) {
        if (root.ValueKind != JsonValueKind.Object) {
            throw new InvalidDataException("Sidecar frame must be an object.");
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject()) {
            if (!seen.Add(property.Name)) {
                throw new InvalidDataException(
                    "Sidecar frame contains duplicate properties."
                );
            }
            result.Add(property.Name, property.Value);
        }
        return result;
    }

    internal static void RequireProtocolVersion(
        Dictionary<string, JsonElement> properties,
        int expectedVersion
    ) {
        if (!properties.TryGetValue("v", out JsonElement version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out int value)
            || value != expectedVersion) {
            throw new InvalidDataException(
                "Sidecar frame has an unsupported protocol version."
            );
        }
    }

    internal static string RequireString(
        Dictionary<string, JsonElement> properties,
        string name
    ) {
        if (!properties.TryGetValue(name, out JsonElement element)
            || element.ValueKind != JsonValueKind.String) {
            throw new InvalidDataException(
                $"Sidecar frame requires string '{name}'."
            );
        }
        return element.GetString()
            ?? throw new InvalidDataException(
                $"Sidecar frame string '{name}' is null."
            );
    }

    internal static string RequireIdentifier(
        Dictionary<string, JsonElement> properties,
        string name
    ) {
        string value = RequireString(properties, name);
        try {
            RequireIdentifier(value, name);
            return value;
        }
        catch (ArgumentException exception) {
            throw new InvalidDataException(
                $"Sidecar frame identifier '{name}' is invalid.",
                exception
            );
        }
    }

    internal static string? OptionalIdentifier(
        Dictionary<string, JsonElement> properties,
        string name
    ) => properties.ContainsKey(name)
        ? RequireIdentifier(properties, name)
        : null;

    internal static void RequireIdentifier(
        string value,
        string parameter
    ) {
        if (string.IsNullOrEmpty(value)
            || StrictUtf8.GetByteCount(value) > MaximumIdentifierUtf8Bytes
            || !IdentifierRegex().IsMatch(value)) {
            throw new ArgumentException(
                "Delegate identifiers must match [A-Za-z0-9][A-Za-z0-9._:-]* "
                    + $"and fit {MaximumIdentifierUtf8Bytes} UTF-8 bytes.",
                parameter
            );
        }
    }

    internal static void RequireExactKeys(
        Dictionary<string, JsonElement> properties,
        IReadOnlyList<string> expected
    ) {
        if (properties.Count != expected.Count
            || expected.Any(key => !properties.ContainsKey(key))) {
            throw new InvalidDataException(
                "Sidecar frame has missing or unknown properties."
            );
        }
    }

    internal static void RequireAllowedKeys(
        Dictionary<string, JsonElement> properties,
        IReadOnlyList<string> allowed
    ) {
        var set = new HashSet<string>(allowed, StringComparer.Ordinal);
        if (properties.Keys.Any(key => !set.Contains(key))) {
            throw new InvalidDataException(
                "Sidecar frame has an unknown property."
            );
        }
    }

    internal static void RequirePresent(
        Dictionary<string, JsonElement> properties,
        IReadOnlyList<string> required
    ) {
        if (required.Any(key => !properties.ContainsKey(key))) {
            throw new InvalidDataException(
                "Sidecar frame is missing a required property."
            );
        }
    }

    [GeneratedRegex(
        "^[A-Za-z0-9][A-Za-z0-9._:-]*$",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex IdentifierRegex();
}
