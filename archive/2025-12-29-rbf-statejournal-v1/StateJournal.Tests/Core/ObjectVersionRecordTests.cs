// Tests for ObjectVersionRecord payload layout

using System.Buffers;
using Atelia.StateJournal;
using FluentAssertions;
using Xunit;

namespace Atelia.StateJournal.Tests.Core;

public class ObjectVersionRecordTests {
    // =========================================================================
    // Constants Verification
    // =========================================================================

    [Fact]
    public void Constants_AreCorrect() {
        ObjectVersionRecord.PrevVersionPtrSize.Should().Be(8);
        ObjectVersionRecord.MinPayloadLength.Should().Be(8);
        ObjectVersionRecord.NullPrevVersionPtr.Should().Be(0UL);
    }

    // =========================================================================
    // Roundtrip Tests
    // =========================================================================

    [Fact]
    public void Roundtrip_WithNonZeroPrevPtr_Succeeds() {
        // Arrange
        ulong prevPtr = 0x123456789ABCDEF0UL;
        byte[] diffPayload = [0x01, 0x02, 0x03, 0x04, 0x05];

        // Act - Encode
        var buffer = new ArrayBufferWriter<byte>();
        ObjectVersionRecord.WriteTo(buffer, prevPtr, diffPayload.AsSpan());
        var encoded = buffer.WrittenSpan;

        // Act - Decode
        var result = ObjectVersionRecord.TryParse(encoded, out var decodedPtr, out var decodedDiff);

        // Assert
        result.IsSuccess.Should().BeTrue();
        decodedPtr.Should().Be(prevPtr);
        decodedDiff.ToArray().Should().BeEquivalentTo(diffPayload);
    }

    [Fact]
    public void Roundtrip_WithZeroPrevPtr_Succeeds() {
        // Arrange - Genesis Base (PrevVersionPtr = 0)
        ulong prevPtr = ObjectVersionRecord.NullPrevVersionPtr;
        byte[] diffPayload = [0xAB, 0xCD, 0xEF];

        // Act - Encode
        var buffer = new ArrayBufferWriter<byte>();
        ObjectVersionRecord.WriteTo(buffer, prevPtr, diffPayload.AsSpan());
        var encoded = buffer.WrittenSpan;

        // Act - Decode
        var result = ObjectVersionRecord.TryParse(encoded, out var decodedPtr, out var decodedDiff);

        // Assert
        result.IsSuccess.Should().BeTrue();
        decodedPtr.Should().Be(0UL);
        decodedDiff.ToArray().Should().BeEquivalentTo(diffPayload);
        ObjectVersionRecord.IsBaseVersion(decodedPtr).Should().BeTrue();
    }

    [Fact]
    public void Roundtrip_WithEmptyDiffPayload_Succeeds() {
        // Arrange - Empty diff (still valid for Checkpoint Base with empty dict)
        ulong prevPtr = 0;
        ReadOnlySpan<byte> diffPayload = ReadOnlySpan<byte>.Empty;

        // Act - Encode
        var buffer = new ArrayBufferWriter<byte>();
        ObjectVersionRecord.WriteTo(buffer, prevPtr, diffPayload);
        var encoded = buffer.WrittenSpan;

        // Assert - Encoded length
        encoded.Length.Should().Be(ObjectVersionRecord.PrevVersionPtrSize);

        // Act - Decode
        var result = ObjectVersionRecord.TryParse(encoded, out var decodedPtr, out var decodedDiff);

        // Assert
        result.IsSuccess.Should().BeTrue();
        decodedPtr.Should().Be(0UL);
        decodedDiff.Length.Should().Be(0);
    }

    [Fact]
    public void Roundtrip_WithMaxPrevPtr_Succeeds() {
        // Arrange - Maximum u64 value
        ulong prevPtr = ulong.MaxValue;
        byte[] diffPayload = [0xFF];

        // Act - Encode
        var buffer = new ArrayBufferWriter<byte>();
        ObjectVersionRecord.WriteTo(buffer, prevPtr, diffPayload.AsSpan());
        var encoded = buffer.WrittenSpan;

        // Act - Decode
        var result = ObjectVersionRecord.TryParse(encoded, out var decodedPtr, out var decodedDiff);

        // Assert
        result.IsSuccess.Should().BeTrue();
        decodedPtr.Should().Be(ulong.MaxValue);
        decodedDiff.ToArray().Should().BeEquivalentTo(diffPayload);
        ObjectVersionRecord.IsBaseVersion(decodedPtr).Should().BeFalse();
    }

    [Fact]
    public void Roundtrip_WithMemoryOverload_Succeeds() {
        // Arrange
        ulong prevPtr = 42UL;
        byte[] diffPayload = [0x01, 0x02];
        ReadOnlyMemory<byte> diffMemory = diffPayload;

        // Act - Encode using Memory overload
        var buffer = new ArrayBufferWriter<byte>();
        ObjectVersionRecord.WriteTo(buffer, prevPtr, diffMemory);

        // Act - Decode using Memory overload
        ReadOnlyMemory<byte> encoded = buffer.WrittenMemory;
        var result = ObjectVersionRecord.TryParse(encoded);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.PrevVersionPtr.Should().Be(prevPtr);
        result.Value.DiffPayload.ToArray().Should().BeEquivalentTo(diffPayload);
        result.Value.IsBaseVersion.Should().BeFalse();
    }

    // =========================================================================
    // Boundary Tests
    // =========================================================================

    [Fact]
    public void Boundary_PayloadEmpty_Fails() {
        // Arrange - Empty payload (less than 8 bytes)
        var payload = ReadOnlySpan<byte>.Empty;

        // Act
        var result = ObjectVersionRecord.TryParse(payload, out _, out _);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().BeOfType<ObjectVersionRecordTruncatedError>();
        var error = (ObjectVersionRecordTruncatedError)result.Error!;
        error.ActualLength.Should().Be(0);
        error.MinLength.Should().Be(8);
    }

    [Fact]
    public void Boundary_PayloadTooShort_1Byte_Fails() {
        // Arrange - Only 1 byte (need 8)
        byte[] payload = [0x01];

        // Act
        var result = ObjectVersionRecord.TryParse(payload, out _, out _);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().BeOfType<ObjectVersionRecordTruncatedError>();
        var error = (ObjectVersionRecordTruncatedError)result.Error!;
        error.ActualLength.Should().Be(1);
    }

    [Fact]
    public void Boundary_PayloadTooShort_3Bytes_Fails() {
        // Arrange - Only 3 bytes (need 8)
        byte[] payload = [0x01, 0x02, 0x03];

        // Act
        var result = ObjectVersionRecord.TryParse(payload, out _, out _);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().BeOfType<ObjectVersionRecordTruncatedError>();
        var error = (ObjectVersionRecordTruncatedError)result.Error!;
        error.ActualLength.Should().Be(3);
    }

    [Fact]
    public void Boundary_PayloadTooShort_7Bytes_Fails() {
        // Arrange - Only 7 bytes (need 8)
        byte[] payload = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07];

        // Act
        var result = ObjectVersionRecord.TryParse(payload, out _, out _);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().BeOfType<ObjectVersionRecordTruncatedError>();
        var error = (ObjectVersionRecordTruncatedError)result.Error!;
        error.ActualLength.Should().Be(7);
    }

    [Fact]
    public void Boundary_PayloadExactly8Bytes_Succeeds() {
        // Arrange - Exactly 8 bytes (minimum valid)
        byte[] payload = [0x42, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

        // Act
        var result = ObjectVersionRecord.TryParse(payload, out var prevPtr, out var diffPayload);

        // Assert
        result.IsSuccess.Should().BeTrue();
        prevPtr.Should().Be(0x42UL);  // Little-endian: 0x42 is at byte 0
        diffPayload.Length.Should().Be(0);  // No diff payload
    }

    [Fact]
    public void Boundary_DiffPayload_1Byte_Succeeds() {
        // Arrange - PrevPtr + 1 byte diff
        ulong prevPtr = 100;
        byte[] diffPayload = [0xAA];

        // Act - Encode
        var buffer = new ArrayBufferWriter<byte>();
        ObjectVersionRecord.WriteTo(buffer, prevPtr, diffPayload.AsSpan());

        // Assert - Length
        buffer.WrittenCount.Should().Be(9);  // 8 + 1

        // Act - Decode
        var result = ObjectVersionRecord.TryParse(buffer.WrittenSpan, out var decodedPtr, out var decodedDiff);

        // Assert
        result.IsSuccess.Should().BeTrue();
        decodedPtr.Should().Be(100);
        decodedDiff.Length.Should().Be(1);
        decodedDiff[0].Should().Be(0xAA);
    }

    [Fact]
    public void Boundary_DiffPayload_2Bytes_Succeeds() {
        // Arrange
        ulong prevPtr = 0;
        byte[] diffPayload = [0x01, 0x02];

        // Act
        var buffer = new ArrayBufferWriter<byte>();
        ObjectVersionRecord.WriteTo(buffer, prevPtr, diffPayload.AsSpan());

        // Assert
        buffer.WrittenCount.Should().Be(10);  // 8 + 2

        var result = ObjectVersionRecord.TryParse(buffer.WrittenSpan, out _, out var decodedDiff);
        result.IsSuccess.Should().BeTrue();
        decodedDiff.Length.Should().Be(2);
    }

    [Fact]
    public void Boundary_DiffPayload_3Bytes_Succeeds() {
        // Arrange
        ulong prevPtr = 0xDEADBEEFUL;
        byte[] diffPayload = [0x01, 0x02, 0x03];

        // Act
        var buffer = new ArrayBufferWriter<byte>();
        ObjectVersionRecord.WriteTo(buffer, prevPtr, diffPayload.AsSpan());

        // Assert
        buffer.WrittenCount.Should().Be(11);  // 8 + 3

        var result = ObjectVersionRecord.TryParse(buffer.WrittenSpan, out var decodedPtr, out var decodedDiff);
        result.IsSuccess.Should().BeTrue();
        decodedPtr.Should().Be(0xDEADBEEFUL);
        decodedDiff.Length.Should().Be(3);
    }

    // =========================================================================
    // Wire Format Verification
    // =========================================================================

    [Fact]
    public void WireFormat_PrevPtrIsLittleEndian() {
        // Arrange
        ulong prevPtr = 0x0102030405060708UL;
        ReadOnlySpan<byte> diffPayload = ReadOnlySpan<byte>.Empty;

        // Act
        var buffer = new ArrayBufferWriter<byte>();
        ObjectVersionRecord.WriteTo(buffer, prevPtr, diffPayload);
        var encoded = buffer.WrittenSpan;

        // Assert - Little-endian: LSB first
        encoded[0].Should().Be(0x08);  // Least significant byte
        encoded[1].Should().Be(0x07);
        encoded[2].Should().Be(0x06);
        encoded[3].Should().Be(0x05);
        encoded[4].Should().Be(0x04);
        encoded[5].Should().Be(0x03);
        encoded[6].Should().Be(0x02);
        encoded[7].Should().Be(0x01);  // Most significant byte
    }

    [Fact]
    public void WireFormat_DiffPayloadStartsAtOffset8() {
        // Arrange
        ulong prevPtr = 0;
        byte[] diffPayload = [0xCA, 0xFE, 0xBA, 0xBE];

        // Act
        var buffer = new ArrayBufferWriter<byte>();
        ObjectVersionRecord.WriteTo(buffer, prevPtr, diffPayload.AsSpan());
        var encoded = buffer.WrittenSpan;

        // Assert - DiffPayload starts at offset 8
        encoded[8].Should().Be(0xCA);
        encoded[9].Should().Be(0xFE);
        encoded[10].Should().Be(0xBA);
        encoded[11].Should().Be(0xBE);
    }

    // =========================================================================
    // GetPayloadLength Tests
    // =========================================================================

    [Theory]
    [InlineData(0, 8)]
    [InlineData(1, 9)]
    [InlineData(10, 18)]
    [InlineData(100, 108)]
    public void GetPayloadLength_ReturnsCorrectValue(int diffLength, int expectedTotal) {
        var result = ObjectVersionRecord.GetPayloadLength(diffLength);
        result.Should().Be(expectedTotal);
    }

    // =========================================================================
    // IsBaseVersion Tests
    // =========================================================================

    [Theory]
    [InlineData(0UL, true)]
    [InlineData(1UL, false)]
    [InlineData(ulong.MaxValue, false)]
    [InlineData(0x123456789ABCDEF0UL, false)]
    public void IsBaseVersion_ReturnsCorrectValue(ulong prevPtr, bool expectedIsBase) {
        ObjectVersionRecord.IsBaseVersion(prevPtr).Should().Be(expectedIsBase);
    }

    // =========================================================================
    // ParsedObjectVersionRecord Tests
    // =========================================================================

    [Fact]
    public void ParsedRecord_IsBaseVersion_ReflectsPrevPtr() {
        // Arrange - Genesis Base
        byte[] payload = new byte[8];  // All zeros = PrevVersionPtr = 0

        // Act
        var result = ObjectVersionRecord.TryParse((ReadOnlyMemory<byte>)payload);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.IsBaseVersion.Should().BeTrue();
    }

    [Fact]
    public void ParsedRecord_NonBaseVersion_IsBaseVersionFalse() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        ObjectVersionRecord.WriteTo(buffer, 0x42UL, new byte[] { 0x01 }.AsSpan());

        // Act
        var result = ObjectVersionRecord.TryParse(buffer.WrittenMemory);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.IsBaseVersion.Should().BeFalse();
        result.Value.PrevVersionPtr.Should().Be(0x42UL);
        result.Value.DiffPayload.Length.Should().Be(1);
    }

    // =========================================================================
    // Large DiffPayload Test
    // =========================================================================

    [Fact]
    public void Roundtrip_LargeDiffPayload_Succeeds() {
        // Arrange - 1 KB diff payload
        ulong prevPtr = 999;
        byte[] diffPayload = new byte[1024];
        for (int i = 0; i < diffPayload.Length; i++) {
            diffPayload[i] = (byte)(i & 0xFF);
        }

        // Act - Encode
        var buffer = new ArrayBufferWriter<byte>();
        ObjectVersionRecord.WriteTo(buffer, prevPtr, diffPayload.AsSpan());

        // Assert - Length
        buffer.WrittenCount.Should().Be(8 + 1024);

        // Act - Decode
        var result = ObjectVersionRecord.TryParse(buffer.WrittenSpan, out var decodedPtr, out var decodedDiff);

        // Assert
        result.IsSuccess.Should().BeTrue();
        decodedPtr.Should().Be(999);
        decodedDiff.ToArray().Should().BeEquivalentTo(diffPayload);
    }
}
