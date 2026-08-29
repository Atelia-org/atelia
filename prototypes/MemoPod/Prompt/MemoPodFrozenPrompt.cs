using System.Security.Cryptography;
using System.Text;
using Atelia.Completion.Abstractions;

namespace Atelia.MemoPod;

internal sealed class MemoPodFrozenPrompt {
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    private MemoPodFrozenPrompt(
        string exactText,
        int utf8Length,
        string sha256
    ) {
        ExactText = exactText;
        Utf8Length = utf8Length;
        Sha256 = sha256;
    }

    internal string ExactText { get; }
    internal int Utf8Length { get; }
    internal string Sha256 { get; }

    internal ObservationMessage ToHistoryMessage()
        => new(ExactText);

    internal int EstimateTokenCount(
        IMemoPodPromptTokenEstimator estimator
    ) {
        ArgumentNullException.ThrowIfNull(estimator);
        int estimatedTokenCount = estimator.EstimateTokenCount(ExactText);
        if (estimatedTokenCount < 0) {
            throw new InvalidOperationException(
                "MemoPod prompt token estimators must return a non-negative count."
            );
        }
        return estimatedTokenCount;
    }

    internal static MemoPodFrozenPrompt FromOwnedUtf8(byte[] utf8) {
        ArgumentNullException.ThrowIfNull(utf8);
        string exactText = StrictUtf8.GetString(utf8);
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(utf8));
        return new MemoPodFrozenPrompt(exactText, utf8.Length, sha256);
    }
}
