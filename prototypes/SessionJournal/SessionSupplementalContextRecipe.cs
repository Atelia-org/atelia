using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal;

internal enum SessionSupplementalContextStatus {
    NoMatch,
    Selected
}

internal sealed record SessionSupplementalContextControl(
    SessionSupplementalContextStatus Status,
    string? ObservationContent
);

internal sealed record SessionSupplementalContextPartition(
    ImmutableArray<SessionRequestContextInput> RecapInputs,
    SessionRequestContextInput TerminalInput,
    SessionSupplementalContextControl Control
);

/// <summary>
/// Version-owned grammar and expansion for Prepared v6 recipe v2. The terminal
/// supplemental input is a control envelope and is never itself provider-facing.
/// </summary>
internal static class SessionSupplementalContextRecipe {
    public const string RecipeId =
        "atelia.session-journal.coherent-artifact-tail-plus-supplemental.recipe.v2";
    public const string ControlSchemaId =
        "atelia.session-journal.supplemental-context.control.v1";
    public const int MaxRecapInputCount = 128;
    public const int MaxExactContextInputCount = MaxRecapInputCount + 1;

    private const string NoMatchControl =
        "{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"no-match\",\"observationContent\":null}";
    private const string SelectedPrefix =
        "{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"selected\",\"observationContent\":\"";
    private const string SelectedSuffix = "\"}";
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    public static SessionRequestContextInput CreateNoMatchTerminalInput()
        => CreateTerminalInput(NoMatchControl);

    public static SessionRequestContextInput CreateSelectedTerminalInput(
        string observationContent
    ) => CreateTerminalInput(RenderSelectedControl(observationContent));

    public static string RenderSelectedControl(string observationContent) {
        ArgumentNullException.ThrowIfNull(observationContent);
        if (observationContent.Length == 0) {
            throw new ArgumentException(
                "Selected supplemental observation content cannot be empty.",
                nameof(observationContent)
            );
        }

        int encodedContentByteCount = CountCanonicalJsonStringContentUtf8(
            observationContent
        );
        int totalByteCount;
        try {
            totalByteCount = checked(
                SelectedPrefix.Length
                + encodedContentByteCount
                + SelectedSuffix.Length
            );
        }
        catch (OverflowException exception) {
            throw new ArgumentException(
                "Supplemental context control exceeds its UTF-8 byte limit.",
                nameof(observationContent),
                exception
            );
        }
        if (totalByteCount > SessionArtifactContextSnapshotHasher.MaxSnapshotUtf8Bytes) {
            throw new ArgumentException(
                $"Supplemental context control exceeds the {SessionArtifactContextSnapshotHasher.MaxSnapshotUtf8Bytes}-byte UTF-8 limit.",
                nameof(observationContent)
            );
        }

        var builder = new StringBuilder(totalByteCount);
        builder.Append(SelectedPrefix);
        AppendCanonicalJsonStringContent(builder, observationContent);
        builder.Append(SelectedSuffix);
        string result = builder.ToString();
        if (StrictUtf8.GetByteCount(result) != totalByteCount) {
            throw new InvalidOperationException(
                "Supplemental context control UTF-8 pre-count diverged from rendering."
            );
        }
        return result;
    }

    public static SessionSupplementalContextControl ParseControl(string value) {
        ArgumentNullException.ThrowIfNull(value);
        int byteCount;
        byte[] utf8;
        try {
            byteCount = StrictUtf8.GetByteCount(value);
            if (byteCount > SessionArtifactContextSnapshotHasher.MaxSnapshotUtf8Bytes) {
                throw new InvalidDataException(
                    $"Supplemental context control exceeds the {SessionArtifactContextSnapshotHasher.MaxSnapshotUtf8Bytes}-byte UTF-8 limit."
                );
            }
            utf8 = StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException exception) {
            throw new InvalidDataException(
                "Supplemental context control contains invalid Unicode scalar data.",
                exception
            );
        }

        JsonDocument document;
        try {
            document = JsonDocument.Parse(utf8);
        }
        catch (JsonException exception) {
            throw new InvalidDataException(
                "Supplemental context control is not valid JSON.",
                exception
            );
        }
        using (document) {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) {
                throw new InvalidDataException(
                    "Supplemental context control must be a JSON object."
                );
            }
            JsonProperty[] properties = root.EnumerateObject().ToArray();
            if (properties.Length != 3
                || !string.Equals(properties[0].Name, "schema", StringComparison.Ordinal)
                || !string.Equals(properties[1].Name, "status", StringComparison.Ordinal)
                || !string.Equals(properties[2].Name, "observationContent", StringComparison.Ordinal)) {
                throw new InvalidDataException(
                    "Supplemental context control properties must have the exact canonical shape and order."
                );
            }
            if (properties[0].Value.ValueKind != JsonValueKind.String
                || !string.Equals(
                    properties[0].Value.GetString(),
                    ControlSchemaId,
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    "Supplemental context control schema is unsupported."
                );
            }
            if (properties[1].Value.ValueKind != JsonValueKind.String) {
                throw new InvalidDataException(
                    "Supplemental context control status must be a string."
                );
            }

            string status = properties[1].Value.GetString()!;
            SessionSupplementalContextControl control;
            string canonical;
            if (string.Equals(status, "no-match", StringComparison.Ordinal)) {
                if (properties[2].Value.ValueKind != JsonValueKind.Null) {
                    throw new InvalidDataException(
                        "A no-match supplemental context control requires null observationContent."
                    );
                }
                control = new(SessionSupplementalContextStatus.NoMatch, null);
                canonical = NoMatchControl;
            }
            else if (string.Equals(status, "selected", StringComparison.Ordinal)) {
                if (properties[2].Value.ValueKind != JsonValueKind.String) {
                    throw new InvalidDataException(
                        "A selected supplemental context control requires string observationContent."
                    );
                }
                string observationContent = properties[2].Value.GetString()!;
                if (observationContent.Length == 0) {
                    throw new InvalidDataException(
                        "Selected supplemental observation content cannot be empty."
                    );
                }
                control = new(
                    SessionSupplementalContextStatus.Selected,
                    observationContent
                );
                try {
                    canonical = RenderSelectedControl(observationContent);
                }
                catch (ArgumentException exception) {
                    throw new InvalidDataException(
                        "Selected supplemental observation content violates its canonical bounds.",
                        exception
                    );
                }
            }
            else {
                throw new InvalidDataException(
                    $"Unsupported supplemental context control status '{status}'."
                );
            }

            if (!string.Equals(value, canonical, StringComparison.Ordinal)) {
                throw new InvalidDataException(
                    "Supplemental context control is not in canonical form."
                );
            }
            return control;
        }
    }

    public static SessionSupplementalContextPartition ValidateAndPartition(
        ImmutableArray<SessionRequestContextInput> exactContextInputs
    ) {
        if (exactContextInputs.IsDefault) {
            throw new InvalidDataException(
                "Prepared v6 plan.exactContextInputs must be initialized."
            );
        }
        if (exactContextInputs.Length is < 1 or > MaxExactContextInputCount) {
            throw new InvalidDataException(
                $"Prepared v6 plan.exactContextInputs must contain 1 to {MaxExactContextInputCount} entries."
            );
        }

        SessionRequestContextInput terminal = exactContextInputs[^1]
            ?? throw new InvalidDataException(
                "Prepared v6 terminal supplemental context input cannot be null."
            );
        SessionRequestArtifactContextSnapshot snapshot = terminal.ContextSnapshot
            ?? throw new InvalidDataException(
                "Prepared v6 terminal supplemental context snapshot cannot be null."
            );
        if (snapshot.SystemPromptFragment.Length != 0
            || snapshot.ActionMessage.Length != 0
            || snapshot.ObservationMessage.Length == 0) {
            throw new InvalidDataException(
                "Prepared v6 terminal input must contain only the canonical supplemental observation control."
            );
        }
        SessionSupplementalContextControl control = ParseControl(
            snapshot.ObservationMessage
        );
        string actualHash = SessionArtifactContextSnapshotHasher.ComputeSha256(snapshot);
        if (!string.Equals(terminal.ContentSha256, actualHash, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "Prepared v6 terminal contentSha256 does not match its exact control snapshot."
            );
        }

        return new(
            exactContextInputs[..^1],
            terminal,
            control
        );
    }

    public static (
        string SystemPrompt,
        ImmutableArray<IHistoryMessage> Context
    ) Expand(
        string baseSystemPrompt,
        ImmutableArray<SessionRequestContextInput> exactContextInputs
    ) {
        SessionSupplementalContextPartition partition = ValidateAndPartition(
            exactContextInputs
        );
        SessionRequestArtifactContextSnapshot aggregate =
            SessionCoherentRequestRecipe.AggregateExactInputs(
                partition.RecapInputs
            );
        (string systemPrompt, ImmutableArray<IHistoryMessage> recapContext) =
            SessionCoherentRequestRecipe.Expand(baseSystemPrompt, aggregate);
        if (partition.Control.Status == SessionSupplementalContextStatus.NoMatch) {
            return (systemPrompt, recapContext);
        }

        return (
            systemPrompt,
            recapContext.Add(
                new ObservationMessage(partition.Control.ObservationContent)
            )
        );
    }

    private static SessionRequestContextInput CreateTerminalInput(string control) {
        var snapshot = new SessionRequestArtifactContextSnapshot("", control, "");
        return new(
            SessionArtifactContextSnapshotHasher.ComputeSha256(snapshot),
            snapshot
        );
    }

    private static int CountCanonicalJsonStringContentUtf8(string value) {
        int count = 0;
        try {
            for (int index = 0; index < value.Length; index++) {
                char character = value[index];
                if (char.IsHighSurrogate(character)) {
                    if (index + 1 >= value.Length
                        || !char.IsLowSurrogate(value[index + 1])) {
                        throw new EncoderFallbackException(
                            "A high surrogate was not followed by a low surrogate."
                        );
                    }
                    index++;
                    count = checked(count + 4);
                }
                else if (char.IsLowSurrogate(character)) {
                    throw new EncoderFallbackException(
                        "A low surrogate was not preceded by a high surrogate."
                    );
                }
                else if (RequiresShortEscape(character)) {
                    count = checked(count + 2);
                }
                else if (RequiresUnicodeEscape(character)) {
                    count = checked(count + 6);
                }
                else if (character <= 0x7f) {
                    count = checked(count + 1);
                }
                else if (character <= 0x7ff) {
                    count = checked(count + 2);
                }
                else {
                    count = checked(count + 3);
                }
            }
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException(
                "Supplemental observation content contains invalid Unicode scalar data.",
                nameof(value),
                exception
            );
        }
        catch (OverflowException exception) {
            throw new ArgumentException(
                "Supplemental observation content exceeds its UTF-8 byte limit.",
                nameof(value),
                exception
            );
        }
        return count;
    }

    private static void AppendCanonicalJsonStringContent(
        StringBuilder builder,
        string value
    ) {
        for (int index = 0; index < value.Length; index++) {
            char character = value[index];
            switch (character) {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\t': builder.Append("\\t"); break;
                case '\n': builder.Append("\\n"); break;
                case '\f': builder.Append("\\f"); break;
                case '\r': builder.Append("\\r"); break;
                default:
                    if (RequiresUnicodeEscape(character)) {
                        builder.Append("\\u")
                            .Append(
                                ((int)character).ToString(
                                    "x4",
                                    CultureInfo.InvariantCulture
                                )
                            );
                    }
                    else if (char.IsHighSurrogate(character)) {
                        builder.Append(character).Append(value[++index]);
                    }
                    else {
                        builder.Append(character);
                    }
                    break;
            }
        }
    }

    private static bool RequiresShortEscape(char value)
        => value is '"' or '\\' or '\b' or '\t' or '\n' or '\f' or '\r';

    private static bool RequiresUnicodeEscape(char value)
        => (value < 0x20 && !RequiresShortEscape(value))
            || value is >= '\u007f' and <= '\u009f'
            || value is '\u2028' or '\u2029';
}
