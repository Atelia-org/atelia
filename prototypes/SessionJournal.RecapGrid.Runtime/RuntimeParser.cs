using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.RecapGrid.Manager;

namespace Atelia.SessionJournal.RecapGrid.Runtime;

internal abstract record RuntimeParseResult {
    private RuntimeParseResult() { }

    internal sealed record Parsed(RecapCellExecutionOutcome Outcome)
        : RuntimeParseResult;

    internal sealed record Failed(string Code, string Detail)
        : RuntimeParseResult;
}

internal static class RuntimeParser {
    internal const int MaximumNeutralContentUtf8Bytes = 256 * 1024;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    internal static RuntimeParseResult Parse(
        PreparedRecapWork prepared,
        CompletionResult result
    ) {
        if (result is null) {
            return Failed(
                "CompletionResultNull",
                "The completion invoker returned null."
            );
        }
        if (!result.Termination.IsSuccess) {
            return Failed(
                "CompletionIncomplete",
                "Completion did not terminate successfully."
            );
        }
        if (result.Errors is { Count: > 0 }) {
            return Failed(
                "CompletionReportedErrors",
                "Completion reported provider errors."
            );
        }
        RecapCompletionRoute route = prepared.Route;
        if (!string.Equals(
                result.Invocation.ProviderId,
                route.Invoker.ProviderId,
                StringComparison.Ordinal)
            || !string.Equals(
                result.Invocation.ApiSpecId,
                route.Invoker.ApiSpecId,
                StringComparison.Ordinal)
            || !string.Equals(
                result.Invocation.Model,
                route.ModelId,
                StringComparison.Ordinal)) {
            return Failed(
                "InvocationMismatch",
                "Completion invocation differs from the selected route."
            );
        }

        ActionBlock.Text? replacement = null;
        foreach (ActionBlock output in result.Message.Blocks) {
            switch (output) {
                case ActionBlock.ReasoningBlock:
                    continue;
                case ActionBlock.Text text when replacement is null:
                    replacement = text;
                    continue;
                default:
                    return InvalidOutputEnvelope();
            }
        }
        if (replacement is null) {
            return InvalidOutputEnvelope();
        }

        string content = replacement.Content;
        if (string.IsNullOrWhiteSpace(content)) {
            return Failed(
                "FullReplacementTextBlank",
                "Full replacement text must be non-blank."
            );
        }
        int contentBytes;
        try {
            contentBytes = StrictUtf8.GetByteCount(content);
        }
        catch (EncoderFallbackException) {
            return Failed(
                "FullReplacementTextInvalidUtf16",
                "Full replacement text contains invalid UTF-16."
            );
        }
        int cap = Math.Min(
            prepared.Work.Definition.MaxContentUtf8Bytes,
            MaximumNeutralContentUtf8Bytes
        );
        if (contentBytes > cap) {
            return Failed(
                "FullReplacementTextTooLarge",
                "Full replacement text exceeds its exact V3 byte cap."
            );
        }
        RecapCellExecutionOutcome outcome = prepared.SameColumnPrior is {
            Content: { } priorContent
        } && string.Equals(content, priorContent, StringComparison.Ordinal)
            ? new RecapCellExecutionOutcome.KeepUnchanged(
                prepared.Work.EvaluationKey.Digest
            )
            : new RecapCellExecutionOutcome.Updated(
                prepared.Work.EvaluationKey.Digest,
                content
            );
        return new RuntimeParseResult.Parsed(outcome);
    }

    private static RuntimeParseResult.Failed Failed(
        string code,
        string detail
    ) => new(code, detail);

    private static RuntimeParseResult.Failed InvalidOutputEnvelope() =>
        Failed(
            "FullReplacementTextInvalid",
            "V3 requires optional reasoning and exactly one full replacement text block."
        );
}
