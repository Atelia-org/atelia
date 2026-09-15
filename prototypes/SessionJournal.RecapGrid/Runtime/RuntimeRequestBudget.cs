using System.Text;
using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal.RecapGrid.Runtime;

internal static class RuntimeRequestBudget {
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // A narrow text-payload measure of the existing Runtime V3 request domain.
    // Framing added by provider adapters is outside this metric. In particular,
    // adding future tool definitions requires extending this counter explicitly.
    internal static long MeasureInputUtf8Bytes(CompletionRequest request) {
        if (!request.PromptPrefix.OutputContract.IsProviderDefault
            || !request.PromptPrefix.OutputContract.Tools.IsEmpty) {
            throw new NotSupportedException("Runtime V3 input measurement requires its existing tool-free output contract.");
        }
        long total = 0;
        Add(request.ModelId);
        Add(request.PromptPrefix.SystemPrompt);
        foreach (IHistoryMessage message in request.PromptPrefix.SharedContextMessages) { Measure(message); }
        foreach (IHistoryMessage message in request.TailMessages) { Measure(message); }
        return total;

        void Add(string? text) {
            if (text is not null) { total = checked(total + StrictUtf8.GetByteCount(text)); }
        }

        void Measure(IHistoryMessage message) {
            switch (message) {
                case ToolResultsMessage results:
                    Add(results.Content);
                    foreach (ToolResult result in results.Results) {
                        Add(result.ToolName);
                        Add(result.ToolCallId);
                        Add(result.Status.ToString());
                        foreach (ToolResultBlock block in result.Blocks) {
                            if (block is not ToolResultBlock.Text text) {
                                throw new NotSupportedException("Unsupported recap tool-result block for input measurement.");
                            }
                            Add(text.Content);
                        }
                    }
                    break;
                case ObservationMessage observation when observation.GetType() == typeof(ObservationMessage):
                    Add(observation.Content);
                    break;
                case ActionMessage action:
                    foreach (ActionBlock block in action.Blocks) {
                        switch (block) {
                            case ActionBlock.Text text: Add(text.Content); break;
                            case ActionBlock.ToolCall tool:
                                Add(tool.Call.ToolName);
                                Add(tool.Call.ToolCallId);
                                Add(tool.Call.RawArgumentsJson ?? "{}");
                                break;
                            default: throw new NotSupportedException("Unsupported recap action block for input measurement.");
                        }
                    }
                    break;
                default: throw new NotSupportedException("Unprojected or unsupported recap history for input measurement.");
            }
        }
    }
}
