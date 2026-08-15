using System.Text.Json;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;

namespace Atelia.SessionJournal.RecapGrid.Control;

internal static class ControlOperationCanonicalizer {
    internal static string RegistrationDigest(
        RecapGridControlRegistrationBundle bundle
    ) => RecapGridControlOperation.DomainHash(
        "atelia.recap-grid.control-command.registration.v1",
        EncodeRegistration(bundle)
    );

    internal static byte[] EncodeRegistration(
        RecapGridControlRegistrationBundle bundle
    ) => JsonSerializer.SerializeToUtf8Bytes(
            new RegistrationCommandDto(
                bundle.Families.Select(static value =>
                    value.ToCanonicalBytes()).ToArray(),
                bundle.Definitions.Select(static value =>
                    value.ToCanonicalBytes()).ToArray(),
                bundle.Recipes.Select(static value => new RecipeCommandDto(
                    value.Recipe.ToCanonicalBytes(),
                    value.BootstrapWitness?.RowId.Value,
                    value.BootstrapWitness?.DescriptorDigest.Value
                )).ToArray()
            ),
            ControlJson.Options
        );

    internal static string PromotionDigest(
        GridBuildRecipeDigest recipeDigest
    ) => RecapGridControlOperation.DomainHash(
        "atelia.recap-grid.control-command.promotion.v1",
        JsonSerializer.SerializeToUtf8Bytes(
            new PromotionCommandDto(
                recipeDigest.Value
            ),
            ControlJson.Options
        )
    );

    internal static string ResultIdentity(
        string commandDigest,
        string terminalKind
    ) => RecapGridControlOperation.DomainHash(
        "atelia.recap-grid.control-operation-result.v1",
        JsonSerializer.SerializeToUtf8Bytes(
            new ResultDto(commandDigest, terminalKind),
            ControlJson.Options
        )
    );

    private sealed record RegistrationCommandDto(
        byte[][] Families,
        byte[][] Definitions,
        RecipeCommandDto[] Recipes
    );

    private sealed record RecipeCommandDto(
        byte[] Recipe,
        string? BootstrapRowId,
        string? BootstrapDescriptorDigest
    );

    private sealed record PromotionCommandDto(
        string RecipeDigest
    );

    private sealed record ResultDto(
        string CommandDigest,
        string TerminalKind
    );

}
