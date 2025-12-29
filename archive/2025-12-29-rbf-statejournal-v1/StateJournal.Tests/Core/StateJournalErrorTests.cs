using Atelia.StateJournal;
using FluentAssertions;
using Xunit;

namespace Atelia.StateJournal.Tests.Core;

/// <summary>
/// StateJournalError 错误类型测试。
/// </summary>
public class StateJournalErrorTests {
    [Fact]
    public void StateJournalError_InheritsFromAteliaError() {
        // Arrange
        var error = new VarIntDecodeError("Test error");

        // Assert
        error.Should().BeAssignableTo<AteliaError>();
        error.Should().BeAssignableTo<StateJournalError>();
    }

    [Fact]
    public void VarIntDecodeError_HasCorrectErrorCode() {
        var error = new VarIntDecodeError("EOF while reading varint");

        error.ErrorCode.Should().Be("StateJournal.VarInt.DecodeError");
        error.Message.Should().Be("EOF while reading varint");
    }

    [Fact]
    public void VarIntNonCanonicalError_FormatsMessage() {
        var error = new VarIntNonCanonicalError(Value: 127, ActualBytes: 2, ExpectedBytes: 1);

        error.ErrorCode.Should().Be("StateJournal.VarInt.NonCanonical");
        error.Message.Should().Contain("127");
        error.Message.Should().Contain("2 bytes");
        error.Message.Should().Contain("1 bytes");
    }

    [Fact]
    public void UnknownRecordTypeError_IncludesDetails() {
        var error = new UnknownRecordTypeError(FrameTagValue: 0x00FF0001, RecordType: 0x00FF);

        error.ErrorCode.Should().Be("StateJournal.FrameTag.UnknownRecordType");
        error.Details.Should().NotBeNull();
        error.Details!["RecordType"].Should().Be("0x00FF");
        error.Details["FrameTag"].Should().Be("0x00FF0001");
    }

    [Fact]
    public void UnknownObjectKindError_IncludesDetails() {
        var error = new UnknownObjectKindError(FrameTagValue: 0x00FF0001, ObjectKind: 0x00FF);

        error.ErrorCode.Should().Be("StateJournal.FrameTag.UnknownObjectKind");
        error.Details.Should().NotBeNull();
        error.Details!["ObjectKind"].Should().Be("0x00FF");
    }

    [Fact]
    public void InvalidSubTypeError_IncludesContext() {
        var error = new InvalidSubTypeError(
            FrameTagValue: 0x00010002,
            RecordType: 0x0002,
            SubType: 0x0001
        );

        error.ErrorCode.Should().Be("StateJournal.FrameTag.InvalidSubType");
        error.Message.Should().Contain("0x0001");
    }

    [Fact]
    public void AddressAlignmentError_ShowsOffset() {
        var error = new AddressAlignmentError(Address: 0x12345677);

        error.ErrorCode.Should().Be("StateJournal.Address.Alignment");
        error.Message.Should().Contain("0x0000000012345677");
        error.Message.Should().Contain("offset % 4 = 3");
    }

    [Fact]
    public void AddressOutOfBoundsError_ShowsBounds() {
        var error = new AddressOutOfBoundsError(Address: 1000, FileLength: 500);

        error.ErrorCode.Should().Be("StateJournal.Address.OutOfBounds");
        error.Message.Should().Contain("0x00000000000003E8"); // 1000 in hex
        error.Message.Should().Contain("500");
    }

    [Fact]
    public void ObjectDetachedError_HasRecoveryHint() {
        var error = new ObjectDetachedError(ObjectId: 42);

        error.ErrorCode.Should().Be("StateJournal.Object.Detached");
        error.Message.Should().Contain("42");
        error.RecoveryHint.Should().Contain("CreateObject()");
    }

    [Fact]
    public void ObjectNotFoundError_HasRecoveryHint() {
        var error = new ObjectNotFoundError(ObjectId: 123);

        error.ErrorCode.Should().Be("StateJournal.Object.NotFound");
        error.Message.Should().Contain("123");
        error.RecoveryHint.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void DiffKeySortingError_ShowsKeys() {
        var error = new DiffKeySortingError(PreviousKey: 100, CurrentKey: 50);

        error.ErrorCode.Should().Be("StateJournal.Diff.KeySorting");
        error.Message.Should().Contain("100");
        error.Message.Should().Contain("50");
    }

    [Fact]
    public void UnknownValueTypeError_ShowsByte() {
        var error = new UnknownValueTypeError(ValueTypeByte: 0xFF);

        error.ErrorCode.Should().Be("StateJournal.ValueType.Unknown");
        error.Message.Should().Contain("0xFF");
    }
}
