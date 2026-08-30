using System.Security.Cryptography;
using System.Text;

namespace Atelia.Galatea.Server;

internal readonly record struct GalateaVisibleActionFingerprint(
    string Sha256,
    int Utf8Bytes
) {
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    internal static GalateaVisibleActionFingerprint Derive(
        string visibleText
    ) {
        ArgumentNullException.ThrowIfNull(visibleText);
        byte[] utf8 = StrictUtf8.GetBytes(visibleText);
        return new GalateaVisibleActionFingerprint(
            Convert.ToHexString(SHA256.HashData(utf8)).ToLowerInvariant(),
            utf8.Length
        );
    }
}
