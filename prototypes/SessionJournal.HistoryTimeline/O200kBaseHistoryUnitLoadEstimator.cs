using System.Text;
using Atelia.Completion.Abstractions;
using Microsoft.ML.Tokenizers;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.HistoryTimeline;

/// <summary>
/// The fixed H0 reference estimator. Each HistoryUnit is framed and
/// tokenized independently, so its result is not a provider request token
/// count.
/// </summary>
public sealed class O200kBaseHistoryUnitLoadEstimator
    : IHistoryUnitLoadEstimator {
    public const string EstimatorId =
        "atelia.history-load.o200k-base.history-unit-v1";

    private static readonly Lazy<Tokenizer> SharedTokenizer = new(
        static () => TiktokenTokenizer.CreateForEncoding(
            "o200k_base"
        ),
        LazyThreadSafetyMode.ExecutionAndPublication
    );

    public string Id => EstimatorId;

    public HistoryUnitLoadMeasurement Measure(
        SJ.SessionHistoryPlanningUnit unit,
        int maxRenderedUtf8Bytes
    ) {
        ArgumentNullException.ThrowIfNull(unit);
        if (maxRenderedUtf8Bytes <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(maxRenderedUtf8Bytes)
            );
        }

        HistoryUnitLoadRendering rendering =
            HistoryUnitLoadRenderer.Render(
                unit.Message,
                maxRenderedUtf8Bytes
            );
        int tokenCount;
        try {
            tokenCount = SharedTokenizer.Value.CountTokens(
                rendering.Text
            );
        }
        catch (Exception exception) when (
            HistoryLoadNonFatalException.IsCatchable(exception)
        ) {
            throw new HistoryLoadMeasurementException(
                HistoryLoadMeasurementDefectCodes.EstimatorFailed,
                "The o200k_base tokenizer could not measure the "
                + "canonical HistoryUnit rendering.",
                exception
            );
        }
        return new HistoryUnitLoadMeasurement(
            new HistoryLoadUnit(Math.Max(1, tokenCount)),
            rendering.Utf8Bytes
        );
    }

}

internal sealed record HistoryUnitLoadRendering(
    string Text,
    int Utf8Bytes
);

internal static class HistoryUnitLoadRenderer {
    internal static HistoryUnitLoadRendering Render(
        IHistoryMessage message,
        int maxRenderedUtf8Bytes
    ) {
        ArgumentNullException.ThrowIfNull(message);
        if (maxRenderedUtf8Bytes <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(maxRenderedUtf8Bytes)
            );
        }

        var writer = new BoundedStrictUtf8RenderingWriter(
            maxRenderedUtf8Bytes
        );
        switch (message) {
            case ToolResultsMessage toolResults:
                RenderToolResults(writer, toolResults);
                break;
            case ObservationMessage observation
                when observation.GetType()
                    == typeof(ObservationMessage):
                writer.AppendField(
                    "observation",
                    observation.Content
                );
                break;
            case ActionMessage action:
                RenderAction(writer, action);
                break;
            default:
                throw new HistoryLoadMeasurementException(
                    HistoryLoadMeasurementDefectCodes
                        .UnsupportedHistoryMessage,
                    $"History message runtime type "
                    + $"'{message.GetType().FullName}' is not "
                    + "supported by the H0 estimator."
                );
        }
        return writer.Finish();
    }

    private static void RenderAction(
        BoundedStrictUtf8RenderingWriter writer,
        ActionMessage action
    ) {
        writer.AppendTag("action");
        foreach (ActionBlock? block in action.Blocks) {
            switch (block) {
                case ActionBlock.Text text:
                    writer.AppendField(
                        "action-text",
                        text.Content
                    );
                    break;
                case ActionBlock.ToolCall toolCall
                    when toolCall.Call is not null:
                    writer.AppendField(
                        "tool-call-name",
                        toolCall.Call.ToolName
                    );
                    writer.AppendField(
                        "tool-call-arguments",
                        toolCall.Call.RawArgumentsJson ?? "{}"
                    );
                    break;
                case ActionBlock.ReasoningBlock:
                    writer.AppendTag("reasoning-opaque");
                    break;
                default:
                    throw UnsupportedBlock(
                        "action",
                        block?.GetType()
                    );
            }
        }
    }

    private static void RenderToolResults(
        BoundedStrictUtf8RenderingWriter writer,
        ToolResultsMessage toolResults
    ) {
        writer.AppendField(
            "tool-results-content",
            toolResults.Content
        );
        foreach (ToolResult? result in toolResults.Results) {
            if (result is null) {
                throw UnsupportedBlock(
                    "tool result",
                    runtimeType: null
                );
            }
            writer.AppendField(
                "tool-result-name",
                result.ToolName
            );
            writer.AppendField(
                "tool-result-status",
                StatusToken(result.Status)
            );
            foreach (ToolResultBlock? block in result.Blocks) {
                switch (block) {
                    case ToolResultBlock.Text text:
                        writer.AppendField(
                            "tool-result-text",
                            text.Content
                        );
                        break;
                    default:
                        throw UnsupportedBlock(
                            "tool result",
                            block?.GetType()
                        );
                }
            }
        }
    }

    private static string StatusToken(ToolExecutionStatus status)
        => status switch {
            ToolExecutionStatus.Success => "success",
            ToolExecutionStatus.Failed => "failed",
            ToolExecutionStatus.Skipped => "skipped",
            _ => throw new HistoryLoadMeasurementException(
                HistoryLoadMeasurementDefectCodes
                    .UnsupportedHistoryBlock,
                $"Tool result status '{status}' is unsupported."
            )
        };

    private static HistoryLoadMeasurementException UnsupportedBlock(
        string owner,
        Type? runtimeType
    ) => new(
        HistoryLoadMeasurementDefectCodes.UnsupportedHistoryBlock,
        $"{owner} block runtime type "
        + $"'{runtimeType?.FullName ?? "<null>"}' is unsupported."
    );
}

internal sealed class BoundedStrictUtf8RenderingWriter {
    private readonly int _maxUtf8Bytes;
    private readonly StringBuilder _builder = new();
    private int _utf8Bytes;

    internal BoundedStrictUtf8RenderingWriter(int maxUtf8Bytes) {
        _maxUtf8Bytes = maxUtf8Bytes;
    }

    internal void AppendTag(string tag) {
        AppendLiteral("[");
        AppendLiteral(tag);
        AppendLiteral("]\n");
    }

    internal void AppendField(string tag, string? scalar) {
        AppendTag(tag);
        AppendLiteral(scalar ?? string.Empty);
        AppendLiteral("\n");
    }

    internal HistoryUnitLoadRendering Finish()
        => new(_builder.ToString(), _utf8Bytes);

    private void AppendLiteral(string value) {
        for (int index = 0; index < value.Length;) {
            char current = value[index];
            int charCount;
            int utf8Count;
            if (char.IsHighSurrogate(current)) {
                if (index + 1 >= value.Length
                    || !char.IsLowSurrogate(value[index + 1])) {
                    throw InvalidUnicode(index);
                }
                charCount = 2;
                utf8Count = 4;
            }
            else if (char.IsLowSurrogate(current)) {
                throw InvalidUnicode(index);
            }
            else {
                charCount = 1;
                utf8Count = current switch {
                    <= '\u007F' => 1,
                    <= '\u07FF' => 2,
                    _ => 3
                };
            }

            int nextBytes;
            try {
                nextBytes = checked(_utf8Bytes + utf8Count);
            }
            catch (OverflowException exception) {
                throw new HistoryLoadMeasurementException(
                    HistoryLoadMeasurementDefectCodes
                        .MeasurementOverflow,
                    "Canonical HistoryUnit UTF-8 byte count "
                    + "overflowed Int32.",
                    exception
                );
            }
            if (nextBytes > _maxUtf8Bytes) {
                throw new HistoryLoadMeasurementException(
                    HistoryLoadMeasurementDefectCodes
                        .HistoryLoadInputTooLarge,
                    "Canonical HistoryUnit rendering exceeds "
                    + $"{_maxUtf8Bytes} UTF-8 bytes."
                );
            }
            _builder.Append(value, index, charCount);
            _utf8Bytes = nextBytes;
            index += charCount;
        }
    }

    private static HistoryLoadMeasurementException InvalidUnicode(
        int utf16Index
    ) => new(
        HistoryLoadMeasurementDefectCodes.InvalidUnicode,
        "Canonical HistoryUnit input contains an unpaired "
        + $"UTF-16 surrogate at index {utf16Index}."
    );
}
