using System.Security.Cryptography;

namespace Atelia.MemoPod;

public sealed partial class MemoPod {
    internal const string StateIdentityPrefix =
        MemoPodDocumentCodec.Schema + ".sha256:";

    /// <summary>
    /// Computes an opaque identity for the current complete document candidate.
    /// In Frozen phase the candidate is the committed state represented by this
    /// handle; in Editable phase it may contain uncommitted working changes.
    /// </summary>
    /// <remarks>
    /// The identity is the lowercase SHA-256 of the exact canonical MemoPod V2
    /// document bytes, qualified by the document schema. It is not a snapshot,
    /// revision, CAS token, or concurrent-read lease.
    /// </remarks>
    public string ComputeStateIdentity() {
        ThrowIfInvalidated();
        byte[] canonical = MemoPodDocumentCodec.Encode(
            _working.CaptureDocument()
        );
        try {
            return StateIdentityPrefix
                + Convert.ToHexStringLower(SHA256.HashData(canonical));
        }
        finally {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    /// <summary>
    /// Confirms directory durability for the exact current document previously
    /// opened by this Frozen handle.
    /// </summary>
    /// <remarks>
    /// This is a recovery-only settlement seam for a fresh strict Open that has
    /// already proven the expected state identity. Normal successful Freeze does
    /// not require a second confirmation.
    /// </remarks>
    public void ConfirmCurrentDocumentDurability() {
        ThrowIfInvalidated();
        RequirePhase(
            MemoPodPhase.Frozen,
            nameof(ConfirmCurrentDocumentDurability)
        );

        try {
            MemoPodStorePaths paths = MemoPodStoreLayout.Resolve(
                _rootPath,
                _working.PodId
            );
            MemoPodStoreLayout.RequireForRead(paths);
            MemoPodStoreLayout.RequireRegularFile(paths.DocumentPath);
            MemoPodStoreLayout.FlushDirectory(paths.PodsPath);
        }
        catch (Exception exception)
            when (MemoPodPersistenceErrors.CanMap(exception)) {
            throw MemoPodPersistenceErrors.FromException(
                exception,
                "MemoPod could not confirm current document durability."
            );
        }
    }
}
