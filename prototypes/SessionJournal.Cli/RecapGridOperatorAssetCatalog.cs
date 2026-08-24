using System.Security.Cryptography;
using System.Text;
using Atelia.Galatea.RecapGrid;
using Atelia.SessionJournal.RecapGrid.AgentControl;
using Atelia.SessionJournal.RecapGrid.Control;

namespace Atelia.SessionJournal.Cli;

/// <summary>
/// Compile-time closed catalog for assets installed by the operator CLI.
/// This is intentionally separate from the Agent Control built-in catalog:
/// adding an operator-only asset must not rotate frozen Agent Control runtime
/// identities.
/// </summary>
internal static class RecapGridOperatorAssetCatalog {
    private static readonly string ProvisionRuntimeIdentityDigest =
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "atelia.session-journal.recap-grid-cli.provision-asset.v1"
        )));

    internal static bool TryCreateRegistrationBundle(
        string assetId,
        out RecapGridControlRegistrationBundle? bundle
    ) {
        switch (assetId) {
            case RecapGridAgentControlBuiltIns.MysteryInvestigationV4:
                return RecapGridAgentControlBuiltIns
                    .TryCreateRegistrationBundle(assetId, out bundle);
            case GalateaRecapGridAssets.RollingRewriteZhCnV4:
                return GalateaRecapGridAssets
                    .TryCreateRegistrationBundle(assetId, out bundle);
            default:
                bundle = null;
                return false;
        }
    }

    /// <summary>
    /// Returns the stable CLI receipt identity for installing one exact
    /// code-owned asset into one exact Control instance.
    /// </summary>
    internal static RecapGridControlOperation CreateProvisionOperation(
        string assetId,
        ControlInstanceId controlInstanceId
    ) {
        if (!TryCreateRegistrationBundle(assetId, out _)) {
            throw new ArgumentException(
                "The code-owned operator asset id is unknown.",
                nameof(assetId)
            );
        }
        if (controlInstanceId.Value is null) {
            throw new ArgumentException(
                "Control instance id must not be default.",
                nameof(controlInstanceId)
            );
        }
        return RecapGridControlOperation.Create(
            $"recap-grid-operator:provision-asset:{controlInstanceId.Value}:{assetId}",
            executionSequence: 1,
            runtimeIdentityDigest: ProvisionRuntimeIdentityDigest
        );
    }
}
