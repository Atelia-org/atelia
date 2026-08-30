using System.Security.Cryptography;
using System.Text;
using Atelia.EventJournal;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server.CharacterMemory;

/// <summary>
/// Attaches the per-user Character Memory authority to one writable
/// SessionJournal without starting any extraction reconciliation.
/// </summary>
internal static class CharacterMemorySessionComposition {
    private const string SessionRepositoryPrefix = "cmsr1-";
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    internal static async ValueTask<CharacterNoteDefaultPodReconciler>
        AttachWritableSessionAsync(
        GalateaUserConfig user,
        SessionJournalEngine engine,
        ICharacterNoteExtractor extractor
    ) {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(extractor);

        string sessionDirectory = RequireCanonicalAbsoluteDirectory(
            user.SessionDir,
            nameof(user.SessionDir)
        );
        string engineDirectory = RequireCanonicalAbsoluteDirectory(
            engine.Path,
            nameof(engine.Path)
        );
        if (!string.Equals(
                sessionDirectory,
                engineDirectory,
                StringComparison.Ordinal)) {
            throw new InvalidOperationException(
                "Character Memory session identity does not match the "
                + "attached SessionJournal."
            );
        }

        string storeDirectory = RequireCanonicalAbsoluteDirectory(
            user.CharacterMemoryStateDir,
            nameof(user.CharacterMemoryStateDir)
        );
        var owner = new CharacterMemoryStoreOwner(
            user.UserId,
            CreateSessionRepositoryId(sessionDirectory)
        );
        if (Path.Exists(storeDirectory)) {
            return await CharacterNoteDefaultPodReconciler
                .OpenExistingAsync(storeDirectory, owner, extractor)
                .ConfigureAwait(false);
        }

        EventJournalPhysicalAppendFrontier frontier = engine.ReadView
            .ReadPhysicalAppendFrontier();
        string? selectedHead = engine.ReadView.ReadCurrentHead() is { } head
            ? EventAddressTextCodec.Format(head)
            : null;
        string parent = Path.GetDirectoryName(storeDirectory)
            ?? throw new InvalidOperationException(
                "Character Memory store has no parent directory."
            );
        Directory.CreateDirectory(parent);
        return await CharacterNoteDefaultPodReconciler.CreateNewAsync(
                storeDirectory,
                owner,
                new CharacterMemoryStoreBaseline(frontier, selectedHead),
                extractor
            )
            .ConfigureAwait(false);
    }

    internal static string CreateSessionRepositoryId(
        string sessionDirectory
    ) {
        string canonical = RequireCanonicalAbsoluteDirectory(
            sessionDirectory,
            nameof(sessionDirectory)
        );
        byte[] utf8 = StrictUtf8.GetBytes(canonical);
        return SessionRepositoryPrefix
            + Convert.ToHexString(SHA256.HashData(utf8)).ToLowerInvariant();
    }

    private static string RequireCanonicalAbsoluteDirectory(
        string path,
        string parameterName
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path)) {
            throw new ArgumentException(
                "Character Memory paths must be absolute.",
                parameterName
            );
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
