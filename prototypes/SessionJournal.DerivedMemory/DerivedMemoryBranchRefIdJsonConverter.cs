using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedMemory;

internal sealed class DerivedMemoryBranchRefIdJsonConverter
    : JsonConverter<RefId> {
    public override RefId Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) {
        if (reader.TokenType != JsonTokenType.String) {
            throw new JsonException(
                "Derived-memory branchRefId must be a canonical lowercase hexadecimal string."
            );
        }
        string text = reader.GetString()
            ?? throw new JsonException(
                "Derived-memory branchRefId cannot be null."
            );
        try {
            return RefId.ParseHex(text).Unwrap();
        }
        catch (FormatException exception) {
            throw new JsonException(
                "Derived-memory branchRefId must contain exactly 16 lowercase hexadecimal characters.",
                exception
            );
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        RefId value,
        JsonSerializerOptions options
    ) => writer.WriteStringValue(value.ToHexString());
}
