using Atelia.Galatea.Prompts;
using Atelia.Galatea.RecapGrid;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;

namespace Atelia.Galatea.Server;

internal sealed class GalateaRecapGridDefaultPolicy {
    private GalateaRecapGridDefaultPolicy(
        BuildTarget target,
        RecapGridControlRegistrationBundle? registrationBundle
    ) {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        RegistrationBundle = registrationBundle;
    }

    internal BuildTarget Target { get; }
    internal BuildTargetDigest TargetDigest => Target.Digest;
    internal RecapGridControlRegistrationBundle? RegistrationBundle { get; }

    internal static GalateaRecapGridDefaultPolicy ForCharacter(
        GalateaCharacterName characterName
    ) {
        ArgumentNullException.ThrowIfNull(characterName);
        if (!GalateaRecapGridAssets.TryCreateRegistrationBundle(
                GalateaRecapGridAssets.RollingRewriteZhCnV7,
                new GalateaRecapGridAssetParameters(
                    characterName
                ),
                out RecapGridControlRegistrationBundle? bundle)
            || bundle is null) {
            throw new InvalidDataException(
                "The current Galatea RecapGrid asset is unavailable."
            );
        }
        return ForTarget(BuildTarget.Create(bundle.Definitions.Select(
            static definition => new BuildTargetColumn(
                definition.LogicalColumnId,
                definition.Digest
            )
        )), bundle);
    }

    internal static GalateaRecapGridDefaultPolicy ForTarget(
        BuildTarget target
    ) {
        ArgumentNullException.ThrowIfNull(target);
        return new GalateaRecapGridDefaultPolicy(target, null);
    }

    internal static GalateaRecapGridDefaultPolicy ForTarget(
        BuildTarget target,
        RecapGridControlRegistrationBundle registrationBundle
    ) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(registrationBundle);
        MaintainerDefinitionRevision[] definitions =
            registrationBundle.Definitions.ToArray();
        HashSet<FamilyDefinitionDigest> definitionFamilies = definitions
            .Select(static definition => definition.FamilyDigest)
            .ToHashSet();
        if (registrationBundle.Recipes.Count != 0
            || target.OrderedColumns.Count != definitions.Length
            || target.OrderedColumns.Where((column, index) =>
                column.LogicalColumnId != definitions[index].LogicalColumnId
                || column.DefinitionDigest != definitions[index].Digest
            ).Any()
            || !definitionFamilies.SetEquals(
                registrationBundle.Families.Select(static family =>
                    family.Digest))) {
            throw new ArgumentException(
                "A default-policy bundle must contain exactly its target definitions and no recipes.",
                nameof(registrationBundle)
            );
        }
        return new GalateaRecapGridDefaultPolicy(target, registrationBundle);
    }
}
