namespace Atelia.SessionJournal.Cli;

internal static class MemoryMaintainerOutputUtil {
    public static MemoryBlockTextPreview? CreateBlockPreview(string? text, int tailPreviewChars = 600) {
        if (text is null) { return null; }
        var tailPreview = text.Length <= tailPreviewChars
            ? text
            : text[^tailPreviewChars..];
        return new MemoryBlockTextPreview(text.Length, tailPreview);
    }
}

internal sealed record MemoryBlockTextPreview(int Length, string TailPreview);
