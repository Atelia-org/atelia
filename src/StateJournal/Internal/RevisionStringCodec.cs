namespace Atelia.StateJournal.Internal;

/// <summary>
/// 容器层 string API 与 per-Revision <see cref="SymbolId"/> 存储之间的薄桥接。
/// 统一承接 <see cref="Revision"/> 的 symbol table 语义，避免把 intern/load 细节散落在各容器实现里。
/// </summary>
internal static class RevisionStringCodec {
    internal static SymbolId Encode(Revision revision, string? value) {
        ArgumentNullException.ThrowIfNull(revision);
        return revision.InternSymbol(value);
    }

    internal static GetIssue Decode(Revision revision, SymbolId id, out string? value) {
        ArgumentNullException.ThrowIfNull(revision);

        if (id.IsNull) {
            value = null;
            return GetIssue.None;
        }

        if (!revision.TryGetSymbol(id, out value)) {
            value = null;
            return GetIssue.LoadFailed;
        }

        return GetIssue.None;
    }
}
