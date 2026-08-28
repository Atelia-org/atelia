using System.Text;
using Atelia.Galatea.Prompts;

namespace Atelia.Galatea.Server;

internal static class GalateaBuiltInSystemPromptTemplate {
    internal const string ResourceName =
        "Atelia.Galatea.Server.PromptTemplates.TrpgHost.Standard.zh-CN.md";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );
    private static readonly Lazy<byte[]> Bytes = new(LoadAndValidate);

    internal static ReadOnlyMemory<byte> Utf8 => Bytes.Value;

    internal static string Source => StrictUtf8.GetString(Bytes.Value);

    private static byte[] LoadAndValidate() {
        using Stream stream = typeof(GalateaBuiltInSystemPromptTemplate)
            .Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException(
                "The built-in Galatea system prompt template is missing."
            );
        if (stream.Length is < 1
            or > GalateaStrictConfigReader.MaximumSystemPromptUtf8Bytes) {
            throw new InvalidDataException(
                "The built-in Galatea system prompt template is empty or "
                + "exceeds its byte limit."
            );
        }
        byte[] bytes = GC.AllocateUninitializedArray<byte>(
            checked((int)stream.Length)
        );
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1
            || bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble())
            || bytes.AsSpan().Contains((byte)'\r')) {
            throw new InvalidDataException(
                "The built-in Galatea system prompt template must be "
                + "BOM-less, LF-only strict UTF-8."
            );
        }
        string source;
        try {
            source = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception) {
            throw new InvalidDataException(
                "The built-in Galatea system prompt template is not strict "
                + "UTF-8.",
                exception
            );
        }
        _ = GalateaPromptTemplate.Render(
            source,
            new GalateaCharacterName("Galatea"),
            new GalateaPlayerName("Player"),
            GalateaStrictConfigReader.MaximumSystemPromptUtf8Bytes
        );
        if (!source.Contains(
                GalateaPromptTemplate.PlayerNameToken,
                StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "The built-in Galatea system prompt template must reference "
                + GalateaPromptTemplate.PlayerNameToken + "."
            );
        }
        return bytes;
    }
}
