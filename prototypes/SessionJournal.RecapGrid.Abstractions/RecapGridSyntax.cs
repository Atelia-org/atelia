using System.Security.Cryptography;
using System.Text;

namespace Atelia.SessionJournal.RecapGrid;

public static class RecapGridLimits {
    public const int MaximumIdentifierUtf8Bytes = 128;
    public const int MaximumSystemPromptUtf8Bytes = 128 * 1024;
    public const int MaximumToolDescriptionUtf8Bytes = 16 * 1024;
    public const int MaximumToolCount = 32;
    public const int MaximumToolSchemaDepth = 16;
    public const int MaximumToolSchemaNodeCount = 4_096;
    public const int MaximumObjectPropertyCount = 256;
    public const int MaximumFamilyCanonicalUtf8Bytes = 512 * 1024;
    public const int MaximumTopicUtf8Bytes = 4 * 1024;
    public const int MaximumUserPromptUtf8Bytes = 64 * 1024;
    public const int MaximumDefinitionCanonicalUtf8Bytes = 128 * 1024;
    public const int MaximumContentUtf8Bytes = 1024 * 1024;
    public const int MaximumColumnCount = 128;
    public const int MaximumTargetCanonicalUtf8Bytes = 64 * 1024;
    public const int MaximumRecipeCanonicalUtf8Bytes = 128 * 1024;
    public const int MaximumProjectionCanonicalUtf8Bytes = 64 * 1024;
    public const int MaximumCellArtifactCanonicalUtf8Bytes =
        MaximumContentUtf8Bytes + 128 * 1024;
    public const int MaximumRowViewCanonicalUtf8Bytes = 512 * 1024;
    public const int MaximumFulfilledViewKeyCanonicalUtf8Bytes = 16 * 1024;
}

internal static class RecapGridSyntax {
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    internal static string RequireIdentifier(
        string value,
        string parameterName
    ) {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(char.IsControl)) {
            throw new ArgumentException(
                "The identifier must be non-empty, trimmed, and contain no control characters.",
                parameterName
            );
        }
        RequireUtf8Length(
            value,
            RecapGridLimits.MaximumIdentifierUtf8Bytes,
            parameterName
        );
        return value;
    }

    internal static string RequireText(
        string value,
        int maximumUtf8Bytes,
        string parameterName,
        bool allowEmpty = false
    ) {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!allowEmpty && string.IsNullOrWhiteSpace(value)) {
            throw new ArgumentException(
                "The text must not be empty.",
                parameterName
            );
        }
        RequireUtf8Length(value, maximumUtf8Bytes, parameterName);
        return value;
    }

    internal static string RequireLowerHex(
        string value,
        int length,
        string parameterName
    ) {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length != length
            || value.Any(static character =>
                character is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f'))) {
            throw new ArgumentException(
                $"The value must contain exactly {length} lowercase hexadecimal characters.",
                parameterName
            );
        }
        return value;
    }

    internal static string RequireTypedValue(
        string? value,
        int length,
        string parameterName
    ) {
        if (value is null) {
            throw new ArgumentException(
                "A default typed identity is not valid.",
                parameterName
            );
        }
        return RequireLowerHex(value, length, parameterName);
    }

    internal static int Utf8Length(string value) {
        try {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException(
                "The value contains invalid UTF-16.",
                nameof(value),
                exception
            );
        }
    }

    internal static void RequireUtf8Length(
        string value,
        int maximumUtf8Bytes,
        string parameterName
    ) {
        try {
            if (StrictUtf8.GetByteCount(value) > maximumUtf8Bytes) {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    $"The UTF-8 value exceeds {maximumUtf8Bytes} bytes."
                );
            }
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException(
                "The value contains invalid UTF-16.",
                parameterName,
                exception
            );
        }
    }

    internal static T[] MaterializeBounded<T>(
        IEnumerable<T> source,
        int maximumCount,
        string parameterName
    ) {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        using IEnumerator<T> enumerator = source.GetEnumerator();
        var values = new List<T>(Math.Min(maximumCount, 16));
        while (values.Count <= maximumCount && enumerator.MoveNext()) {
            values.Add(enumerator.Current);
        }
        if (values.Count > maximumCount) {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"The sequence exceeds {maximumCount} members."
            );
        }
        return values.ToArray();
    }
}

internal static class RecapGridHash {
    internal static string Compute(
        string domain,
        ReadOnlySpan<byte> canonicalBody
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256
        );
        Append(Encoding.UTF8.GetBytes(domain));
        Append(canonicalBody);
        return Convert.ToHexStringLower(hash.GetHashAndReset());

        void Append(ReadOnlySpan<byte> bytes) {
            Span<byte> length = stackalloc byte[sizeof(int)];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(
                length,
                bytes.Length
            );
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
    }
}
