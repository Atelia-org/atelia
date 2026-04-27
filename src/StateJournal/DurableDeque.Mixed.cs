using System.Diagnostics;
using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal;

/// <summary>替代 <see cref="DurableDeque{ValueBox}"/> 的异构双端队列。</summary>
[UseMixedValueCatalog(typeof(MixedValueCatalog), MixedContainers.Deque)]
public abstract partial class DurableDeque : DurableDequeBase, IDeque,
    IDeque<bool>, IDeque<Symbol>, IDeque<string>, IDeque<ByteString>, IDeque<DurableObject>,
    IDeque<double>, IDeque<float>, IDeque<Half>,
    IDeque<ulong>, IDeque<uint>, IDeque<ushort>, IDeque<byte>,
    IDeque<long>, IDeque<int>, IDeque<short>, IDeque<sbyte> {

    private protected DequeChangeTracker<ValueBox> _core;

    private protected override ReadOnlySpan<byte> TypeCode => HelperRegistry.MixedDeque.TypeCode;

    internal DurableDeque() {
    }

    #region DurableObject

    public override DurableObjectKind Kind => DurableObjectKind.MixedDeque;

    #endregion

    #region DurableDequeBase abstract properties

    public override bool HasChanges => _core.HasChanges;
    private protected override uint EstimatedRebaseBytes => _core.EstimatedRebaseBytes<ValueBoxHelper>();
    private protected override uint EstimatedDeltifyBytes => _core.EstimatedDeltifyBytes<ValueBoxHelper>();

    #endregion

    #region Core Hooks

    private protected virtual void OnCurrentValueRemoved(ValueBox removedValue) { }
    private protected virtual void OnCurrentValueUpserted(ValueBox oldValue, ValueBox newValue, bool existed) { }

    #endregion

    public int Count => _core.Current.Count;

    #region Generated Dispatch (partial — bodies in .g.cs)

    public partial void PushFront<TValue>(TValue? value) where TValue : notnull;
    public partial void PushBack<TValue>(TValue? value) where TValue : notnull;
    public partial bool TrySetAt<TValue>(int index, TValue? value) where TValue : notnull;
    public partial bool TrySetFront<TValue>(TValue? value) where TValue : notnull;
    public partial bool TrySetBack<TValue>(TValue? value) where TValue : notnull;
    public partial IDeque<TValue> Of<TValue>() where TValue : notnull;
    private partial GetIssue PeekCore<TValue>(bool front, out TValue? value) where TValue : notnull;
    private partial GetIssue GetCore<TValue>(int index, out TValue? value) where TValue : notnull;

    #endregion

    #region Generic Accessor

    /// <summary>
    /// 读取头部元素为指定类型。
    /// 对于 exact typed view 直接走接口；对于 <see cref="DurableObject"/> 子类型，先按基类读取再做运行时 cast。
    /// </summary>
    public GetIssue PeekFront<TValue>(out TValue? value) where TValue : notnull => PeekCore<TValue>(front: true, out value);
    /// <summary>读取尾部元素为指定类型。语义同 <see cref="PeekFront{TValue}(out TValue?)"/>。</summary>
    public GetIssue PeekBack<TValue>(out TValue? value) where TValue : notnull => PeekCore<TValue>(front: false, out value);

    /// <summary>
    /// 按索引读取指定类型的元素。
    /// 对于 <see cref="DurableObject"/> 子类型，适合作为 mixed deque 的低摩擦读取入口。
    /// </summary>
    public GetIssue GetAt<TValue>(int index, out TValue? value) where TValue : notnull => GetCore<TValue>(index, out value);
    /// <summary>读取并移除头部元素为指定类型。</summary>
    public GetIssue PopFront<TValue>(out TValue? value) where TValue : notnull => PopCore<TValue>(front: true, out value);
    /// <summary>读取并移除尾部元素为指定类型。</summary>
    public GetIssue PopBack<TValue>(out TValue? value) where TValue : notnull => PopCore<TValue>(front: false, out value);

    /// <summary>try 风格的按索引读取。</summary>
    public bool TryGetAt<TValue>(int index, out TValue? value) where TValue : notnull => GetAt<TValue>(index, out value) == GetIssue.None;
    /// <summary>try 风格的头部读取。</summary>
    public bool TryPeekFront<TValue>(out TValue? value) where TValue : notnull => PeekFront<TValue>(out value) == GetIssue.None;
    /// <summary>try 风格的尾部读取。</summary>
    public bool TryPeekBack<TValue>(out TValue? value) where TValue : notnull => PeekBack<TValue>(out value) == GetIssue.None;
    /// <summary>try 风格的头部弹出。</summary>
    public bool TryPopFront<TValue>(out TValue? value) where TValue : notnull => PopFront<TValue>(out value) == GetIssue.None;
    /// <summary>try 风格的尾部弹出。</summary>
    public bool TryPopBack<TValue>(out TValue? value) where TValue : notnull => PopBack<TValue>(out value) == GetIssue.None;

    public bool TryPeekFrontValueKind(out ValueKind kind) {
        if (!_core.TryPeekFront(out var front)) {
            kind = default;
            return false;
        }
        kind = front.GetValueKind();
        return true;
    }

    public bool TryPeekBackValueKind(out ValueKind kind) {
        if (!_core.TryPeekBack(out var back)) {
            kind = default;
            return false;
        }
        kind = back.GetValueKind();
        return true;
    }

    #endregion

    #region Private Impl

    private GetIssue PeekCore<TValue, VFace>(bool front, out TValue? value)
        where TValue : notnull
        where VFace : ValueBox.ITypedFace<TValue> {
        bool found = front
            ? _core.TryPeekFront(out ValueBox box)
            : _core.TryPeekBack(out box);
        if (!found) {
            value = default;
            return GetIssue.NotFound;
        }
        return VFace.Get(box, out value);
    }

    private GetIssue GetCore<TValue, VFace>(int index, out TValue? value)
        where TValue : notnull
        where VFace : ValueBox.ITypedFace<TValue> {
        if (!_core.TryGetAt(index, out ValueBox box)) {
            value = default;
            return GetIssue.OutOfRange;
        }
        return VFace.Get(box, out value);
    }

    private bool TrySetCore<TValue, VFace>(int index, TValue? value)
        where TValue : notnull
        where VFace : ValueBox.ITypedFace<TValue> {
        if ((uint)index >= (uint)_core.Current.Count) { return false; }

        SetCore<TValue, VFace>(index, value);
        return true;
    }

    private GetIssue PopCore<TValue>(bool front, out TValue? value) where TValue : notnull {
        ThrowIfDetachedOrFrozen();
        var issue = PeekCore<TValue>(front, out value);
        if (issue != GetIssue.None) { return issue; }

        bool callerOwned;
        ValueBox removed;
        if (front) {
            if (!_core.TryPopFront<ValueBoxHelper>(out removed, out callerOwned)) { throw new InvalidOperationException("Deque state changed unexpectedly between peek and pop."); }
        }
        else {
            if (!_core.TryPopBack<ValueBoxHelper>(out removed, out callerOwned)) { throw new InvalidOperationException("Deque state changed unexpectedly between peek and pop."); }
        }
        OnCurrentValueRemoved(removed);
        if (ValueBoxHelper.NeedRelease && callerOwned) {
            ValueBoxHelper.ReleaseSlot(removed);
        }
        return GetIssue.None;
    }

    private void PushCore<TValue, VFace>(bool front, TValue? value)
        where TValue : notnull
        where VFace : ValueBox.ITypedFace<TValue> {
        ThrowIfDetachedOrFrozen();
        ValueBox newValue = VFace.From(value);
        Debug.Assert(!newValue.IsUninitialized);
        OnCurrentValueUpserted(default, newValue, existed: false);
        if (front) {
            _core.PushFront<ValueBoxHelper>(newValue);
        }
        else {
            _core.PushBack<ValueBoxHelper>(newValue);
        }
    }

    private bool TrySetCore<TValue, VFace>(bool front, TValue? value)
        where TValue : notnull
        where VFace : ValueBox.ITypedFace<TValue> {
        ThrowIfDetachedOrFrozen();
        if (_core.Current.Count == 0) { return false; }

        int index = front ? 0 : _core.Current.Count - 1;
        return TrySetCore<TValue, VFace>(index, value);
    }

    private void SetCore<TValue, VFace>(int index, TValue? value)
        where TValue : notnull
        where VFace : ValueBox.ITypedFace<TValue> {
        ThrowIfDetachedOrFrozen();
        ref ValueBox slot = ref _core.GetRef(index);
        ValueBox oldValue = slot;
        if (!VFace.UpdateOrInit(ref slot, value, out uint oldBareBytes)) { return; }

        OnCurrentValueUpserted(oldValue, slot, existed: true);
        _core.AfterSet<ValueBoxHelper>(index, ref slot, oldBareBytes);
    }

    // ── CMS Step E：trusted 零拷贝 push / set 路径（与上方常规版本镜像，仅 face 走 trusted overload） ──

    private void PushCoreTrusted<TValue, VFace>(bool front, TValue value)
        where TValue : notnull
        where VFace : ValueBox.ITrustedTypedFace<TValue> {
        ThrowIfDetachedOrFrozen();
        ValueBox newValue = VFace.FromTrusted(value);
        Debug.Assert(!newValue.IsUninitialized);
        OnCurrentValueUpserted(default, newValue, existed: false);
        if (front) {
            _core.PushFront<ValueBoxHelper>(newValue);
        }
        else {
            _core.PushBack<ValueBoxHelper>(newValue);
        }
    }

    private bool TrySetCoreTrusted<TValue, VFace>(bool front, TValue value)
        where TValue : notnull
        where VFace : ValueBox.ITrustedTypedFace<TValue> {
        ThrowIfDetachedOrFrozen();
        if (_core.Current.Count == 0) { return false; }

        int index = front ? 0 : _core.Current.Count - 1;
        return TrySetAtCoreTrusted<TValue, VFace>(index, value);
    }

    private bool TrySetAtCoreTrusted<TValue, VFace>(int index, TValue value)
        where TValue : notnull
        where VFace : ValueBox.ITrustedTypedFace<TValue> {
        if ((uint)index >= (uint)_core.Current.Count) { return false; }

        ThrowIfDetachedOrFrozen();
        ref ValueBox slot = ref _core.GetRef(index);
        ValueBox oldValue = slot;
        if (!VFace.UpdateOrInitTrusted(ref slot, value, out uint oldBareBytes)) { return true; }

        OnCurrentValueUpserted(oldValue, slot, existed: true);
        _core.AfterSet<ValueBoxHelper>(index, ref slot, oldBareBytes);
        return true;
    }

    #endregion

    #region Double Helpers

    public void PushFrontExactDouble(double value) => PushCore<double, ValueBox.ExactDoubleFace>(front: true, value);
    public void PushBackExactDouble(double value) => PushCore<double, ValueBox.ExactDoubleFace>(front: false, value);
    public bool TrySetFrontExactDouble(double value) => TrySetCore<double, ValueBox.ExactDoubleFace>(front: true, value);
    public bool TrySetBackExactDouble(double value) => TrySetCore<double, ValueBox.ExactDoubleFace>(front: false, value);
    #endregion

    #region DurableObject Helpers

    private DurableRef ToDurableRef(DurableObject? value) {
        ThrowIfDetachedOrFrozen();
        if (value is not null) { Revision.EnsureCanReference(value); }
        return value is not null ? new DurableRef(value.Kind, value.LocalId) : default;
    }

    private GetIssue GetDurableObjectAt(int index, out DurableObject? value) {
        value = null;

        var issue = GetCore<DurableRef, ValueBox.DurableRefFace>(index, out var durRef);
        if (issue != GetIssue.None) { return issue; }
        if (durRef.IsNull) { return GetIssue.None; }

        AteliaResult<DurableObject> loadResult = Revision.Load(durRef.Id);
        if (loadResult.IsFailure) { return GetIssue.LoadFailed; }
        DurableObject? loaded = loadResult.Value;
        if (loaded is null || loaded.Kind != durRef.Kind) { return GetIssue.LoadFailed; }

        value = loaded;
        return GetIssue.None;
    }

    private GetIssue PeekDurableObject(bool front, out DurableObject? value) {
        value = null;

        var issue = PeekCore<DurableRef, ValueBox.DurableRefFace>(front, out var durRef);
        if (issue != GetIssue.None) { return issue; }
        if (durRef.IsNull) { return GetIssue.None; }

        AteliaResult<DurableObject> loadResult = Revision.Load(durRef.Id);
        if (loadResult.IsFailure) { return GetIssue.LoadFailed; }
        DurableObject? loaded = loadResult.Value;
        if (loaded is null || loaded.Kind != durRef.Kind) { return GetIssue.LoadFailed; }

        value = loaded;
        return GetIssue.None;
    }

    #endregion

    #region Symbol Helpers

    private GetIssue GetSymbolAt(int index, out Symbol value) {
        var issue = GetCore<SymbolId, ValueBox.SymbolIdFace>(index, out var symbolId);
        if (issue != GetIssue.None) {
            value = default;
            return issue;
        }
        if (symbolId.IsNull) {
            // slot 存的是 ValueBox.Null 或非 Symbol 类型；Symbol 本身契约非 null，不应被误读为 Symbol.Empty。
            value = default;
            return GetIssue.TypeMismatch;
        }
        if (!Revision.TryGetSymbol(symbolId, out string? str)) {
            value = default;
            return GetIssue.LoadFailed;
        }
        value = new Symbol(str!);
        return GetIssue.None;
    }

    private GetIssue PeekSymbol(bool front, out Symbol value) {
        var issue = PeekCore<SymbolId, ValueBox.SymbolIdFace>(front, out var symbolId);
        if (issue != GetIssue.None) {
            value = default;
            return issue;
        }
        if (symbolId.IsNull) {
            // slot 存的是 ValueBox.Null 或非 Symbol 类型；Symbol 本身契约非 null，不应被误读为 Symbol.Empty。
            value = default;
            return GetIssue.TypeMismatch;
        }
        if (!Revision.TryGetSymbol(symbolId, out string? str)) {
            value = default;
            return GetIssue.LoadFailed;
        }
        value = new Symbol(str!);
        return GetIssue.None;
    }

    private void PushSymbol(bool front, Symbol value) {
        ThrowIfDetachedOrFrozen();
        SymbolId id = Revision.InternSymbol(value.Value);
        PushCore<SymbolId, ValueBox.SymbolIdFace>(front, id);
    }

    private bool TrySetSymbol(int index, Symbol value) {
        ThrowIfDetachedOrFrozen();
        SymbolId id = Revision.InternSymbol(value.Value);
        return TrySetCore<SymbolId, ValueBox.SymbolIdFace>(index, id);
    }

    private bool TrySetSymbol(bool front, Symbol value) {
        ThrowIfDetachedOrFrozen();
        SymbolId id = Revision.InternSymbol(value.Value);
        return TrySetCore<SymbolId, ValueBox.SymbolIdFace>(front, id);
    }

    #endregion
}
