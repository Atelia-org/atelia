using System.Diagnostics;

namespace Atelia.StateJournal;

/// <summary>双端队列的非泛型基础视图，仅暴露与元素类型无关的成员。</summary>
public interface IDeque {
    /// <summary>当前元素个数。</summary>
    int Count { get; }
}

/// <summary>
/// 类型化双端队列接口。
/// 读取采用 <see cref="GetIssue"/> + <c>out</c> 的统一约定，外层可自行组合出 try 风格或 throwing 风格。
/// </summary>
public interface IDeque<TValue> : IDeque where TValue : notnull {
    /// <summary>在头部插入一个元素。</summary>
    void PushFront(TValue? value);
    /// <summary>在尾部插入一个元素。</summary>
    void PushBack(TValue? value);

    /// <summary>按索引读取元素。越界时返回 <see cref="GetIssue.OutOfRange"/>。</summary>
    GetIssue GetAt(int index, out TValue? value);
    /// <summary>读取头部元素。空队列时返回 <see cref="GetIssue.NotFound"/>。</summary>
    GetIssue PeekFront(out TValue? value);
    /// <summary>读取尾部元素。空队列时返回 <see cref="GetIssue.NotFound"/>。</summary>
    GetIssue PeekBack(out TValue? value);

    /// <summary>尝试按索引覆盖元素。越界时返回 <see langword="false"/>。</summary>
    bool TrySetAt(int index, TValue? value);
    /// <summary>尝试覆盖头部元素。空队列时返回 <see langword="false"/>。</summary>
    bool TrySetFront(TValue? value);
    /// <summary>尝试覆盖尾部元素。空队列时返回 <see langword="false"/>。</summary>
    bool TrySetBack(TValue? value);

    /// <summary>读取并移除头部元素。空队列时返回 <see cref="GetIssue.NotFound"/>。</summary>
    GetIssue PopFront(out TValue? value);
    /// <summary>读取并移除尾部元素。空队列时返回 <see cref="GetIssue.NotFound"/>。</summary>
    GetIssue PopBack(out TValue? value);
}

/// <summary><see cref="IDeque{TValue}"/> 的通用扩展辅助方法。</summary>
public static class DequeExtensions {
    /// <summary>throwing 风格的按索引读取。</summary>
    public static TValue? GetAt<TValue>(this IDeque<TValue> deque, int index)
        where TValue : notnull
        => deque.GetAtOrThrow(index);

    /// <summary>try 风格的按索引读取。</summary>
    public static bool TryGetAt<TValue>(this IDeque<TValue> deque, int index, out TValue? value)
        where TValue : notnull
        => deque.GetAt(index, out value) == GetIssue.None;

    /// <summary>throwing 风格的按索引读取。</summary>
    public static TValue? GetAtOrThrow<TValue>(this IDeque<TValue> deque, int index)
        where TValue : notnull
        => DequeThrowHelpers.GetAtOrThrow(index, deque.GetAt(index, out TValue? value), value);

    /// <summary>throwing 风格的头部读取。</summary>
    public static TValue? GetFrontOrThrow<TValue>(this IDeque<TValue> deque)
        where TValue : notnull
        => DequeThrowHelpers.GetEdgeValueOrThrow("front", deque.PeekFront(out TValue? value), value);

    /// <summary>throwing 风格的尾部读取。</summary>
    public static TValue? GetBackOrThrow<TValue>(this IDeque<TValue> deque)
        where TValue : notnull
        => DequeThrowHelpers.GetEdgeValueOrThrow("back", deque.PeekBack(out TValue? value), value);

    /// <summary>throwing 风格的按索引写入。</summary>
    public static void SetAtOrThrow<TValue>(this IDeque<TValue> deque, int index, TValue? value)
        where TValue : notnull {
        if (!deque.TrySetAt(index, value)) { throw new ArgumentOutOfRangeException(nameof(index)); }
    }

    /// <summary>try 风格的头部读取。</summary>
    public static bool TryPeekFront<TValue>(this IDeque<TValue> deque, out TValue? value)
        where TValue : notnull
        => deque.PeekFront(out value) == GetIssue.None;

    /// <summary>try 风格的尾部读取。</summary>
    public static bool TryPeekBack<TValue>(this IDeque<TValue> deque, out TValue? value)
        where TValue : notnull
        => deque.PeekBack(out value) == GetIssue.None;

    /// <summary>try 风格的头部弹出。</summary>
    public static bool TryPopFront<TValue>(this IDeque<TValue> deque, out TValue? value)
        where TValue : notnull
        => deque.PopFront(out value) == GetIssue.None;

    /// <summary>try 风格的尾部弹出。</summary>
    public static bool TryPopBack<TValue>(this IDeque<TValue> deque, out TValue? value)
        where TValue : notnull
        => deque.PopBack(out value) == GetIssue.None;

    /// <summary>throwing 风格的头部写入。</summary>
    public static void SetFrontOrThrow<TValue>(this IDeque<TValue> deque, TValue? value)
        where TValue : notnull {
        if (!deque.TrySetFront(value)) { throw new InvalidOperationException("Deque is empty."); }
    }

    /// <summary>throwing 风格的尾部写入。</summary>
    public static void SetBackOrThrow<TValue>(this IDeque<TValue> deque, TValue? value)
        where TValue : notnull {
        if (!deque.TrySetBack(value)) { throw new InvalidOperationException("Deque is empty."); }
    }

    /// <summary>mixed deque 的 throwing 风格按索引读取。</summary>
    public static TValue? GetAt<TValue>(this DurableDeque deque, int index)
        where TValue : notnull
        => DequeThrowHelpers.GetAtOrThrow(index, deque.GetAt<TValue>(index, out TValue? value), value);

    /// <summary>mixed deque 的 throwing 风格头部读取。</summary>
    public static TValue? GetFront<TValue>(this DurableDeque deque)
        where TValue : notnull
        => DequeThrowHelpers.GetEdgeValueOrThrow("front", deque.PeekFront<TValue>(out TValue? value), value);

    /// <summary>mixed deque 的 throwing 风格尾部读取。</summary>
    public static TValue? GetBack<TValue>(this DurableDeque deque)
        where TValue : notnull
        => DequeThrowHelpers.GetEdgeValueOrThrow("back", deque.PeekBack<TValue>(out TValue? value), value);
}

internal static class DequeThrowHelpers {
    public static TValue? GetAtOrThrow<TValue>(int index, GetIssue issue, TValue? value)
        where TValue : notnull =>
        issue switch {
            GetIssue.None => value,
            GetIssue.PrecisionLost => throw new InvalidCastException(
                $"Value at index {index} cannot be cast to {typeof(TValue).Name} without losing precision."
            ),
            GetIssue.OverflowedToInfinity => throw new OverflowException(
                $"Value at index {index} overflows to infinity when cast to {typeof(TValue).Name}."
            ),
            GetIssue.Saturated => throw new OverflowException(
                $"Value at index {index} is out of bounds for {typeof(TValue).Name}."
            ),
            GetIssue.LoadFailed => throw new InvalidDataException(
                $"Value at index {index} references a DurableObject that cannot be loaded."
            ),
            GetIssue.UnsupportedType => throw new NotSupportedException(
                $"Type {typeof(TValue).Name} is not a supported value type for deque access."
            ),
            GetIssue.TypeMismatch => throw new InvalidCastException(
                $"Value at index {index} is not of type {typeof(TValue).Name}."
            ),
            GetIssue.OutOfRange => throw new ArgumentOutOfRangeException(nameof(index)),
            GetIssue.NotFound => throw new InvalidOperationException("Requested value is not available."),
            _ => throw new UnreachableException()
        };

    public static TValue? GetEdgeValueOrThrow<TValue>(string edgeName, GetIssue issue, TValue? value)
        where TValue : notnull =>
        issue switch {
            GetIssue.None => value,
            GetIssue.NotFound => throw new InvalidOperationException("Deque is empty."),
            GetIssue.PrecisionLost => throw new InvalidCastException(
                $"Value at deque {edgeName} cannot be cast to {typeof(TValue).Name} without losing precision."
            ),
            GetIssue.OverflowedToInfinity => throw new OverflowException(
                $"Value at deque {edgeName} overflows to infinity when cast to {typeof(TValue).Name}."
            ),
            GetIssue.Saturated => throw new OverflowException(
                $"Value at deque {edgeName} is out of bounds for {typeof(TValue).Name}."
            ),
            GetIssue.LoadFailed => throw new InvalidDataException(
                $"Value at deque {edgeName} references a DurableObject that cannot be loaded."
            ),
            GetIssue.UnsupportedType => throw new NotSupportedException(
                $"Type {typeof(TValue).Name} is not a supported value type for deque access."
            ),
            GetIssue.TypeMismatch => throw new InvalidCastException(
                $"Value at deque {edgeName} is not of type {typeof(TValue).Name}."
            ),
            GetIssue.OutOfRange => throw new UnreachableException("Deque edge access should not produce OutOfRange."),
            _ => throw new UnreachableException()
        };
}

internal class DequeDemo {
    public static void Run() {
        var typed = Durable.Deque<int>();
        typed.PushBack(2);
        typed.PushFront(1);
        typed.PushBack(3);
        Debug.Assert(typed.TrySetFront(10));
        Debug.Assert(typed.TrySetBack(30));
        Debug.Assert(typed.TrySetAt(1, 20));
        Debug.Assert(typed.GetAtOrThrow(1) == 20);
        Debug.Assert(typed.GetFrontOrThrow() == 10);
        Debug.Assert(typed.GetBackOrThrow() == 30);
        Debug.Assert(typed.TryPeekFront(out int front) && front == 10);
        Debug.Assert(typed.TryPeekBack(out int back) && back == 30);
        Debug.Assert(typed.TryPopFront(out int poppedFront) && poppedFront == 10);
        Debug.Assert(typed.TryPopBack(out int poppedBack) && poppedBack == 30);

        var mixed = Durable.Deque();
        mixed.PushBack("title");
        mixed.PushBack(42);
        mixed.PushFront(true);
        Debug.Assert(mixed.OfInt32.TrySetAt(2, 99));
        Debug.Assert(mixed.TryPeekFront(out bool enabled) && enabled);
        Debug.Assert(mixed.OfInt32.GetAtOrThrow(2) == 99);

        mixed.OfString.PushFront("new-title");
        Debug.Assert(mixed.TryPopFront(out string? s) && s == "new-title");

        // 对于 MixedDeque 中的 DurableObject 子类型，不提供 Of<Subtype>() 视图，
        // 而是通过 generic 方法族访问，避免隐藏分配的 subtype wrapper。
        var owner = new Revision(1);
        var child = owner.CreateDict<int, int>();
        child.Upsert(7, 70);
        var mixedWithChild = owner.CreateDeque();
        mixedWithChild.PushBack(child);
        Debug.Assert(mixedWithChild.TryPeekFront<DurableDict<int, int>>(out var frontChild) && ReferenceEquals(frontChild, child));
        Debug.Assert(ReferenceEquals(mixedWithChild.GetBack<DurableDict<int, int>>(), child));
    }
}
