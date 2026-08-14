using System.Text;

namespace Atelia.Galatea.RecapGrid;

internal static class PromptResourceLoader {
    internal const string FamilySystemResourceName =
        "Atelia.Galatea.RecapGrid.Prompts.RecapMaintainerFamily.System.zh-CN.md";
    internal const string WorldUnderstandingResourceName =
        "Atelia.Galatea.RecapGrid.Prompts.WorldUnderstanding.Rewrite.zh-CN.user.md";
    internal const string AutobiographyResourceName =
        "Atelia.Galatea.RecapGrid.Prompts.Autobiography.Rewrite.zh-CN.user.md";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    internal static string ReadText(string resourceName, int maximumBytes) {
        using Stream stream = typeof(GalateaRecapGridAssets).Assembly
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException(
                $"The exact embedded prompt resource '{resourceName}' is missing."
            );
        return ReadText(stream, resourceName, maximumBytes);
    }

    internal static string ReadText(
        Stream stream,
        string resourceName,
        int maximumBytes
    ) {
        ArgumentNullException.ThrowIfNull(stream);
        if (string.IsNullOrWhiteSpace(resourceName)) {
            throw new ArgumentException(
                "A resource name is required.",
                nameof(resourceName)
            );
        }
        if (maximumBytes < 1) {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }
        byte[] buffer = new byte[checked(maximumBytes + 1)];
        int count = 0;
        while (count < buffer.Length) {
            int read = stream.Read(buffer, count, buffer.Length - count);
            if (read == 0) { break; }
            count += read;
        }
        if (count > maximumBytes || stream.ReadByte() != -1) {
            throw new InvalidDataException(
                $"The embedded prompt resource '{resourceName}' exceeds its byte limit."
            );
        }
        return DecodeExact(buffer.AsSpan(0, count), resourceName);
    }

    internal static string DecodeExact(
        ReadOnlySpan<byte> bytes,
        string resourceName
    ) {
        if (bytes.IsEmpty) {
            throw new InvalidDataException(
                $"The embedded prompt resource '{resourceName}' is empty."
            );
        }
        if (bytes.Length >= 3
            && bytes[0] == 0xEF
            && bytes[1] == 0xBB
            && bytes[2] == 0xBF) {
            throw new InvalidDataException(
                $"The embedded prompt resource '{resourceName}' has a UTF-8 BOM."
            );
        }
        if (bytes.Contains((byte)'\r')) {
            throw new InvalidDataException(
                $"The embedded prompt resource '{resourceName}' is not LF-only."
            );
        }
        try {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception) {
            throw new InvalidDataException(
                $"The embedded prompt resource '{resourceName}' is not strict UTF-8.",
                exception
            );
        }
    }
}
