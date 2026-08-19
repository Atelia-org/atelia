using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.Tests;

internal static class PreparedV6Fixture {
    public static CompletionRequestPreparedBody Create(
        string? selectedObservationContent = "supplemental detail",
        ImmutableArray<SessionRequestContextInput>? recapInputs = null
    ) {
        CompletionRequestPreparedBody v5 = PreparedV5Fixture.Create(
            "correlation-01",
            "observation",
            Address(1),
            Address(2),
            Address(3),
            Address(4),
            "model-A",
            ImmutableArray<ToolDefinition>.Empty,
            toolRuntimeIdentity: null
        );
        ImmutableArray<SessionRequestContextInput> recap =
            recapInputs ?? v5.Plan.ExactContextInputs;
        SessionRequestContextInput terminal = selectedObservationContent is null
            ? SessionSupplementalContextRecipe.CreateNoMatchTerminalInput()
            : SessionSupplementalContextRecipe.CreateSelectedTerminalInput(
                selectedObservationContent
            );
        return v5 with {
            Plan = v5.Plan with {
                ExactContextInputs = [.. recap, terminal]
            },
            Recipe = v5.Recipe with {
                RecipeId = SessionSupplementalContextRecipe.RecipeId
            }
        };
    }

    public static CompletionRequestPreparedBody Upgrade(
        CompletionRequestPreparedBody v5,
        string? selectedObservationContent
    ) {
        ArgumentNullException.ThrowIfNull(v5);
        SessionRequestContextInput terminal = selectedObservationContent is null
            ? SessionSupplementalContextRecipe.CreateNoMatchTerminalInput()
            : SessionSupplementalContextRecipe.CreateSelectedTerminalInput(
                selectedObservationContent
            );
        return v5 with {
            Plan = v5.Plan with {
                ExactContextInputs = [.. v5.Plan.ExactContextInputs, terminal]
            },
            Recipe = v5.Recipe with {
                RecipeId = SessionSupplementalContextRecipe.RecipeId
            }
        };
    }

    private static EventAddress Address(int ticket)
        => EventAddressTextCodec.Parse(
            $"ej1:{ticket:X16}0000000100000000".ToLowerInvariant()
        );
}
