using System.Text;
using System.Text.Json;
using Atelia.Galatea.Input;
using Atelia.MemoPod;

namespace Atelia.Galatea.Server;

internal static class ActionReceiptPreview {
    internal static string Create(string text) {
        ArgumentNullException.ThrowIfNull(text);
        _ = GalateaInputValidation.StrictUtf8.GetByteCount(text);
        Rune[] scalars = text.EnumerateRunes().ToArray();
        if (scalars.Length <= 48) { return text; }
        var preview = new StringBuilder();
        foreach (Rune scalar in scalars.AsSpan(0, 32)) { preview.Append(scalar.ToString()); }
        preview.Append(" …（省略）… ");
        foreach (Rune scalar in scalars.AsSpan(scalars.Length - 16)) { preview.Append(scalar.ToString()); }
        return preview.ToString();
    }
}

/// <summary>Frozen confirmation content, or its identity-preserving all-null input projection.</summary>
internal abstract class ActionReceiptBatch : IEquatable<ActionReceiptBatch> {
    private protected ActionReceiptBatch(string sourceActionAddress) {
        ArgumentNullException.ThrowIfNull(sourceActionAddress);
        SourceActionAddress = sourceActionAddress;
    }
    internal string SourceActionAddress { get; }
    internal abstract string Kind { get; }
    internal abstract bool HasPreviews { get; }
    internal abstract ActionReceiptBatch Compact();
    internal abstract JsonElement ToJson();
    internal void RequireFrozen() {
        if (!HasPreviews) { throw new InvalidDataException("Persistent action receipt content must contain frozen previews."); }
    }
    internal bool MatchesProjection(ActionReceiptBatch? candidate) {
        RequireFrozen();
        return candidate is not null && (candidate.HasPreviews ? Equals(candidate) : Compact().Equals(candidate));
    }
    public bool Equals(ActionReceiptBatch? other) => other is not null && GetType() == other.GetType()
        && SourceActionAddress == other.SourceActionAddress && ContentEquals(other);
    public sealed override bool Equals(object? obj) => obj is ActionReceiptBatch other && Equals(other);
    public sealed override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ToJson().GetRawText());
    private protected abstract bool ContentEquals(ActionReceiptBatch other);
}

internal sealed record MailReceiptItem(string DispatchId, string Outcome, string? RecipientPreview, string? Preview);
internal sealed class MailReceiptBatch : ActionReceiptBatch {
    internal MailReceiptBatch(string sourceActionAddress, IReadOnlyList<MailReceiptItem> items) : base(sourceActionAddress) {
        ArgumentNullException.ThrowIfNull(items);
        Items = Array.AsReadOnly(items.ToArray());
        foreach (MailReceiptItem item in Items) {
            if (item is null) { throw new InvalidDataException("Action receipt items must not be null."); }
            if (item.RecipientPreview is not null) { GalateaActionReceiptSchema.RequirePreview(item.RecipientPreview); }
            if (item.Preview is not null) { GalateaActionReceiptSchema.RequirePreview(item.Preview); }
        }
        _ = GalateaActionReceiptSchema.Validate(ToJson());
    }
    internal IReadOnlyList<MailReceiptItem> Items { get; }
    internal override string Kind => "mail";
    internal override bool HasPreviews => Items[0].Preview is not null;
    internal override ActionReceiptBatch Compact() => new MailReceiptBatch(SourceActionAddress,
        Items.Select(item => new MailReceiptItem(item.DispatchId, item.Outcome, null, null)).ToArray());
    internal override JsonElement ToJson() => JsonSerializer.SerializeToElement(new {
        kind = Kind, sourceActionAddress = SourceActionAddress,
        items = Items.Select(item => new { dispatchId = item.DispatchId, outcome = item.Outcome,
            recipientPreview = item.RecipientPreview, preview = item.Preview }).ToArray()
    });
    private protected override bool ContentEquals(ActionReceiptBatch other) => Items.SequenceEqual(((MailReceiptBatch)other).Items);
}

internal sealed record NoteReceiptItem(MemoId MemoId, string? Preview);
internal sealed class NoteReceiptBatch : ActionReceiptBatch {
    internal NoteReceiptBatch(string sourceActionAddress, MemoPodId podId, IReadOnlyList<NoteReceiptItem> items) : base(sourceActionAddress) {
        ArgumentNullException.ThrowIfNull(items);
        PodId = podId;
        Items = Array.AsReadOnly(items.ToArray());
        foreach (NoteReceiptItem item in Items) {
            if (item is null) { throw new InvalidDataException("Action receipt items must not be null."); }
            if (item.Preview is not null) { GalateaActionReceiptSchema.RequirePreview(item.Preview); }
        }
        _ = GalateaActionReceiptSchema.Validate(ToJson());
    }
    internal MemoPodId PodId { get; }
    internal IReadOnlyList<NoteReceiptItem> Items { get; }
    internal override string Kind => "note-save";
    internal override bool HasPreviews => Items[0].Preview is not null;
    internal override ActionReceiptBatch Compact() => new NoteReceiptBatch(SourceActionAddress, PodId,
        Items.Select(item => new NoteReceiptItem(item.MemoId, null)).ToArray());
    internal override JsonElement ToJson() => JsonSerializer.SerializeToElement(new {
        kind = Kind, sourceActionAddress = SourceActionAddress, podId = PodId.Value,
        items = Items.Select(item => new { memoId = item.MemoId.Value, preview = item.Preview }).ToArray()
    });
    private protected override bool ContentEquals(ActionReceiptBatch other) => PodId == ((NoteReceiptBatch)other).PodId
        && Items.SequenceEqual(((NoteReceiptBatch)other).Items);
}

internal static class ActionReceiptBatchCodec {
    internal static string SerializeFrozen(ActionReceiptBatch batch) {
        ArgumentNullException.ThrowIfNull(batch);
        batch.RequireFrozen();
        string json = batch.ToJson().GetRawText();
        RequirePayloadBound(json);
        return json;
    }
    internal static ActionReceiptBatch ReadFrozen(string json) {
        ArgumentNullException.ThrowIfNull(json);
        RequirePayloadBound(json);
        using JsonDocument document = JsonDocument.Parse(json);
        ActionReceiptBatch batch = Read(document.RootElement);
        batch.RequireFrozen();
        return batch;
    }
    internal static ActionReceiptBatch Read(JsonElement value) {
        _ = GalateaActionReceiptSchema.Validate(value);
        string source = value.GetProperty("sourceActionAddress").GetString()!;
        JsonElement.ArrayEnumerator items = value.GetProperty("items").EnumerateArray();
        return value.GetProperty("kind").GetString() switch {
            "mail" => new MailReceiptBatch(source, items.Select(item => new MailReceiptItem(
                item.GetProperty("dispatchId").GetString()!, item.GetProperty("outcome").GetString()!,
                item.GetProperty("recipientPreview").GetString(), item.GetProperty("preview").GetString())).ToArray()),
            "note-save" => new NoteReceiptBatch(source, MemoPodId.Parse(value.GetProperty("podId").GetString()!),
                items.Select(item => new NoteReceiptItem(MemoId.Parse(item.GetProperty("memoId").GetString()!),
                    item.GetProperty("preview").GetString())).ToArray()),
            _ => throw new InvalidDataException("Unknown action receipt kind.")
        };
    }
    private static void RequirePayloadBound(string json) {
        if (GalateaInputValidation.StrictUtf8.GetByteCount(json) > GalateaObservationLimits.MaximumActionReceiptNoticesUtf8Bytes) {
            throw new InvalidDataException("Frozen action receipt content exceeds its JSON byte limit.");
        }
    }
}

/// <summary>Human display uses only the confirmation frozen in the input, without consulting business state.</summary>
internal static class ActionReceiptDisplay {
    internal static string Render(ActionReceiptBatch batch) {
        var text = new StringBuilder("Source Action: ").Append(batch.SourceActionAddress);
        switch (batch) {
            case MailReceiptBatch mail:
                foreach (MailReceiptItem item in mail.Items) {
                    text.Append("\nDispatch: ").Append(item.DispatchId).Append('\n')
                        .Append(item.Outcome == "accepted" ? "本次邮件提交已受理。" : "本次邮件未进入投递队列。");
                    if (item.RecipientPreview is not null) { text.Append("\n收件方识别预览：").Append(item.RecipientPreview); }
                    if (item.Preview is not null) { text.Append("\n正文识别预览：").Append(item.Preview); }
                }
                break;
            case NoteReceiptBatch note:
                text.Append("\n本次 Note 内容已保存到 Default MemoPod。\nMemoPod: ").Append(note.PodId.Value);
                foreach (NoteReceiptItem item in note.Items) {
                    text.Append("\nMemo: ").Append(item.MemoId.Value);
                    if (item.Preview is not null) { text.Append("\n正文识别预览：").Append(item.Preview); }
                }
                break;
            default: throw new InvalidDataException("Unknown action receipt kind.");
        }
        return text.ToString();
    }
}
