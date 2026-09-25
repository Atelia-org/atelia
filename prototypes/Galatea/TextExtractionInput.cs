using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Atelia.Galatea.Server;

internal enum TextExtractionExecutionPolicy {
    SingleCompletion,
    UntilNoToolCalls,
}

/// <summary>Owns the source and its deterministic presentation together.</summary>
internal sealed class TextExtractionInput {
    internal const int MaximumLineCount = 65_536;
    internal const int MaximumRenderedUtf8Bytes = 8 * 1024 * 1024;

    private TextExtractionInput(string originalText, bool numbered) {
        ArgumentNullException.ThrowIfNull(originalText);
        int byteCount;
        try { byteCount = TextExtractorUtf8.GetByteCount(originalText); }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException("Source must be strict UTF-8 text.", nameof(originalText), exception);
        }
        if (byteCount
                > TextExtractorBounds.MaximumTargetTextUtf8Bytes) {
            throw new ArgumentOutOfRangeException(nameof(originalText));
        }
        OriginalText = originalText;
        if (numbered) {
            Lines = new TextExtractionSourceLines(originalText);
            RenderedText = Lines.Render();
        }
        else {
            RenderedText = originalText;
        }
    }

    internal string OriginalText { get; }
    internal string RenderedText { get; }
    internal TextExtractionSourceLines? Lines { get; }
    internal static TextExtractionInput Plain(string originalText) => new(originalText, false);
    internal static TextExtractionInput Numbered(string originalText) {
        try { return new(originalText, true); }
        catch (ArgumentException exception) {
            throw new TextExtractionException(TextExtractionFailureKind.InputLimitExceeded,
                "Numbered extraction input is invalid or exceeds its bounds.",
                innerException: exception, diagnosticReasonCode: "numbered-input-invalid-or-over-limit");
        }
    }
}

internal sealed class TextExtractionSourceLines {
    internal const string ProtocolVersion = "source-lines.v1";
    private static readonly JsonSerializerOptions JsonOptions = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private readonly string _source;
    private readonly List<(int Start, int End)> _lines = [];

    internal TextExtractionSourceLines(string source) {
        ArgumentNullException.ThrowIfNull(source);
        if (TextExtractorUtf8.GetByteCount(source)
                > TextExtractorBounds.MaximumTargetTextUtf8Bytes) {
            throw new ArgumentOutOfRangeException(nameof(source));
        }
        _source = source;
        int start = 0;
        for (int i = 0; i < source.Length; i++) {
            if (source[i] is not ('\r' or '\n')) { continue; }
            AddLine(start, i);
            if (source[i] == '\r' && i + 1 < source.Length && source[i + 1] == '\n') {
                i++;
            }
            start = i + 1;
        }
        AddLine(start, source.Length);
    }

    internal int LineCount => _lines.Count;

    private void AddLine(int start, int end) {
        if (_lines.Count >= TextExtractionInput.MaximumLineCount) {
            throw new ArgumentOutOfRangeException("source", "Source line count exceeds the limit.");
        }
        _lines.Add((start, end));
    }

    internal string Slice(int startLine, int endLine) {
        if (startLine < 1 || endLine < startLine || endLine > LineCount) {
            throw new TextExtractionException(
                TextExtractionFailureKind.InvalidSourceRange,
                "The source line range is invalid.",
                diagnosticReasonCode: "invalid-source-range"
            );
        }
        return _source[_lines[startLine - 1].Start.._lines[endLine - 1].End];
    }

    internal string Render() {
        var builder = new StringBuilder();
        int bytes = 0;
        for (int i = 0; i < _lines.Count; i++) {
            (int start, int end) = _lines[i];
            string row = "L" + (i + 1).ToString("D6", CultureInfo.InvariantCulture)
                + " | " + JsonSerializer.Serialize(_source[start..end], JsonOptions) + "\n";
            bytes = checked(bytes + TextExtractorUtf8.GetByteCount(row));
            if (bytes > TextExtractionInput.MaximumRenderedUtf8Bytes) {
                throw new ArgumentOutOfRangeException("source", "Numbered input exceeds the byte limit.");
            }
            builder.Append(row);
        }
        return builder.ToString();
    }
}

internal enum TextExtractionAdmissionKind { Accepted, AlreadyAccepted, Rejected }

internal sealed record TextExtractionAdmission<T>(
    TextExtractionAdmissionKind Kind,
    T? Value,
    int? SourceStartLine,
    string? ReasonCode
) where T : class {
    internal static TextExtractionAdmission<T> Rejected(string reasonCode) =>
        new(TextExtractionAdmissionKind.Rejected, null, null, reasonCode);
}

/// <summary>One ExtractAsync invocation; never retained by an extractor or tool.</summary>
internal sealed class TextExtractionSession {
    private readonly Dictionary<string, string> _occurrences = new(StringComparer.Ordinal);
    private int _materializedUtf8Bytes;

    internal TextExtractionSession(TextExtractionInput input, TextExtractionTrace trace) {
        Input = input;
        Trace = trace;
    }

    internal TextExtractionInput Input { get; }
    internal TextExtractionTrace Trace { get; }
    internal TextExtractionException? Failure { get; set; }
    internal TextExtractionAdmissionKind? LastAdmission { get; set; }
    internal string? RejectionReason { get; set; }

    internal TextExtractionAdmission<T> Admit<T>(
        T value,
        string occurrenceKey,
        string fingerprint,
        int sourceStartLine,
        int materializedUtf8Bytes,
        int maximumTotalUtf8Bytes,
        int maximumCount = int.MaxValue
    ) where T : class {
        ArgumentNullException.ThrowIfNull(value);
        if (_occurrences.TryGetValue(occurrenceKey, out string? previous)) {
            return string.Equals(previous, fingerprint, StringComparison.Ordinal)
                ? new(TextExtractionAdmissionKind.AlreadyAccepted, null, sourceStartLine, null)
                : TextExtractionAdmission<T>.Rejected("conflicting-candidate");
        }
        if (materializedUtf8Bytes < 0 || _occurrences.Count >= maximumCount
            || (long)_materializedUtf8Bytes + materializedUtf8Bytes > maximumTotalUtf8Bytes) {
            return TextExtractionAdmission<T>.Rejected("materialized-budget-exceeded");
        }
        _occurrences.Add(occurrenceKey, fingerprint);
        _materializedUtf8Bytes += materializedUtf8Bytes;
        return new(TextExtractionAdmissionKind.Accepted, value, sourceStartLine, null);
    }
}
