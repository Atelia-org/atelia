using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Atelia.SessionJournal.MemoPod;

internal static class MemoPodDocumentCodec {
    internal const string Schema = "atelia.memo-pod.document.v2";

    private static readonly JsonWriterOptions WriterOptions = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
        SkipValidation = false
    };

    internal static byte[] Encode(MemoPodDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            writer.WriteStartObject();
            writer.WriteString("schema", Schema);
            writer.WriteString("podId", document.PodId.Value);
            writer.WriteString("topic", document.Topic);
            writer.WriteNumber("nextMemoId", document.NextMemoOrdinal);
            writer.WriteStartArray("memos");
            foreach (Memo memo in document.Memos) {
                writer.WriteStartObject();
                writer.WriteString("id", memo.Id.Value);
                WriteNullableString(writer, "title", memo.Title);
                WriteNullableString(writer, "gist", memo.Gist);
                WriteNullableString(writer, "summary", memo.Summary);
                writer.WriteString("exactText", memo.ExactText);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        if (buffer.WrittenCount > MemoPodLimits.MaximumDocumentUtf8Bytes) {
            throw new MemoPodDocumentLimitException(
                $"MemoPod document exceeds {MemoPodLimits.MaximumDocumentUtf8Bytes} UTF-8 bytes."
            );
        }
        return buffer.WrittenSpan.ToArray();
    }

    internal static MemoPodDocument Decode(ReadOnlySpan<byte> utf8) {
        if (utf8.IsEmpty) {
            throw new MemoPodDocumentFormatException(
                "MemoPod document must not be empty."
            );
        }
        if (utf8.Length > MemoPodLimits.MaximumDocumentUtf8Bytes) {
            throw new MemoPodDocumentLimitException(
                $"MemoPod document exceeds {MemoPodLimits.MaximumDocumentUtf8Bytes} UTF-8 bytes."
            );
        }
        if (utf8.Length >= 3
            && utf8[0] == 0xEF
            && utf8[1] == 0xBB
            && utf8[2] == 0xBF) {
            throw new MemoPodDocumentFormatException(
                "MemoPod document must not contain a UTF-8 BOM."
            );
        }

        try {
            MemoPodDocument document = DecodeCore(utf8);
            byte[] canonical = Encode(document);
            if (!utf8.SequenceEqual(canonical)) {
                throw new MemoPodDocumentFormatException(
                    "MemoPod document is valid JSON but is not the exact canonical V2 encoding."
                );
            }
            return document;
        }
        catch (MemoPodDocumentFormatException) {
            throw;
        }
        catch (MemoPodDocumentLimitException) {
            throw;
        }
        catch (Exception exception) when (exception is JsonException
            or FormatException
            or ArgumentException
            or OverflowException) {
            throw new MemoPodDocumentFormatException(
                "MemoPod document is malformed or violates the V2 domain contract.",
                exception
            );
        }
    }

    private static MemoPodDocument DecodeCore(ReadOnlySpan<byte> utf8) {
        var reader = new Utf8JsonReader(
            utf8,
            new JsonReaderOptions {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            }
        );

        RequireToken(ref reader, JsonTokenType.StartObject, "root object");
        string schema = ReadRequiredStringProperty(
            ref reader,
            "schema"
        );
        if (!string.Equals(schema, Schema, StringComparison.Ordinal)) {
            throw new MemoPodDocumentFormatException(
                $"MemoPod document schema must be '{Schema}'."
            );
        }

        MemoPodId podId = MemoPodId.Parse(
            ReadRequiredStringProperty(ref reader, "podId")
        );
        string topic = ReadRequiredStringProperty(ref reader, "topic");
        ulong nextMemoOrdinal = ReadRequiredUInt64Property(
            ref reader,
            "nextMemoId"
        );

        RequireProperty(ref reader, "memos");
        RequireToken(ref reader, JsonTokenType.StartArray, "memos array");
        var memos = new List<Memo>();
        while (ReadNext(ref reader, "memo or end of memos")
            is not JsonTokenType.EndArray) {
            if (reader.TokenType is not JsonTokenType.StartObject) {
                throw new MemoPodDocumentFormatException(
                    "Every memos entry must be an object."
                );
            }
            if (memos.Count == MemoPodLimits.MaximumActiveMemoCount) {
                throw new MemoPodDocumentFormatException(
                    $"MemoPod document exceeds {MemoPodLimits.MaximumActiveMemoCount} active memos."
                );
            }

            MemoId memoId = MemoId.Parse(
                ReadRequiredStringProperty(ref reader, "id")
            );
            string? title = ReadRequiredNullableStringProperty(
                ref reader,
                "title"
            );
            string? gist = ReadRequiredNullableStringProperty(
                ref reader,
                "gist"
            );
            string? summary = ReadRequiredNullableStringProperty(
                ref reader,
                "summary"
            );
            string exactText = ReadRequiredStringProperty(
                ref reader,
                "exactText"
            );
            RequireToken(ref reader, JsonTokenType.EndObject, "memo end");
            memos.Add(new Memo(memoId, exactText, title, gist, summary));
        }

        RequireToken(ref reader, JsonTokenType.EndObject, "root end");
        if (reader.Read()) {
            throw new MemoPodDocumentFormatException(
                "MemoPod document contains trailing JSON content."
            );
        }

        return new MemoPodDocument(
            podId,
            topic,
            nextMemoOrdinal,
            memos
        );
    }

    private static string ReadRequiredStringProperty(
        ref Utf8JsonReader reader,
        string propertyName
    ) {
        RequireProperty(ref reader, propertyName);
        RequireToken(ref reader, JsonTokenType.String, propertyName);
        return reader.GetString()
            ?? throw new MemoPodDocumentFormatException(
                $"MemoPod document property '{propertyName}' must not be null."
            );
    }

    private static string? ReadRequiredNullableStringProperty(
        ref Utf8JsonReader reader,
        string propertyName
    ) {
        RequireProperty(ref reader, propertyName);
        JsonTokenType tokenType = ReadNext(ref reader, propertyName);
        return tokenType switch {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString()
                ?? throw new MemoPodDocumentFormatException(
                    $"MemoPod document property '{propertyName}' must be a string or null."
                ),
            _ => throw new MemoPodDocumentFormatException(
                $"MemoPod document property '{propertyName}' must be a string or null."
            )
        };
    }

    private static ulong ReadRequiredUInt64Property(
        ref Utf8JsonReader reader,
        string propertyName
    ) {
        RequireProperty(ref reader, propertyName);
        RequireToken(ref reader, JsonTokenType.Number, propertyName);
        if (!reader.TryGetUInt64(out ulong value)) {
            throw new MemoPodDocumentFormatException(
                $"MemoPod document property '{propertyName}' must be an unsigned JSON integer."
            );
        }
        return value;
    }

    private static void RequireProperty(
        ref Utf8JsonReader reader,
        string propertyName
    ) {
        RequireToken(ref reader, JsonTokenType.PropertyName, propertyName);
        if (!reader.ValueTextEquals(propertyName)) {
            throw new MemoPodDocumentFormatException(
                $"MemoPod document expected property '{propertyName}' in canonical order."
            );
        }
    }

    private static void RequireToken(
        ref Utf8JsonReader reader,
        JsonTokenType tokenType,
        string context
    ) {
        JsonTokenType actual = ReadNext(ref reader, context);
        if (actual != tokenType) {
            throw new MemoPodDocumentFormatException(
                $"MemoPod document expected {tokenType} for {context}, but found {actual}."
            );
        }
    }

    private static JsonTokenType ReadNext(
        ref Utf8JsonReader reader,
        string context
    ) {
        if (!reader.Read()) {
            throw new MemoPodDocumentFormatException(
                $"MemoPod document ended before {context}."
            );
        }
        return reader.TokenType;
    }

    private static void WriteNullableString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value
    ) {
        if (value is null) {
            writer.WriteNull(propertyName);
        }
        else {
            writer.WriteString(propertyName, value);
        }
    }
}

internal sealed class MemoPodDocumentFormatException : IOException {
    internal MemoPodDocumentFormatException(string message)
        : base(message) { }

    internal MemoPodDocumentFormatException(
        string message,
        Exception innerException
    ) : base(message, innerException) { }
}

internal sealed class MemoPodDocumentLimitException : IOException {
    internal MemoPodDocumentLimitException(string message)
        : base(message) { }
}
