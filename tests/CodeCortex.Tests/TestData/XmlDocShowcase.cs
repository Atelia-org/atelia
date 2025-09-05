using System;

namespace Atelia.DocSamples;

#pragma warning disable 0067
/// <summary>
/// Showcase for XML documentation rendering in Outline.
/// <para>Includes multiple structures:</para>
/// <list type="bullet">
///   <item>Bullet item A</item>
///   <item>Bullet item B with <see cref="string"/> and <c>inline code</c></item>
/// </list>
/// <list type="number">
///   <item>Step 1</item>
///   <item>Step 2</item>
/// </list>
/// <list type="table">
///   &lt;listheader&gt;
///     <term>Key</term><term>Value</term>
///   &lt;/listheader&gt;
///   <item><term>A</term><term>Alpha</term></item>
///   <item><term>B</term><term>Beta</term></item>
/// </list>
/// </summary>
public class XmlDocShowcase<T> {
    /// <summary>
    /// Initializes with a name.
    /// </summary>
    /// <param name="name">Display name.</param>
    public XmlDocShowcase(string name) { Name = name; }

    /// <summary>Display name.</summary>
    public string Name { get; }

    /// <summary>Indexer by position.</summary>
    /// <param name="index">0-based position.</param>
    public T this[int index] => throw new NotImplementedException();

    /// <summary>
    /// Combine two values into a tuple.
    /// </summary>
    /// <param name="key">The &lt;typeparamref name="TKey"/&gt; key.</param>
    /// <param name="value">The &lt;typeparamref name="TValue"/&gt; value.</param>
    /// <typeparam name="TKey">Key type.</typeparam>
    /// <typeparam name="TValue">Value type.</typeparam>
    /// <returns>A tuple of (&lt;paramref name="key"/&gt;, &lt;paramref name="value"/&gt;).</returns>
    /// <exception cref="ArgumentNullException">If &lt;paramref name="key"/&gt; is null for reference types.</exception>
    public (TKey, TValue) Combine<TKey, TValue>(TKey key, TValue value) {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Example method showing param with multi-line description and nested list.
    /// </summary>
    /// <param name="force">
    ///     Whether to force execution.
    ///     <list type="bullet">
    ///       <item><term>true</term><description>Run regardless of conditions.</description></item>
    ///       <item><term>false</term><description>Respect normal thresholds.</description></item>
    ///     </list>
    /// </param>
    /// <returns>
    /// Normalized status text.
    /// <list type="number">
    ///   <item>Phase A</item>
    ///   <item>Phase B</item>
    /// </list>
    /// </returns>
    /// <exception cref="InvalidOperationException">When state is invalid.</exception>
    public string CompactExample(bool force) {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Raised when an item is added.
    /// <para>Typical handlers:</para>
    /// <list type="bullet">
    ///   <item>Update UI</item>
    ///   <item>Log to diagnostics</item>
    /// </list>
    /// </summary>
    public event EventHandler? ItemAdded;

    /// <summary>
    /// Raised when an inner value changes.
    /// </summary>
    public event EventHandler<Inner>? InnerChanged;

    /// <summary>
    /// Formats a value to string for display.
    /// </summary>
    /// <typeparam name="TIn">Input type.</typeparam>
    /// <param name="value">The input value.</param>
    /// <returns>Formatted string.</returns>
    /// <exception cref="ArgumentNullException">If &lt;paramref name="value"/&gt; is null for reference types.</exception>
    public delegate string Formatter<in TIn>(TIn value);
    /// <summary>
    /// Showcase with tables in returns and exceptions.
    /// </summary>
    /// <param name="count">Item count.</param>
    /// <returns>
    /// <list type="table">
    ///   &lt;listheader&gt;<term>State</term><term>Meaning</term>&lt;/listheader&gt;
    ///   <item><term>Empty</term><term>No items</term></item>
    ///   <item><term>NonEmpty</term><term>Has items</term></item>
    /// </list>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <list type="table">
    ///   &lt;listheader&gt;<term>Param</term><term>Rule</term>&lt;/listheader&gt;
    ///   <item><term>count</term><term>Must be &gt;= 0</term></item>
    /// </list>
    /// </exception>
    public string TableShowcase(int count) => throw new NotImplementedException();



    /// <summary>Nested type.</summary>
    public struct Inner {
        /// <summary>Inner value.</summary>
        public int Value;
    }
}

#pragma warning restore 0067
