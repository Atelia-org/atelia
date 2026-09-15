using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal;

/// <summary>Immutable input facts. Structured content is never implicitly converted to a prompt.</summary>
[JsonConverter(typeof(SessionInputContentJsonConverter))]
public sealed class SessionInputContent : IEquatable<SessionInputContent> {
    // The content envelope consumes one JSON container level. Direct serialization
    // therefore fits the framework's default 64-level limit; outer DTOs own their limits.
    public const int MaximumContentDepth = 64;
    public const int MaximumStructuredDepth = MaximumContentDepth - 1;
    private readonly string? _text;
    private readonly JsonElement _value;
    private readonly string? _json;
    private SessionInputContent(string text) { ValidateUnicode(text); _text = text; }
    private SessionInputContent(string schemaId, JsonElement value) {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaId);
        ValidateUnicode(schemaId);
        if (value.ValueKind != JsonValueKind.Object) {
            throw new ArgumentException("Structured input must be a JSON object.", nameof(value));
        }
        ValidateJson(value);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, SessionRequestCanonicalizer.WriterOptions)) {
            value.WriteTo(writer);
        }
        _json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        using JsonDocument document = JsonDocument.Parse(_json, new JsonDocumentOptions { MaxDepth = MaximumStructuredDepth });
        _value = document.RootElement.Clone();
        SchemaId = schemaId;
    }

    public static SessionInputContent Text(string value) {
        ArgumentNullException.ThrowIfNull(value);
        return new(value);
    }
    public static SessionInputContent Structured(string schemaId, JsonElement value) => new(schemaId, value);
    public bool IsStructured => SchemaId is not null;
    public string? SchemaId { get; }
    public string TextValue => !IsStructured ? _text! : throw new InvalidOperationException("Structured input has no text value; project it explicitly at the request boundary.");
    public JsonElement JsonValue => IsStructured ? _value : throw new InvalidOperationException("Text input has no JSON value.");
    /// <summary>Encodes the stable machine-readable content envelope, with no presentation.</summary>
    public byte[] ToUtf8Json() {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, SessionRequestCanonicalizer.WriterOptions)) { Write(writer); }
        return buffer.WrittenMemory.ToArray();
    }
    public static implicit operator SessionInputContent(string value) => Text(value);
    public bool Equals(SessionInputContent? other) => other is not null
        && string.Equals(SchemaId, other.SchemaId, StringComparison.Ordinal)
        && string.Equals(_text, other._text, StringComparison.Ordinal)
        && string.Equals(_json, other._json, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is SessionInputContent other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(SchemaId, _text, _json);
    public static bool operator ==(SessionInputContent? left, SessionInputContent? right) => Equals(left, right);
    public static bool operator !=(SessionInputContent? left, SessionInputContent? right) => !Equals(left, right);

    internal IHistoryMessage ToHistoryMessage() => IsStructured
        ? new SessionInputObservationMessage(this)
        : new ObservationMessage(TextValue);
    internal void Write(Utf8JsonWriter writer) {
        writer.WriteStartObject();
        writer.WriteString("kind", IsStructured ? "structured" : "text");
        if (IsStructured) {
            writer.WriteString("schemaId", SchemaId);
            writer.WritePropertyName("value");
            _value.WriteTo(writer);
        }
        else { writer.WriteString("value", _text); }
        writer.WriteEndObject();
    }
    internal static SessionInputContent Read(JsonElement element) {
        if (element.ValueKind != JsonValueKind.Object) { throw new InvalidDataException("Input must be an object."); }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject()) {
            if (!names.Add(property.Name)) { throw new InvalidDataException("Duplicate input property."); }
        }
        string? kind = element.GetProperty("kind").GetString();
        string[] expected = kind switch {
            "text" => ["kind", "value"],
            "structured" => ["kind", "schemaId", "value"],
            _ => throw new InvalidDataException("Unknown input content kind.")
        };
        if (!element.EnumerateObject().Select(static p => p.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(expected.Order(StringComparer.Ordinal))) { throw new InvalidDataException("Unexpected input properties."); }
        return kind == "text" ? Text(element.GetProperty("value").GetString() ?? throw new InvalidDataException("Text input must not be null."))
            : Structured(element.GetProperty("schemaId").GetString()!, element.GetProperty("value"));
    }
    private static void ValidateUnicode(string text) => _ = new UTF8Encoding(false, true).GetByteCount(text);
    private static void ValidateJson(JsonElement value, int depth = 1) {
        if (value.ValueKind is (JsonValueKind.Object or JsonValueKind.Array) && depth > MaximumStructuredDepth) {
            throw new InvalidDataException($"Structured input exceeds {MaximumStructuredDepth} container levels.");
        }

        switch (value.ValueKind) {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in value.EnumerateObject()) {
                    ValidateUnicode(property.Name);
                    if (!names.Add(property.Name)) { throw new InvalidDataException("Duplicate JSON input property."); }
                    ValidateJson(property.Value, depth + 1);
                }
                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in value.EnumerateArray()) { ValidateJson(item, depth + 1); }
                break;
            case JsonValueKind.String:
                try { ValidateUnicode(value.GetString()!); }
                catch (Exception exception) when (exception is InvalidOperationException or EncoderFallbackException) {
                    throw new InvalidDataException("Structured input contains invalid Unicode.", exception);
                }
                break;
            case JsonValueKind.Undefined: throw new InvalidDataException("Undefined JSON input.");
        }
    }
}

/// <summary>Machine-readable serialization; mutually exclusive accessors are never reflected.</summary>
public sealed class SessionInputContentJsonConverter : JsonConverter<SessionInputContent> {
    public override SessionInputContent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        try { return SessionInputContent.Read(document.RootElement); }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or InvalidOperationException or KeyNotFoundException) {
            throw new JsonException("Invalid SessionInputContent envelope.", exception);
        }
    }

    public override void Write(Utf8JsonWriter writer, SessionInputContent value, JsonSerializerOptions options) => value.Write(writer);
}

/// <summary>A semantic history unit. It must be projected before passing to a provider.</summary>
public sealed record SessionInputObservationMessage(SessionInputContent Content) : IHistoryMessage {
    public HistoryMessageKind Kind => HistoryMessageKind.Observation;
}

/// <summary>Host-owned transient projection; never invoked by raw reads or audit.</summary>
public interface ISessionInputProjector {
    string Project(SessionInputContent input);
}

internal static class SessionInputProjection {
    internal static string Project(SessionInputContent content, ISessionInputProjector? projector) =>
        content.IsStructured
            ? (projector ?? throw new NotSupportedException($"No input projector is configured for '{content.SchemaId}'.")).Project(content)
            : content.TextValue;
    internal static IHistoryMessage Project(IHistoryMessage message, ISessionInputProjector? projector) =>
        message is SessionInputObservationMessage observation
            ? new ObservationMessage(Project(observation.Content, projector))
            : message;
}
