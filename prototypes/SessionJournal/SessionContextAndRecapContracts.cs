using System.Text;
using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal;

public sealed record SessionContextHeader(
    string? SystemPromptFragment,
    string? ObservationMessage,
    ActionMessage? ActionMessage
) : IHistoryMessage {
    public HistoryMessageKind Kind => HistoryMessageKind.ContextHeader;
}

public sealed record ContextHeaderSnapshot(
    string SystemPromptFragment,
    string ObservationMessage,
    string ActionMessage
) {
    public static ContextHeaderSnapshot Empty { get; } = new(string.Empty, string.Empty, string.Empty);

    public bool IsEmpty =>
        string.IsNullOrEmpty(SystemPromptFragment)
        && string.IsNullOrEmpty(ObservationMessage)
        && string.IsNullOrEmpty(ActionMessage);

    public static ContextHeaderSnapshot FromSessionContextHeader(SessionContextHeader? header) {
        if (header is null) { return Empty; }

        return new ContextHeaderSnapshot(
            header.SystemPromptFragment ?? string.Empty,
            header.ObservationMessage ?? string.Empty,
            header.ActionMessage?.GetFlattenedText() ?? string.Empty
        );
    }

    public SessionContextHeader ToSessionContextHeader()
        => new(
            string.IsNullOrEmpty(SystemPromptFragment) ? null : SystemPromptFragment,
            string.IsNullOrEmpty(ObservationMessage) ? null : ObservationMessage,
            string.IsNullOrEmpty(ActionMessage)
                ? null
                : new ActionMessage([new ActionBlock.Text(ActionMessage)])
        );
}

public enum ContextHeaderCarrier {
    System,
    Observation,
    Action
}

public static class ContextHeaderCarrierTokens {
    public const string System = "system";
    public const string Observation = "observation";
    public const string Action = "action";

    public static string ToStorageToken(ContextHeaderCarrier carrier)
        => carrier switch {
            ContextHeaderCarrier.System => System,
            ContextHeaderCarrier.Observation => Observation,
            ContextHeaderCarrier.Action => Action,
            _ => throw new ArgumentOutOfRangeException(nameof(carrier), carrier, "Unknown context-header carrier.")
        };

    public static bool TryParseStorageToken(string? token, out ContextHeaderCarrier carrier) {
        switch (token) {
            case System:
                carrier = ContextHeaderCarrier.System;
                return true;
            case Observation:
                carrier = ContextHeaderCarrier.Observation;
                return true;
            case Action:
                carrier = ContextHeaderCarrier.Action;
                return true;
            default:
                carrier = default;
                return false;
        }
    }
}

/// <summary>
/// Provider-facing target for one derived context contribution. Carrier and
/// block key are its routing identity; semantic heading is a presentation
/// envelope and deliberately does not participate in identity or ordering.
/// </summary>
public sealed record ContextHeaderBlockTarget {
    public const int MaximumSemanticHeadingUtf8Bytes = 256;

    public ContextHeaderBlockTarget(
        ContextHeaderCarrier carrier,
        string blockKey,
        string semanticHeading
    ) {
        Carrier = carrier;
        BlockKey = string.IsNullOrWhiteSpace(blockKey)
            ? throw new ArgumentException(
                "Context-header block key cannot be empty.",
                nameof(blockKey)
            )
            : blockKey;
        SemanticHeading = ValidateSemanticHeading(
            semanticHeading,
            nameof(semanticHeading)
        );
    }

    public ContextHeaderCarrier Carrier { get; }
    public string BlockKey { get; }
    public string SemanticHeading { get; }

    internal static string ValidateSemanticHeading(
        string value,
        string parameterName
    ) {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(static character =>
                char.IsControl(character)
                || character is '\u2028' or '\u2029')) {
            throw new ArgumentException(
                "Context-header semantic heading must be non-empty, trimmed, single-line, and contain no control characters.",
                parameterName
            );
        }
        try {
            if (new UTF8Encoding(false, true).GetByteCount(value)
                > MaximumSemanticHeadingUtf8Bytes) {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    $"Context-header semantic heading exceeds {MaximumSemanticHeadingUtf8Bytes} UTF-8 bytes."
                );
            }
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException(
                "Context-header semantic heading contains invalid UTF-16.",
                parameterName,
                exception
            );
        }
        return value;
    }
}
