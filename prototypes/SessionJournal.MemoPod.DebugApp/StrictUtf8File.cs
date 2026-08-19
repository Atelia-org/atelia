using System.Text;

namespace Atelia.SessionJournal.MemoPod.DebugApp;

internal static class StrictUtf8File {
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    internal static string Read(string path, int maximumUtf8Bytes) {
        if (maximumUtf8Bytes <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(maximumUtf8Bytes)
            );
        }

        try {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            using var stream = new FileStream(
                Path.GetFullPath(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan
            );
            if (stream.Length > maximumUtf8Bytes) {
                throw new InvalidDataException(
                    "Operator text input exceeds its UTF-8 byte bound."
                );
            }

            int length = checked((int)stream.Length);
            byte[] bytes = GC.AllocateUninitializedArray<byte>(length);
            stream.ReadExactly(bytes);
            if (stream.Length != length || stream.ReadByte() != -1) {
                throw new InvalidDataException(
                    "Operator text input changed during its bounded read."
                );
            }
            if (bytes is [0xEF, 0xBB, 0xBF, ..]) {
                throw new InvalidDataException(
                    "Operator text input must not contain a UTF-8 BOM."
                );
            }
            return StrictUtf8.GetString(bytes);
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidDataException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException) {
            throw new OperatorInputException(exception);
        }
    }
}
