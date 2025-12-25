// Source: Atelia.StateJournal - Ptr64 类型别名
// Spec: atelia/docs/StateJournal/rbf-interface.md §2.3

// NOTE: global using must be at file scope (before namespace declarations)

/// <summary>
/// <see cref="Ptr64"/> 是 <see cref="Atelia.Rbf.Address64"/> 的类型别名。
/// </summary>
/// <remarks>
/// <para><b>语义</b>：Ptr64 表示指向 ObjectVersionRecord 的 8 字节文件偏移量。
/// 在 MVP 阶段，它与 <see cref="Atelia.Rbf.Address64"/> 完全等价。</para>
/// <para><b>设计理由</b>：使用 global using 别名而非独立类型，以避免类型转换开销
/// 并保持与 Rbf 层 <see cref="Atelia.Rbf.Address64"/> 的互操作性。</para>
/// </remarks>
global using Ptr64 = Atelia.Rbf.Address64;
