using System.Text.Json;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;

namespace Atelia.SessionJournal.Cli;

internal static class AdmissionCodec {
    private const int MaximumFamilyEntries = 256;
    internal static RecapGridControlAdmission Decode(ReadOnlySpan<byte> bytes) {
        try {
            using JsonDocument document = JsonDocument.Parse(
                bytes.ToArray(),
                new JsonDocumentOptions {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8
                }
            );
            JsonElement root = document.RootElement;
            RequireProperties(
                root,
                "v",
                "permissions",
                "familyAllowlist",
                "capabilityFingerprintAllowlist",
                "targetCarrierAllowlist",
                "logicalColumnPrefixes",
                "maximumBootstrapRows",
                "maximumProjectedCalls"
            );
            if (root.GetProperty("v").GetInt32() != 1) {
                throw new InvalidDataException(
                    "Control admission schema version is unsupported."
                );
            }
            string[] permissionTokens = ReadStrings(
                root.GetProperty("permissions"),
                6
            );
            if (permissionTokens.Length == 0
                || permissionTokens.Distinct(StringComparer.Ordinal).Count()
                    != permissionTokens.Length) {
                throw new InvalidDataException(
                    "Control permissions must be explicit and unique."
                );
            }
            RecapGridControlPermission permissions =
                RecapGridControlPermission.None;
            foreach (string token in permissionTokens) {
                permissions |= token switch {
                    "create" => RecapGridControlPermission.Create,
                    "register-family" => RecapGridControlPermission
                        .RegisterFamily,
                    "register-definition" => RecapGridControlPermission
                        .RegisterDefinition,
                    "register-recipe" => RecapGridControlPermission
                        .RegisterRecipe,
                    "activate" => RecapGridControlPermission.Activate,
                    "promote" => RecapGridControlPermission.Promote,
                    _ => throw new InvalidDataException(
                        "Control admission contains an unknown permission."
                    )
                };
            }
            FamilyDefinitionDigest[] families = ReadStrings(
                root.GetProperty("familyAllowlist"),
                MaximumFamilyEntries
            ).Select(static value => new FamilyDefinitionDigest(value))
                .ToArray();
            string[] capabilities = ReadStrings(
                root.GetProperty("capabilityFingerprintAllowlist"),
                MaximumFamilyEntries
            );
            ContextHeaderCarrier[] carriers = ReadStrings(
                root.GetProperty("targetCarrierAllowlist"),
                16
            ).Select(value => ContextHeaderCarrierTokens
                .TryParseStorageToken(value, out ContextHeaderCarrier carrier)
                    ? carrier
                    : throw new InvalidDataException(
                        "Control admission contains an unknown carrier."
                    )).ToArray();
            string[] prefixes = ReadStrings(
                root.GetProperty("logicalColumnPrefixes"),
                RecapGridLimits.MaximumColumnCount
            );
            return new RecapGridControlAdmission(
                permissions,
                families,
                capabilities,
                carriers,
                prefixes,
                root.GetProperty("maximumBootstrapRows").GetInt32(),
                root.GetProperty("maximumProjectedCalls").GetInt32()
            );
        }
        catch (InvalidDataException) {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException
                or ArgumentException
                or InvalidOperationException
                or OverflowException) {
            throw new InvalidDataException(
                "Control admission is not a strict V1 value.",
                exception
            );
        }
    }

    private static string[] ReadStrings(JsonElement array, int maximum) {
        if (array.ValueKind != JsonValueKind.Array
            || array.GetArrayLength() > maximum) {
            throw new InvalidDataException(
                "Control admission array exceeds its code-owned bound."
            );
        }
        return [.. array.EnumerateArray().Select(element =>
            element.ValueKind == JsonValueKind.String
                ? element.GetString()!
                : throw new InvalidDataException(
                    "Control admission arrays contain only strings."
                ))];
    }

    private static void RequireProperties(
        JsonElement value,
        params string[] exact
    ) {
        if (value.ValueKind != JsonValueKind.Object
            || !value.EnumerateObject().Select(static item => item.Name)
                .SequenceEqual(exact, StringComparer.Ordinal)) {
            throw new InvalidDataException(
                "Control admission properties are missing, duplicated, unknown, or out of order."
            );
        }
    }
}
