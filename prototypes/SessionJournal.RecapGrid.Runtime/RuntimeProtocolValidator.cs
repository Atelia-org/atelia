using System.Collections.Immutable;
using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal.RecapGrid.Runtime;

internal static class RuntimeProtocolValidator {
    internal static RuntimePreflightResult.Rejected? Validate(
        FamilyDefinition family
    ) {
        if (family.OutputProtocol.Mode
                is not FamilyOutputMode.FullReplacementText
            || family.OrderedTools.Count != 0) {
            return new RuntimePreflightResult.Rejected(
                "OutputProtocolMismatch",
                "Runtime V3 requires FullReplacementText output and no tools."
            );
        }
        return null;
    }

    internal static CompletionOutputContract CreateOutputContract(
        FamilyDefinition family
    ) {
        if (family.OrderedTools.Count != 0) {
            throw new InvalidOperationException(
                "FullReplacementText output cannot project tools."
            );
        }
        return CompletionOutputContract.ProviderDefault(
            ImmutableArray<ToolDefinition>.Empty
        );
    }
}
