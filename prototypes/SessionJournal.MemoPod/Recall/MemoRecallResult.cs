using System.Collections.Immutable;
using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal.MemoPod;

public sealed class MemoRecallResult {
    internal MemoRecallResult(
        ImmutableArray<Memo> memos,
        string frozenPromptSha256,
        CompletionUsage usage
    ) {
        if (memos.IsDefault) {
            throw new ArgumentException(
                "Recalled memos must be an initialized immutable array.",
                nameof(memos)
            );
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(frozenPromptSha256);
        ArgumentNullException.ThrowIfNull(usage);

        Memos = memos;
        FrozenPromptSha256 = frozenPromptSha256;
        Usage = usage;
    }

    public ImmutableArray<Memo> Memos { get; }
    public string FrozenPromptSha256 { get; }
    public CompletionUsage Usage { get; }
}
