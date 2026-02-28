using System;

namespace Atelia.StateJournal;

partial struct ValueBox {
    public GetStatus GetCoerced(out double value) => Get(out value);
    public GetStatus GetCoerced(out float value) => Get(out value);
    public GetStatus GetCoerced(out Half value) => Get(out value);

    public GetStatus GetCoerced(out long value) {
        GetStatus status = TryGetIntegerSource(out long signed, out ulong unsigned, out bool isUnsigned);
        if (status == GetStatus.Success) {
            return isUnsigned ? CoerceUnsignedToInt64(unsigned, out value) : CoerceSignedToInt64(signed, out value);
        }
        if (status != GetStatus.TypeMismatch) {
            value = default;
            return status;
        }
        GetStatus fpStatus = Get(out double d);
        if (fpStatus != GetStatus.Success && fpStatus != GetStatus.PrecisionLost) {
            value = default;
            return fpStatus;
        }
        return CoerceDoubleToInt64(d, out value);
    }

    public GetStatus GetCoerced(out ulong value) {
        GetStatus status = TryGetIntegerSource(out long signed, out ulong unsigned, out bool isUnsigned);
        if (status == GetStatus.Success) {
            return isUnsigned ? CoerceUnsignedToUInt64(unsigned, out value) : CoerceSignedToUInt64(signed, out value);
        }
        if (status != GetStatus.TypeMismatch) {
            value = default;
            return status;
        }
        GetStatus fpStatus = Get(out double d);
        if (fpStatus != GetStatus.Success && fpStatus != GetStatus.PrecisionLost) {
            value = default;
            return fpStatus;
        }
        return CoerceDoubleToUInt64(d, out value);
    }

    public GetStatus GetCoerced(out int value) {
        GetStatus status = TryGetIntegerSource(out long signed, out ulong unsigned, out bool isUnsigned);
        if (status == GetStatus.Success) {
            return isUnsigned ? CoerceUnsignedToInt32(unsigned, out value) : CoerceSignedToInt32(signed, out value);
        }
        if (status != GetStatus.TypeMismatch) {
            value = default;
            return status;
        }
        GetStatus fpStatus = Get(out double d);
        if (fpStatus != GetStatus.Success && fpStatus != GetStatus.PrecisionLost) {
            value = default;
            return fpStatus;
        }
        return CoerceDoubleToInt32(d, out value);
    }

    public GetStatus GetCoerced(out uint value) {
        GetStatus status = TryGetIntegerSource(out long signed, out ulong unsigned, out bool isUnsigned);
        if (status == GetStatus.Success) {
            return isUnsigned ? CoerceUnsignedToUInt32(unsigned, out value) : CoerceSignedToUInt32(signed, out value);
        }
        if (status != GetStatus.TypeMismatch) {
            value = default;
            return status;
        }
        GetStatus fpStatus = Get(out double d);
        if (fpStatus != GetStatus.Success && fpStatus != GetStatus.PrecisionLost) {
            value = default;
            return fpStatus;
        }
        return CoerceDoubleToUInt32(d, out value);
    }

    public GetStatus GetCoerced(out short value) {
        GetStatus status = TryGetIntegerSource(out long signed, out ulong unsigned, out bool isUnsigned);
        if (status == GetStatus.Success) {
            return isUnsigned ? CoerceUnsignedToInt16(unsigned, out value) : CoerceSignedToInt16(signed, out value);
        }
        if (status != GetStatus.TypeMismatch) {
            value = default;
            return status;
        }
        GetStatus fpStatus = Get(out double d);
        if (fpStatus != GetStatus.Success && fpStatus != GetStatus.PrecisionLost) {
            value = default;
            return fpStatus;
        }
        return CoerceDoubleToInt16(d, out value);
    }

    public GetStatus GetCoerced(out ushort value) {
        GetStatus status = TryGetIntegerSource(out long signed, out ulong unsigned, out bool isUnsigned);
        if (status == GetStatus.Success) {
            return isUnsigned ? CoerceUnsignedToUInt16(unsigned, out value) : CoerceSignedToUInt16(signed, out value);
        }
        if (status != GetStatus.TypeMismatch) {
            value = default;
            return status;
        }
        GetStatus fpStatus = Get(out double d);
        if (fpStatus != GetStatus.Success && fpStatus != GetStatus.PrecisionLost) {
            value = default;
            return fpStatus;
        }
        return CoerceDoubleToUInt16(d, out value);
    }

    public GetStatus GetCoerced(out sbyte value) {
        GetStatus status = TryGetIntegerSource(out long signed, out ulong unsigned, out bool isUnsigned);
        if (status == GetStatus.Success) {
            return isUnsigned ? CoerceUnsignedToSByte(unsigned, out value) : CoerceSignedToSByte(signed, out value);
        }
        if (status != GetStatus.TypeMismatch) {
            value = default;
            return status;
        }
        GetStatus fpStatus = Get(out double d);
        if (fpStatus != GetStatus.Success && fpStatus != GetStatus.PrecisionLost) {
            value = default;
            return fpStatus;
        }
        return CoerceDoubleToSByte(d, out value);
    }

    public GetStatus GetCoerced(out byte value) {
        GetStatus status = TryGetIntegerSource(out long signed, out ulong unsigned, out bool isUnsigned);
        if (status == GetStatus.Success) {
            return isUnsigned ? CoerceUnsignedToByte(unsigned, out value) : CoerceSignedToByte(signed, out value);
        }
        if (status != GetStatus.TypeMismatch) {
            value = default;
            return status;
        }
        GetStatus fpStatus = Get(out double d);
        if (fpStatus != GetStatus.Success && fpStatus != GetStatus.PrecisionLost) {
            value = default;
            return fpStatus;
        }
        return CoerceDoubleToByte(d, out value);
    }

    private GetStatus TryGetIntegerSource(out long signed, out ulong unsigned, out bool isUnsigned) {
        GetStatus status = Get(out long signedValue);
        if (status == GetStatus.Success) {
            signed = signedValue;
            unsigned = default;
            isUnsigned = false;
            return GetStatus.Success;
        }
        if (status == GetStatus.OutOfRange) {
            GetStatus uStatus = Get(out ulong unsignedValue);
            if (uStatus == GetStatus.Success) {
                signed = default;
                unsigned = unsignedValue;
                isUnsigned = true;
                return GetStatus.Success;
            }
            signed = default;
            unsigned = default;
            isUnsigned = false;
            return uStatus;
        }
        signed = default;
        unsigned = default;
        isUnsigned = false;
        return status;
    }

    private static GetStatus CoerceSignedToInt64(long source, out long value) {
        value = source;
        return GetStatus.Success;
    }

    private static GetStatus CoerceUnsignedToInt64(ulong source, out long value) {
        if (source <= long.MaxValue) {
            value = (long)source;
            return GetStatus.Success;
        }
        value = unchecked((long)source);
        return GetStatus.SignednessChanged;
    }

    private static GetStatus CoerceSignedToUInt64(long source, out ulong value) {
        if (source >= 0) {
            value = (ulong)source;
            return GetStatus.Success;
        }
        value = unchecked((ulong)source);
        return GetStatus.SignednessChanged;
    }

    private static GetStatus CoerceUnsignedToUInt64(ulong source, out ulong value) {
        value = source;
        return GetStatus.Success;
    }

    private static GetStatus CoerceSignedToInt32(long source, out int value) {
        if (source >= int.MinValue && source <= int.MaxValue) {
            value = (int)source;
            return GetStatus.Success;
        }
        value = unchecked((int)source);
        return GetStatus.Truncated;
    }

    private static GetStatus CoerceUnsignedToInt32(ulong source, out int value) {
        if (source <= int.MaxValue) {
            value = (int)source;
            return GetStatus.Success;
        }
        if (source <= uint.MaxValue) {
            value = unchecked((int)source);
            return GetStatus.SignednessChanged;
        }
        value = unchecked((int)source);
        return GetStatus.Truncated;
    }

    private static GetStatus CoerceSignedToUInt32(long source, out uint value) {
        if (source >= 0 && source <= uint.MaxValue) {
            value = (uint)source;
            return GetStatus.Success;
        }
        if (source >= int.MinValue && source < 0) {
            value = unchecked((uint)source);
            return GetStatus.SignednessChanged;
        }
        value = unchecked((uint)source);
        return GetStatus.Truncated;
    }

    private static GetStatus CoerceUnsignedToUInt32(ulong source, out uint value) {
        if (source <= uint.MaxValue) {
            value = (uint)source;
            return GetStatus.Success;
        }
        value = unchecked((uint)source);
        return GetStatus.Truncated;
    }

    private static GetStatus CoerceSignedToInt16(long source, out short value) {
        if (source >= short.MinValue && source <= short.MaxValue) {
            value = (short)source;
            return GetStatus.Success;
        }
        value = unchecked((short)source);
        return GetStatus.Truncated;
    }

    private static GetStatus CoerceUnsignedToInt16(ulong source, out short value) {
        if (source <= (ulong)short.MaxValue) {
            value = (short)source;
            return GetStatus.Success;
        }
        if (source <= ushort.MaxValue) {
            value = unchecked((short)source);
            return GetStatus.SignednessChanged;
        }
        value = unchecked((short)source);
        return GetStatus.Truncated;
    }

    private static GetStatus CoerceSignedToUInt16(long source, out ushort value) {
        if (source >= 0 && source <= ushort.MaxValue) {
            value = (ushort)source;
            return GetStatus.Success;
        }
        if (source >= short.MinValue && source < 0) {
            value = unchecked((ushort)source);
            return GetStatus.SignednessChanged;
        }
        value = unchecked((ushort)source);
        return GetStatus.Truncated;
    }

    private static GetStatus CoerceUnsignedToUInt16(ulong source, out ushort value) {
        if (source <= ushort.MaxValue) {
            value = (ushort)source;
            return GetStatus.Success;
        }
        value = unchecked((ushort)source);
        return GetStatus.Truncated;
    }

    private static GetStatus CoerceSignedToSByte(long source, out sbyte value) {
        if (source >= sbyte.MinValue && source <= sbyte.MaxValue) {
            value = (sbyte)source;
            return GetStatus.Success;
        }
        value = unchecked((sbyte)source);
        return GetStatus.Truncated;
    }

    private static GetStatus CoerceUnsignedToSByte(ulong source, out sbyte value) {
        if (source <= (ulong)sbyte.MaxValue) {
            value = (sbyte)source;
            return GetStatus.Success;
        }
        if (source <= byte.MaxValue) {
            value = unchecked((sbyte)source);
            return GetStatus.SignednessChanged;
        }
        value = unchecked((sbyte)source);
        return GetStatus.Truncated;
    }

    private static GetStatus CoerceSignedToByte(long source, out byte value) {
        if (source >= 0 && source <= byte.MaxValue) {
            value = (byte)source;
            return GetStatus.Success;
        }
        if (source >= sbyte.MinValue && source < 0) {
            value = unchecked((byte)source);
            return GetStatus.SignednessChanged;
        }
        value = unchecked((byte)source);
        return GetStatus.Truncated;
    }

    private static GetStatus CoerceUnsignedToByte(ulong source, out byte value) {
        if (source <= byte.MaxValue) {
            value = (byte)source;
            return GetStatus.Success;
        }
        value = unchecked((byte)source);
        return GetStatus.Truncated;
    }

    private static GetStatus CoerceDoubleToInt64(double source, out long value) {
        if (!double.IsFinite(source)) {
            value = unchecked((long)source);
            return GetStatus.OutOfRange;
        }
        if (source < long.MinValue || source > long.MaxValue) {
            value = unchecked((long)source);
            return GetStatus.OutOfRange;
        }
        double truncated = Math.Truncate(source);
        value = (long)truncated;
        return truncated == source ? GetStatus.Success : GetStatus.Truncated;
    }

    private static GetStatus CoerceDoubleToUInt64(double source, out ulong value) {
        if (!double.IsFinite(source)) {
            value = unchecked((ulong)source);
            return GetStatus.OutOfRange;
        }
        if (source < 0 || source > ulong.MaxValue) {
            value = unchecked((ulong)source);
            return GetStatus.OutOfRange;
        }
        double truncated = Math.Truncate(source);
        value = (ulong)truncated;
        return truncated == source ? GetStatus.Success : GetStatus.Truncated;
    }

    private static GetStatus CoerceDoubleToInt32(double source, out int value) {
        if (!double.IsFinite(source)) {
            value = unchecked((int)source);
            return GetStatus.OutOfRange;
        }
        if (source < int.MinValue || source > int.MaxValue) {
            value = unchecked((int)source);
            return GetStatus.OutOfRange;
        }
        double truncated = Math.Truncate(source);
        value = (int)truncated;
        return truncated == source ? GetStatus.Success : GetStatus.Truncated;
    }

    private static GetStatus CoerceDoubleToUInt32(double source, out uint value) {
        if (!double.IsFinite(source)) {
            value = unchecked((uint)source);
            return GetStatus.OutOfRange;
        }
        if (source < 0 || source > uint.MaxValue) {
            value = unchecked((uint)source);
            return GetStatus.OutOfRange;
        }
        double truncated = Math.Truncate(source);
        value = (uint)truncated;
        return truncated == source ? GetStatus.Success : GetStatus.Truncated;
    }

    private static GetStatus CoerceDoubleToInt16(double source, out short value) {
        if (!double.IsFinite(source)) {
            value = unchecked((short)source);
            return GetStatus.OutOfRange;
        }
        if (source < short.MinValue || source > short.MaxValue) {
            value = unchecked((short)source);
            return GetStatus.OutOfRange;
        }
        double truncated = Math.Truncate(source);
        value = (short)truncated;
        return truncated == source ? GetStatus.Success : GetStatus.Truncated;
    }

    private static GetStatus CoerceDoubleToUInt16(double source, out ushort value) {
        if (!double.IsFinite(source)) {
            value = unchecked((ushort)source);
            return GetStatus.OutOfRange;
        }
        if (source < 0 || source > ushort.MaxValue) {
            value = unchecked((ushort)source);
            return GetStatus.OutOfRange;
        }
        double truncated = Math.Truncate(source);
        value = (ushort)truncated;
        return truncated == source ? GetStatus.Success : GetStatus.Truncated;
    }

    private static GetStatus CoerceDoubleToSByte(double source, out sbyte value) {
        if (!double.IsFinite(source)) {
            value = unchecked((sbyte)source);
            return GetStatus.OutOfRange;
        }
        if (source < sbyte.MinValue || source > sbyte.MaxValue) {
            value = unchecked((sbyte)source);
            return GetStatus.OutOfRange;
        }
        double truncated = Math.Truncate(source);
        value = (sbyte)truncated;
        return truncated == source ? GetStatus.Success : GetStatus.Truncated;
    }

    private static GetStatus CoerceDoubleToByte(double source, out byte value) {
        if (!double.IsFinite(source)) {
            value = unchecked((byte)source);
            return GetStatus.OutOfRange;
        }
        if (source < 0 || source > byte.MaxValue) {
            value = unchecked((byte)source);
            return GetStatus.OutOfRange;
        }
        double truncated = Math.Truncate(source);
        value = (byte)truncated;
        return truncated == source ? GetStatus.Success : GetStatus.Truncated;
    }
}
