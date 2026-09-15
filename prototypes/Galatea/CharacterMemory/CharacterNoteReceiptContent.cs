using System.Text.Json;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.MemoPod;

namespace Atelia.Galatea.Server.CharacterMemory;

/// <summary>One immutable Applied batch. Content comes from the capture ledger, never a current Memo lookup.</summary>
internal sealed class CharacterNoteReceiptFacts : IEquatable<CharacterNoteReceiptFacts> {
    internal CharacterNoteReceiptFacts(string sourceActionAddress, IReadOnlyList<CharacterNoteAppliedMemo> memos) {
        _ = EventAddressTextCodec.Parse(sourceActionAddress);
        if (memos.Count is < 1 or > CharacterNoteBounds.MaximumIntentCount) { throw new InvalidDataException("Invalid receipt batch size."); }
        var ids = new HashSet<MemoId>();
        long totalBytes = 0;
        for (int i = 0; i < memos.Count; i++) {
            CharacterNoteAppliedMemo memo = memos[i];
            if (memo is null || string.IsNullOrEmpty(memo.MemoId.Value)
                || memo.SourceActionAddress != sourceActionAddress || memo.ArtifactOrdinal != i
                || memo.PodId != CharacterNoteDefaultPodV1.PodId || !ids.Add(memo.MemoId)) {
                throw new InvalidDataException("Receipt facts must form one ordered Applied batch.");
            }
            GalateaInputContentValidation.RequireText(memo.ExactText, CharacterNoteBounds.MaximumExactTextUtf8Bytes, nameof(memos));
            totalBytes += GalateaBoundedJson.StrictUtf8.GetByteCount(memo.ExactText);
        }
        if (totalBytes > CharacterNoteBounds.MaximumTotalExactTextUtf8Bytes) { throw new InvalidDataException("Receipt facts exceed the Applied batch content limit."); }
        SourceActionAddress = sourceActionAddress;
        Memos = Array.AsReadOnly(memos.ToArray());
    }
    internal string SourceActionAddress { get; }
    internal IReadOnlyList<CharacterNoteAppliedMemo> Memos { get; }
    public bool Equals(CharacterNoteReceiptFacts? other) => other is not null
        && SourceActionAddress == other.SourceActionAddress && Memos.SequenceEqual(other.Memos);
    public override bool Equals(object? obj) => obj is CharacterNoteReceiptFacts other && Equals(other);
    public override int GetHashCode() {
        var hash = new HashCode();
        hash.Add(SourceActionAddress);
        foreach (CharacterNoteAppliedMemo memo in Memos) { hash.Add(memo); }
        return hash.ToHashCode();
    }
}

/// <summary>All saved identities, plus either all complete texts or none. This is content selection.</summary>
internal sealed class CharacterNoteReceiptSelection {
    internal CharacterNoteReceiptSelection(CharacterNoteReceiptFacts facts, bool includeExactTexts)
        : this(facts.SourceActionAddress, facts.Memos.Select(m => m.MemoId).ToArray(),
            includeExactTexts ? facts.Memos.Select(m => m.ExactText).ToArray() : []) { }

    internal CharacterNoteReceiptSelection(string source, IReadOnlyList<MemoId> memoIds, IReadOnlyList<string> exactTexts) {
        _ = EventAddressTextCodec.Parse(source);
        if (memoIds.Count is < 1 or > CharacterNoteBounds.MaximumIntentCount
            || memoIds.Distinct().Count() != memoIds.Count
            || exactTexts.Count != 0 && exactTexts.Count != memoIds.Count) { throw new InvalidDataException("Invalid selected receipt batch."); }
        foreach (string text in exactTexts) { GalateaInputContentValidation.RequireText(text, CharacterNoteBounds.MaximumExactTextUtf8Bytes, nameof(exactTexts)); }
        if (exactTexts.Sum(text => (long)GalateaBoundedJson.StrictUtf8.GetByteCount(text)) > CharacterNoteBounds.MaximumTotalExactTextUtf8Bytes) {
            throw new InvalidDataException("Selected receipt texts exceed the Applied batch content limit.");
        }
        SourceActionAddress = source;
        MemoIds = Array.AsReadOnly(memoIds.ToArray());
        ExactTexts = Array.AsReadOnly(exactTexts.ToArray());
    }
    internal string SourceActionAddress { get; }
    internal IReadOnlyList<MemoId> MemoIds { get; }
    internal IReadOnlyList<string> ExactTexts { get; }
    internal bool IncludeExactTexts => ExactTexts.Count != 0;
    internal JsonElement ToJson() => JsonSerializer.SerializeToElement(new {
        sourceActionAddress = SourceActionAddress,
        podId = CharacterNoteDefaultPodV1.PodIdText,
        saved = MemoIds.Select((memo, ordinal) => new { ordinal, memoId = memo.Value }).ToArray(),
        exactTexts = ExactTexts
    });
}
