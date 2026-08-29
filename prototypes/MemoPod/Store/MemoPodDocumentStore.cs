namespace Atelia.MemoPod;

internal static class MemoPodDocumentStore {
    internal static MemoPodDocument Read(
        string rootPath,
        MemoPodId requestedPodId
    ) {
        MemoPodStorePaths paths = MemoPodStoreLayout.Resolve(
            rootPath,
            requestedPodId
        );
        MemoPodStoreLayout.RequireForRead(paths);
        MemoPodStoreLayout.RequireRegularFile(paths.DocumentPath);

        byte[] bytes;
        using (var stream = new FileStream(
            paths.DocumentPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan
        )) {
            if (stream.Length is <= 0
                or > MemoPodLimits.MaximumDocumentUtf8Bytes) {
                throw new MemoPodDocumentLimitException(
                    $"MemoPod document length must be between 1 and {MemoPodLimits.MaximumDocumentUtf8Bytes} bytes."
                );
            }
            bytes = GC.AllocateUninitializedArray<byte>(
                checked((int)stream.Length)
            );
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) {
                throw new MemoPodStoreException(
                    MemoPodStoreErrorCode.DocumentChangedDuringRead,
                    "MemoPod document changed during its bounded read."
                );
            }
        }

        MemoPodDocument document = MemoPodDocumentCodec.Decode(bytes);
        if (document.PodId != requestedPodId) {
            throw new MemoPodStoreException(
                MemoPodStoreErrorCode.DocumentIdentityMismatch,
                $"MemoPod document identity '{document.PodId}' does not match requested identity '{requestedPodId}'."
            );
        }
        return document;
    }
}
