namespace Atelia.ChatSession.Memory;

public static class MemoryBlockTextNormalizer {
    public static string NormalizeBlockText(string? text) {
        var trimmed = (text ?? string.Empty).Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) { return trimmed; }

        int firstLineEnd = trimmed.IndexOf('\n', StringComparison.Ordinal);
        if (firstLineEnd < 0) { return trimmed; }

        string openingFence = trimmed[..firstLineEnd].Trim();
        if (!openingFence.StartsWith("```", StringComparison.Ordinal)) { return trimmed; }

        int closingFenceStart = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFenceStart <= firstLineEnd) { return trimmed; }

        string trailing = trimmed[(closingFenceStart + 3)..].Trim();
        if (trailing.Length > 0) { return trimmed; }

        return trimmed[(firstLineEnd + 1)..closingFenceStart].Trim();
    }
}
