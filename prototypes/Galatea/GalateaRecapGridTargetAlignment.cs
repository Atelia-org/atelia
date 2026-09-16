using Atelia.Galatea.Prompts;
using Atelia.Galatea.RecapGrid;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;

namespace Atelia.Galatea.Server;

internal sealed class GalateaRecapGridDefaultPolicy {
    private GalateaRecapGridDefaultPolicy(BuildTarget target) {
        Target = target ?? throw new ArgumentNullException(nameof(target));
    }

    internal BuildTarget Target { get; }
    internal BuildTargetDigest TargetDigest => Target.Digest;

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
        )));
    }

    internal static GalateaRecapGridDefaultPolicy ForTarget(
        BuildTarget target
    ) {
        ArgumentNullException.ThrowIfNull(target);
        return new GalateaRecapGridDefaultPolicy(target);
    }
}
