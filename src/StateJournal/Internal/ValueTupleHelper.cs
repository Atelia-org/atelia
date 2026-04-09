using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

internal readonly struct ValueTuple2Helper<T1, T2, H1, H2> : ITypeHelper<ValueTuple<T1, T2>>
    where T1 : notnull
    where T2 : notnull
    where H1 : unmanaged, ITypeHelper<T1>
    where H2 : unmanaged, ITypeHelper<T2> {
    public static bool Equals(ValueTuple<T1, T2> a, ValueTuple<T1, T2> b) =>
        H1.Equals(a.Item1, b.Item1) && H2.Equals(a.Item2, b.Item2);

    public static int Compare(ValueTuple<T1, T2> a, ValueTuple<T1, T2> b) {
        int c = H1.Compare(a.Item1, b.Item1);
        return c != 0 ? c : H2.Compare(a.Item2, b.Item2);
    }

    public static ValueTuple<T1, T2> Freeze(ValueTuple<T1, T2> value) =>
        new(H1.Freeze(value.Item1)!, H2.Freeze(value.Item2)!);

    public static bool NeedRelease => H1.NeedRelease || H2.NeedRelease;

    public static void ReleaseSlot(ValueTuple<T1, T2> value) {
        if (H1.NeedRelease) { H1.ReleaseSlot(value.Item1); }
        if (H2.NeedRelease) { H2.ReleaseSlot(value.Item2); }
    }

    public static void Write(BinaryDiffWriter writer, ValueTuple<T1, T2> value, bool asKey) {
        H1.Write(writer, value.Item1, asKey);
        H2.Write(writer, value.Item2, asKey);
    }

    public static ValueTuple<T1, T2> Read(ref BinaryDiffReader reader, bool asKey) =>
        new(H1.Read(ref reader, asKey)!, H2.Read(ref reader, asKey)!);

    public static bool NeedVisitChildRefs => H1.NeedVisitChildRefs || H2.NeedVisitChildRefs;

    public static void VisitChildRefs<TVisitor>(ValueTuple<T1, T2> value, Revision revision, ref TVisitor visitor)
        where TVisitor : IChildRefVisitor, allows ref struct {
        if (H1.NeedVisitChildRefs) { H1.VisitChildRefs(value.Item1, revision, ref visitor); }
        if (H2.NeedVisitChildRefs) { H2.VisitChildRefs(value.Item2, revision, ref visitor); }
    }

    public static bool NeedValidateReconstructed => H1.NeedValidateReconstructed || H2.NeedValidateReconstructed;

    public static AteliaError? ValidateReconstructed(ValueTuple<T1, T2> value, LoadPlaceholderTracker tracker, string ownerName) {
        if (H1.NeedValidateReconstructed && H1.ValidateReconstructed(value.Item1, tracker, ownerName) is { } firstError) { return firstError; }

        if (H2.NeedValidateReconstructed && H2.ValidateReconstructed(value.Item2, tracker, ownerName) is { } secondError) { return secondError; }

        return null;
    }

    public static void UpdateOrInit(ref BinaryDiffReader reader, ref ValueTuple<T1, T2> old) {
        T1? item1 = old.Item1;
        T2? item2 = old.Item2;
        H1.UpdateOrInit(ref reader, ref item1);
        H2.UpdateOrInit(ref reader, ref item2);
        old = new(item1!, item2!);
    }
}

internal readonly struct ValueTuple3Helper<T1, T2, T3, H1, H2, H3> : ITypeHelper<ValueTuple<T1, T2, T3>>
    where T1 : notnull
    where T2 : notnull
    where T3 : notnull
    where H1 : unmanaged, ITypeHelper<T1>
    where H2 : unmanaged, ITypeHelper<T2>
    where H3 : unmanaged, ITypeHelper<T3> {
    public static bool Equals(ValueTuple<T1, T2, T3> a, ValueTuple<T1, T2, T3> b) =>
        H1.Equals(a.Item1, b.Item1) && H2.Equals(a.Item2, b.Item2) && H3.Equals(a.Item3, b.Item3);

    public static int Compare(ValueTuple<T1, T2, T3> a, ValueTuple<T1, T2, T3> b) {
        int c = H1.Compare(a.Item1, b.Item1);
        if (c != 0) { return c; }
        c = H2.Compare(a.Item2, b.Item2);
        return c != 0 ? c : H3.Compare(a.Item3, b.Item3);
    }

    public static ValueTuple<T1, T2, T3> Freeze(ValueTuple<T1, T2, T3> value) =>
        new(H1.Freeze(value.Item1)!, H2.Freeze(value.Item2)!, H3.Freeze(value.Item3)!);

    public static bool NeedRelease => H1.NeedRelease || H2.NeedRelease || H3.NeedRelease;

    public static void ReleaseSlot(ValueTuple<T1, T2, T3> value) {
        if (H1.NeedRelease) { H1.ReleaseSlot(value.Item1); }
        if (H2.NeedRelease) { H2.ReleaseSlot(value.Item2); }
        if (H3.NeedRelease) { H3.ReleaseSlot(value.Item3); }
    }

    public static void Write(BinaryDiffWriter writer, ValueTuple<T1, T2, T3> value, bool asKey) {
        H1.Write(writer, value.Item1, asKey);
        H2.Write(writer, value.Item2, asKey);
        H3.Write(writer, value.Item3, asKey);
    }

    public static ValueTuple<T1, T2, T3> Read(ref BinaryDiffReader reader, bool asKey) =>
        new(H1.Read(ref reader, asKey)!, H2.Read(ref reader, asKey)!, H3.Read(ref reader, asKey)!);

    public static bool NeedVisitChildRefs => H1.NeedVisitChildRefs || H2.NeedVisitChildRefs || H3.NeedVisitChildRefs;

    public static void VisitChildRefs<TVisitor>(ValueTuple<T1, T2, T3> value, Revision revision, ref TVisitor visitor)
        where TVisitor : IChildRefVisitor, allows ref struct {
        if (H1.NeedVisitChildRefs) { H1.VisitChildRefs(value.Item1, revision, ref visitor); }
        if (H2.NeedVisitChildRefs) { H2.VisitChildRefs(value.Item2, revision, ref visitor); }
        if (H3.NeedVisitChildRefs) { H3.VisitChildRefs(value.Item3, revision, ref visitor); }
    }

    public static bool NeedValidateReconstructed =>
        H1.NeedValidateReconstructed || H2.NeedValidateReconstructed || H3.NeedValidateReconstructed;

    public static AteliaError? ValidateReconstructed(ValueTuple<T1, T2, T3> value, LoadPlaceholderTracker tracker, string ownerName) {
        if (H1.NeedValidateReconstructed && H1.ValidateReconstructed(value.Item1, tracker, ownerName) is { } firstError) { return firstError; }

        if (H2.NeedValidateReconstructed && H2.ValidateReconstructed(value.Item2, tracker, ownerName) is { } secondError) { return secondError; }

        if (H3.NeedValidateReconstructed && H3.ValidateReconstructed(value.Item3, tracker, ownerName) is { } thirdError) { return thirdError; }

        return null;
    }

    public static void UpdateOrInit(ref BinaryDiffReader reader, ref ValueTuple<T1, T2, T3> old) {
        T1? item1 = old.Item1;
        T2? item2 = old.Item2;
        T3? item3 = old.Item3;
        H1.UpdateOrInit(ref reader, ref item1);
        H2.UpdateOrInit(ref reader, ref item2);
        H3.UpdateOrInit(ref reader, ref item3);
        old = new(item1!, item2!, item3!);
    }
}

internal readonly struct ValueTuple4Helper<T1, T2, T3, T4, H1, H2, H3, H4> : ITypeHelper<ValueTuple<T1, T2, T3, T4>>
    where T1 : notnull
    where T2 : notnull
    where T3 : notnull
    where T4 : notnull
    where H1 : unmanaged, ITypeHelper<T1>
    where H2 : unmanaged, ITypeHelper<T2>
    where H3 : unmanaged, ITypeHelper<T3>
    where H4 : unmanaged, ITypeHelper<T4> {
    public static bool Equals(ValueTuple<T1, T2, T3, T4> a, ValueTuple<T1, T2, T3, T4> b) =>
        H1.Equals(a.Item1, b.Item1)
        && H2.Equals(a.Item2, b.Item2)
        && H3.Equals(a.Item3, b.Item3)
        && H4.Equals(a.Item4, b.Item4);

    public static int Compare(ValueTuple<T1, T2, T3, T4> a, ValueTuple<T1, T2, T3, T4> b) {
        int c = H1.Compare(a.Item1, b.Item1);
        if (c != 0) { return c; }
        c = H2.Compare(a.Item2, b.Item2);
        if (c != 0) { return c; }
        c = H3.Compare(a.Item3, b.Item3);
        return c != 0 ? c : H4.Compare(a.Item4, b.Item4);
    }

    public static ValueTuple<T1, T2, T3, T4> Freeze(ValueTuple<T1, T2, T3, T4> value) =>
        new(H1.Freeze(value.Item1)!, H2.Freeze(value.Item2)!, H3.Freeze(value.Item3)!, H4.Freeze(value.Item4)!);

    public static bool NeedRelease => H1.NeedRelease || H2.NeedRelease || H3.NeedRelease || H4.NeedRelease;

    public static void ReleaseSlot(ValueTuple<T1, T2, T3, T4> value) {
        if (H1.NeedRelease) { H1.ReleaseSlot(value.Item1); }
        if (H2.NeedRelease) { H2.ReleaseSlot(value.Item2); }
        if (H3.NeedRelease) { H3.ReleaseSlot(value.Item3); }
        if (H4.NeedRelease) { H4.ReleaseSlot(value.Item4); }
    }

    public static void Write(BinaryDiffWriter writer, ValueTuple<T1, T2, T3, T4> value, bool asKey) {
        H1.Write(writer, value.Item1, asKey);
        H2.Write(writer, value.Item2, asKey);
        H3.Write(writer, value.Item3, asKey);
        H4.Write(writer, value.Item4, asKey);
    }

    public static ValueTuple<T1, T2, T3, T4> Read(ref BinaryDiffReader reader, bool asKey) =>
        new(H1.Read(ref reader, asKey)!, H2.Read(ref reader, asKey)!, H3.Read(ref reader, asKey)!, H4.Read(ref reader, asKey)!);

    public static bool NeedVisitChildRefs =>
        H1.NeedVisitChildRefs || H2.NeedVisitChildRefs || H3.NeedVisitChildRefs || H4.NeedVisitChildRefs;

    public static void VisitChildRefs<TVisitor>(ValueTuple<T1, T2, T3, T4> value, Revision revision, ref TVisitor visitor)
        where TVisitor : IChildRefVisitor, allows ref struct {
        if (H1.NeedVisitChildRefs) { H1.VisitChildRefs(value.Item1, revision, ref visitor); }
        if (H2.NeedVisitChildRefs) { H2.VisitChildRefs(value.Item2, revision, ref visitor); }
        if (H3.NeedVisitChildRefs) { H3.VisitChildRefs(value.Item3, revision, ref visitor); }
        if (H4.NeedVisitChildRefs) { H4.VisitChildRefs(value.Item4, revision, ref visitor); }
    }

    public static bool NeedValidateReconstructed =>
        H1.NeedValidateReconstructed || H2.NeedValidateReconstructed || H3.NeedValidateReconstructed || H4.NeedValidateReconstructed;

    public static AteliaError? ValidateReconstructed(ValueTuple<T1, T2, T3, T4> value, LoadPlaceholderTracker tracker, string ownerName) {
        if (H1.NeedValidateReconstructed && H1.ValidateReconstructed(value.Item1, tracker, ownerName) is { } firstError) { return firstError; }

        if (H2.NeedValidateReconstructed && H2.ValidateReconstructed(value.Item2, tracker, ownerName) is { } secondError) { return secondError; }

        if (H3.NeedValidateReconstructed && H3.ValidateReconstructed(value.Item3, tracker, ownerName) is { } thirdError) { return thirdError; }

        if (H4.NeedValidateReconstructed && H4.ValidateReconstructed(value.Item4, tracker, ownerName) is { } fourthError) { return fourthError; }

        return null;
    }

    public static void UpdateOrInit(ref BinaryDiffReader reader, ref ValueTuple<T1, T2, T3, T4> old) {
        T1? item1 = old.Item1;
        T2? item2 = old.Item2;
        T3? item3 = old.Item3;
        T4? item4 = old.Item4;
        H1.UpdateOrInit(ref reader, ref item1);
        H2.UpdateOrInit(ref reader, ref item2);
        H3.UpdateOrInit(ref reader, ref item3);
        H4.UpdateOrInit(ref reader, ref item4);
        old = new(item1!, item2!, item3!, item4!);
    }
}

internal readonly struct ValueTuple5Helper<T1, T2, T3, T4, T5, H1, H2, H3, H4, H5> : ITypeHelper<ValueTuple<T1, T2, T3, T4, T5>>
    where T1 : notnull
    where T2 : notnull
    where T3 : notnull
    where T4 : notnull
    where T5 : notnull
    where H1 : unmanaged, ITypeHelper<T1>
    where H2 : unmanaged, ITypeHelper<T2>
    where H3 : unmanaged, ITypeHelper<T3>
    where H4 : unmanaged, ITypeHelper<T4>
    where H5 : unmanaged, ITypeHelper<T5> {
    public static bool Equals(ValueTuple<T1, T2, T3, T4, T5> a, ValueTuple<T1, T2, T3, T4, T5> b) =>
        H1.Equals(a.Item1, b.Item1)
        && H2.Equals(a.Item2, b.Item2)
        && H3.Equals(a.Item3, b.Item3)
        && H4.Equals(a.Item4, b.Item4)
        && H5.Equals(a.Item5, b.Item5);

    public static int Compare(ValueTuple<T1, T2, T3, T4, T5> a, ValueTuple<T1, T2, T3, T4, T5> b) {
        int c = H1.Compare(a.Item1, b.Item1);
        if (c != 0) { return c; }
        c = H2.Compare(a.Item2, b.Item2);
        if (c != 0) { return c; }
        c = H3.Compare(a.Item3, b.Item3);
        if (c != 0) { return c; }
        c = H4.Compare(a.Item4, b.Item4);
        return c != 0 ? c : H5.Compare(a.Item5, b.Item5);
    }

    public static ValueTuple<T1, T2, T3, T4, T5> Freeze(ValueTuple<T1, T2, T3, T4, T5> value) =>
        new(H1.Freeze(value.Item1)!, H2.Freeze(value.Item2)!, H3.Freeze(value.Item3)!, H4.Freeze(value.Item4)!, H5.Freeze(value.Item5)!);

    public static bool NeedRelease => H1.NeedRelease || H2.NeedRelease || H3.NeedRelease || H4.NeedRelease || H5.NeedRelease;

    public static void ReleaseSlot(ValueTuple<T1, T2, T3, T4, T5> value) {
        if (H1.NeedRelease) { H1.ReleaseSlot(value.Item1); }
        if (H2.NeedRelease) { H2.ReleaseSlot(value.Item2); }
        if (H3.NeedRelease) { H3.ReleaseSlot(value.Item3); }
        if (H4.NeedRelease) { H4.ReleaseSlot(value.Item4); }
        if (H5.NeedRelease) { H5.ReleaseSlot(value.Item5); }
    }

    public static void Write(BinaryDiffWriter writer, ValueTuple<T1, T2, T3, T4, T5> value, bool asKey) {
        H1.Write(writer, value.Item1, asKey);
        H2.Write(writer, value.Item2, asKey);
        H3.Write(writer, value.Item3, asKey);
        H4.Write(writer, value.Item4, asKey);
        H5.Write(writer, value.Item5, asKey);
    }

    public static ValueTuple<T1, T2, T3, T4, T5> Read(ref BinaryDiffReader reader, bool asKey) =>
        new(H1.Read(ref reader, asKey)!, H2.Read(ref reader, asKey)!, H3.Read(ref reader, asKey)!, H4.Read(ref reader, asKey)!, H5.Read(ref reader, asKey)!);

    public static bool NeedVisitChildRefs =>
        H1.NeedVisitChildRefs || H2.NeedVisitChildRefs || H3.NeedVisitChildRefs || H4.NeedVisitChildRefs || H5.NeedVisitChildRefs;

    public static void VisitChildRefs<TVisitor>(ValueTuple<T1, T2, T3, T4, T5> value, Revision revision, ref TVisitor visitor)
        where TVisitor : IChildRefVisitor, allows ref struct {
        if (H1.NeedVisitChildRefs) { H1.VisitChildRefs(value.Item1, revision, ref visitor); }
        if (H2.NeedVisitChildRefs) { H2.VisitChildRefs(value.Item2, revision, ref visitor); }
        if (H3.NeedVisitChildRefs) { H3.VisitChildRefs(value.Item3, revision, ref visitor); }
        if (H4.NeedVisitChildRefs) { H4.VisitChildRefs(value.Item4, revision, ref visitor); }
        if (H5.NeedVisitChildRefs) { H5.VisitChildRefs(value.Item5, revision, ref visitor); }
    }

    public static bool NeedValidateReconstructed =>
        H1.NeedValidateReconstructed || H2.NeedValidateReconstructed || H3.NeedValidateReconstructed || H4.NeedValidateReconstructed || H5.NeedValidateReconstructed;

    public static AteliaError? ValidateReconstructed(ValueTuple<T1, T2, T3, T4, T5> value, LoadPlaceholderTracker tracker, string ownerName) {
        if (H1.NeedValidateReconstructed && H1.ValidateReconstructed(value.Item1, tracker, ownerName) is { } firstError) { return firstError; }

        if (H2.NeedValidateReconstructed && H2.ValidateReconstructed(value.Item2, tracker, ownerName) is { } secondError) { return secondError; }

        if (H3.NeedValidateReconstructed && H3.ValidateReconstructed(value.Item3, tracker, ownerName) is { } thirdError) { return thirdError; }

        if (H4.NeedValidateReconstructed && H4.ValidateReconstructed(value.Item4, tracker, ownerName) is { } fourthError) { return fourthError; }

        if (H5.NeedValidateReconstructed && H5.ValidateReconstructed(value.Item5, tracker, ownerName) is { } fifthError) { return fifthError; }

        return null;
    }

    public static void UpdateOrInit(ref BinaryDiffReader reader, ref ValueTuple<T1, T2, T3, T4, T5> old) {
        T1? item1 = old.Item1;
        T2? item2 = old.Item2;
        T3? item3 = old.Item3;
        T4? item4 = old.Item4;
        T5? item5 = old.Item5;
        H1.UpdateOrInit(ref reader, ref item1);
        H2.UpdateOrInit(ref reader, ref item2);
        H3.UpdateOrInit(ref reader, ref item3);
        H4.UpdateOrInit(ref reader, ref item4);
        H5.UpdateOrInit(ref reader, ref item5);
        old = new(item1!, item2!, item3!, item4!, item5!);
    }
}

internal readonly struct ValueTuple6Helper<T1, T2, T3, T4, T5, T6, H1, H2, H3, H4, H5, H6> : ITypeHelper<ValueTuple<T1, T2, T3, T4, T5, T6>>
    where T1 : notnull
    where T2 : notnull
    where T3 : notnull
    where T4 : notnull
    where T5 : notnull
    where T6 : notnull
    where H1 : unmanaged, ITypeHelper<T1>
    where H2 : unmanaged, ITypeHelper<T2>
    where H3 : unmanaged, ITypeHelper<T3>
    where H4 : unmanaged, ITypeHelper<T4>
    where H5 : unmanaged, ITypeHelper<T5>
    where H6 : unmanaged, ITypeHelper<T6> {
    public static bool Equals(ValueTuple<T1, T2, T3, T4, T5, T6> a, ValueTuple<T1, T2, T3, T4, T5, T6> b) =>
        H1.Equals(a.Item1, b.Item1)
        && H2.Equals(a.Item2, b.Item2)
        && H3.Equals(a.Item3, b.Item3)
        && H4.Equals(a.Item4, b.Item4)
        && H5.Equals(a.Item5, b.Item5)
        && H6.Equals(a.Item6, b.Item6);

    public static int Compare(ValueTuple<T1, T2, T3, T4, T5, T6> a, ValueTuple<T1, T2, T3, T4, T5, T6> b) {
        int c = H1.Compare(a.Item1, b.Item1);
        if (c != 0) { return c; }
        c = H2.Compare(a.Item2, b.Item2);
        if (c != 0) { return c; }
        c = H3.Compare(a.Item3, b.Item3);
        if (c != 0) { return c; }
        c = H4.Compare(a.Item4, b.Item4);
        if (c != 0) { return c; }
        c = H5.Compare(a.Item5, b.Item5);
        return c != 0 ? c : H6.Compare(a.Item6, b.Item6);
    }

    public static ValueTuple<T1, T2, T3, T4, T5, T6> Freeze(ValueTuple<T1, T2, T3, T4, T5, T6> value) =>
        new(H1.Freeze(value.Item1)!, H2.Freeze(value.Item2)!, H3.Freeze(value.Item3)!, H4.Freeze(value.Item4)!, H5.Freeze(value.Item5)!, H6.Freeze(value.Item6)!);

    public static bool NeedRelease => H1.NeedRelease || H2.NeedRelease || H3.NeedRelease || H4.NeedRelease || H5.NeedRelease || H6.NeedRelease;

    public static void ReleaseSlot(ValueTuple<T1, T2, T3, T4, T5, T6> value) {
        if (H1.NeedRelease) { H1.ReleaseSlot(value.Item1); }
        if (H2.NeedRelease) { H2.ReleaseSlot(value.Item2); }
        if (H3.NeedRelease) { H3.ReleaseSlot(value.Item3); }
        if (H4.NeedRelease) { H4.ReleaseSlot(value.Item4); }
        if (H5.NeedRelease) { H5.ReleaseSlot(value.Item5); }
        if (H6.NeedRelease) { H6.ReleaseSlot(value.Item6); }
    }

    public static void Write(BinaryDiffWriter writer, ValueTuple<T1, T2, T3, T4, T5, T6> value, bool asKey) {
        H1.Write(writer, value.Item1, asKey);
        H2.Write(writer, value.Item2, asKey);
        H3.Write(writer, value.Item3, asKey);
        H4.Write(writer, value.Item4, asKey);
        H5.Write(writer, value.Item5, asKey);
        H6.Write(writer, value.Item6, asKey);
    }

    public static ValueTuple<T1, T2, T3, T4, T5, T6> Read(ref BinaryDiffReader reader, bool asKey) =>
        new(H1.Read(ref reader, asKey)!, H2.Read(ref reader, asKey)!, H3.Read(ref reader, asKey)!, H4.Read(ref reader, asKey)!, H5.Read(ref reader, asKey)!, H6.Read(ref reader, asKey)!);

    public static bool NeedVisitChildRefs =>
        H1.NeedVisitChildRefs || H2.NeedVisitChildRefs || H3.NeedVisitChildRefs || H4.NeedVisitChildRefs || H5.NeedVisitChildRefs || H6.NeedVisitChildRefs;

    public static void VisitChildRefs<TVisitor>(ValueTuple<T1, T2, T3, T4, T5, T6> value, Revision revision, ref TVisitor visitor)
        where TVisitor : IChildRefVisitor, allows ref struct {
        if (H1.NeedVisitChildRefs) { H1.VisitChildRefs(value.Item1, revision, ref visitor); }
        if (H2.NeedVisitChildRefs) { H2.VisitChildRefs(value.Item2, revision, ref visitor); }
        if (H3.NeedVisitChildRefs) { H3.VisitChildRefs(value.Item3, revision, ref visitor); }
        if (H4.NeedVisitChildRefs) { H4.VisitChildRefs(value.Item4, revision, ref visitor); }
        if (H5.NeedVisitChildRefs) { H5.VisitChildRefs(value.Item5, revision, ref visitor); }
        if (H6.NeedVisitChildRefs) { H6.VisitChildRefs(value.Item6, revision, ref visitor); }
    }

    public static bool NeedValidateReconstructed =>
        H1.NeedValidateReconstructed || H2.NeedValidateReconstructed || H3.NeedValidateReconstructed || H4.NeedValidateReconstructed || H5.NeedValidateReconstructed || H6.NeedValidateReconstructed;

    public static AteliaError? ValidateReconstructed(ValueTuple<T1, T2, T3, T4, T5, T6> value, LoadPlaceholderTracker tracker, string ownerName) {
        if (H1.NeedValidateReconstructed && H1.ValidateReconstructed(value.Item1, tracker, ownerName) is { } firstError) { return firstError; }

        if (H2.NeedValidateReconstructed && H2.ValidateReconstructed(value.Item2, tracker, ownerName) is { } secondError) { return secondError; }

        if (H3.NeedValidateReconstructed && H3.ValidateReconstructed(value.Item3, tracker, ownerName) is { } thirdError) { return thirdError; }

        if (H4.NeedValidateReconstructed && H4.ValidateReconstructed(value.Item4, tracker, ownerName) is { } fourthError) { return fourthError; }

        if (H5.NeedValidateReconstructed && H5.ValidateReconstructed(value.Item5, tracker, ownerName) is { } fifthError) { return fifthError; }

        if (H6.NeedValidateReconstructed && H6.ValidateReconstructed(value.Item6, tracker, ownerName) is { } sixthError) { return sixthError; }

        return null;
    }

    public static void UpdateOrInit(ref BinaryDiffReader reader, ref ValueTuple<T1, T2, T3, T4, T5, T6> old) {
        T1? item1 = old.Item1;
        T2? item2 = old.Item2;
        T3? item3 = old.Item3;
        T4? item4 = old.Item4;
        T5? item5 = old.Item5;
        T6? item6 = old.Item6;
        H1.UpdateOrInit(ref reader, ref item1);
        H2.UpdateOrInit(ref reader, ref item2);
        H3.UpdateOrInit(ref reader, ref item3);
        H4.UpdateOrInit(ref reader, ref item4);
        H5.UpdateOrInit(ref reader, ref item5);
        H6.UpdateOrInit(ref reader, ref item6);
        old = new(item1!, item2!, item3!, item4!, item5!, item6!);
    }
}

internal readonly struct ValueTuple7Helper<T1, T2, T3, T4, T5, T6, T7, H1, H2, H3, H4, H5, H6, H7> : ITypeHelper<ValueTuple<T1, T2, T3, T4, T5, T6, T7>>
    where T1 : notnull
    where T2 : notnull
    where T3 : notnull
    where T4 : notnull
    where T5 : notnull
    where T6 : notnull
    where T7 : notnull
    where H1 : unmanaged, ITypeHelper<T1>
    where H2 : unmanaged, ITypeHelper<T2>
    where H3 : unmanaged, ITypeHelper<T3>
    where H4 : unmanaged, ITypeHelper<T4>
    where H5 : unmanaged, ITypeHelper<T5>
    where H6 : unmanaged, ITypeHelper<T6>
    where H7 : unmanaged, ITypeHelper<T7> {
    public static bool Equals(ValueTuple<T1, T2, T3, T4, T5, T6, T7> a, ValueTuple<T1, T2, T3, T4, T5, T6, T7> b) =>
        H1.Equals(a.Item1, b.Item1)
        && H2.Equals(a.Item2, b.Item2)
        && H3.Equals(a.Item3, b.Item3)
        && H4.Equals(a.Item4, b.Item4)
        && H5.Equals(a.Item5, b.Item5)
        && H6.Equals(a.Item6, b.Item6)
        && H7.Equals(a.Item7, b.Item7);

    public static int Compare(ValueTuple<T1, T2, T3, T4, T5, T6, T7> a, ValueTuple<T1, T2, T3, T4, T5, T6, T7> b) {
        int c = H1.Compare(a.Item1, b.Item1);
        if (c != 0) { return c; }
        c = H2.Compare(a.Item2, b.Item2);
        if (c != 0) { return c; }
        c = H3.Compare(a.Item3, b.Item3);
        if (c != 0) { return c; }
        c = H4.Compare(a.Item4, b.Item4);
        if (c != 0) { return c; }
        c = H5.Compare(a.Item5, b.Item5);
        if (c != 0) { return c; }
        c = H6.Compare(a.Item6, b.Item6);
        return c != 0 ? c : H7.Compare(a.Item7, b.Item7);
    }

    public static ValueTuple<T1, T2, T3, T4, T5, T6, T7> Freeze(ValueTuple<T1, T2, T3, T4, T5, T6, T7> value) =>
        new(H1.Freeze(value.Item1)!, H2.Freeze(value.Item2)!, H3.Freeze(value.Item3)!, H4.Freeze(value.Item4)!, H5.Freeze(value.Item5)!, H6.Freeze(value.Item6)!, H7.Freeze(value.Item7)!);

    public static bool NeedRelease =>
        H1.NeedRelease || H2.NeedRelease || H3.NeedRelease || H4.NeedRelease || H5.NeedRelease || H6.NeedRelease || H7.NeedRelease;

    public static void ReleaseSlot(ValueTuple<T1, T2, T3, T4, T5, T6, T7> value) {
        if (H1.NeedRelease) { H1.ReleaseSlot(value.Item1); }
        if (H2.NeedRelease) { H2.ReleaseSlot(value.Item2); }
        if (H3.NeedRelease) { H3.ReleaseSlot(value.Item3); }
        if (H4.NeedRelease) { H4.ReleaseSlot(value.Item4); }
        if (H5.NeedRelease) { H5.ReleaseSlot(value.Item5); }
        if (H6.NeedRelease) { H6.ReleaseSlot(value.Item6); }
        if (H7.NeedRelease) { H7.ReleaseSlot(value.Item7); }
    }

    public static void Write(BinaryDiffWriter writer, ValueTuple<T1, T2, T3, T4, T5, T6, T7> value, bool asKey) {
        H1.Write(writer, value.Item1, asKey);
        H2.Write(writer, value.Item2, asKey);
        H3.Write(writer, value.Item3, asKey);
        H4.Write(writer, value.Item4, asKey);
        H5.Write(writer, value.Item5, asKey);
        H6.Write(writer, value.Item6, asKey);
        H7.Write(writer, value.Item7, asKey);
    }

    public static ValueTuple<T1, T2, T3, T4, T5, T6, T7> Read(ref BinaryDiffReader reader, bool asKey) =>
        new(H1.Read(ref reader, asKey)!, H2.Read(ref reader, asKey)!, H3.Read(ref reader, asKey)!, H4.Read(ref reader, asKey)!, H5.Read(ref reader, asKey)!, H6.Read(ref reader, asKey)!, H7.Read(ref reader, asKey)!);

    public static bool NeedVisitChildRefs =>
        H1.NeedVisitChildRefs || H2.NeedVisitChildRefs || H3.NeedVisitChildRefs || H4.NeedVisitChildRefs || H5.NeedVisitChildRefs || H6.NeedVisitChildRefs || H7.NeedVisitChildRefs;

    public static void VisitChildRefs<TVisitor>(ValueTuple<T1, T2, T3, T4, T5, T6, T7> value, Revision revision, ref TVisitor visitor)
        where TVisitor : IChildRefVisitor, allows ref struct {
        if (H1.NeedVisitChildRefs) { H1.VisitChildRefs(value.Item1, revision, ref visitor); }
        if (H2.NeedVisitChildRefs) { H2.VisitChildRefs(value.Item2, revision, ref visitor); }
        if (H3.NeedVisitChildRefs) { H3.VisitChildRefs(value.Item3, revision, ref visitor); }
        if (H4.NeedVisitChildRefs) { H4.VisitChildRefs(value.Item4, revision, ref visitor); }
        if (H5.NeedVisitChildRefs) { H5.VisitChildRefs(value.Item5, revision, ref visitor); }
        if (H6.NeedVisitChildRefs) { H6.VisitChildRefs(value.Item6, revision, ref visitor); }
        if (H7.NeedVisitChildRefs) { H7.VisitChildRefs(value.Item7, revision, ref visitor); }
    }

    public static bool NeedValidateReconstructed =>
        H1.NeedValidateReconstructed || H2.NeedValidateReconstructed || H3.NeedValidateReconstructed || H4.NeedValidateReconstructed || H5.NeedValidateReconstructed || H6.NeedValidateReconstructed || H7.NeedValidateReconstructed;

    public static AteliaError? ValidateReconstructed(ValueTuple<T1, T2, T3, T4, T5, T6, T7> value, LoadPlaceholderTracker tracker, string ownerName) {
        if (H1.NeedValidateReconstructed && H1.ValidateReconstructed(value.Item1, tracker, ownerName) is { } firstError) { return firstError; }

        if (H2.NeedValidateReconstructed && H2.ValidateReconstructed(value.Item2, tracker, ownerName) is { } secondError) { return secondError; }

        if (H3.NeedValidateReconstructed && H3.ValidateReconstructed(value.Item3, tracker, ownerName) is { } thirdError) { return thirdError; }

        if (H4.NeedValidateReconstructed && H4.ValidateReconstructed(value.Item4, tracker, ownerName) is { } fourthError) { return fourthError; }

        if (H5.NeedValidateReconstructed && H5.ValidateReconstructed(value.Item5, tracker, ownerName) is { } fifthError) { return fifthError; }

        if (H6.NeedValidateReconstructed && H6.ValidateReconstructed(value.Item6, tracker, ownerName) is { } sixthError) { return sixthError; }

        if (H7.NeedValidateReconstructed && H7.ValidateReconstructed(value.Item7, tracker, ownerName) is { } seventhError) { return seventhError; }

        return null;
    }

    public static void UpdateOrInit(ref BinaryDiffReader reader, ref ValueTuple<T1, T2, T3, T4, T5, T6, T7> old) {
        T1? item1 = old.Item1;
        T2? item2 = old.Item2;
        T3? item3 = old.Item3;
        T4? item4 = old.Item4;
        T5? item5 = old.Item5;
        T6? item6 = old.Item6;
        T7? item7 = old.Item7;
        H1.UpdateOrInit(ref reader, ref item1);
        H2.UpdateOrInit(ref reader, ref item2);
        H3.UpdateOrInit(ref reader, ref item3);
        H4.UpdateOrInit(ref reader, ref item4);
        H5.UpdateOrInit(ref reader, ref item5);
        H6.UpdateOrInit(ref reader, ref item6);
        H7.UpdateOrInit(ref reader, ref item7);
        old = new(item1!, item2!, item3!, item4!, item5!, item6!, item7!);
    }
}
