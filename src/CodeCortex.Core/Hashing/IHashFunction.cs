namespace CodeCortex.Core.Hashing;

/// <summary>
/// Abstraction over hashing so tests can plug in deterministic or fake hash implementations.
/// </summary>
public interface IHashFunction {
    /// <summary>
    /// Compute a hash digest for &lt;paramref name="input"/&gt; and return a Base32-like truncated string of given &lt;paramref name="length"/&gt;.
    /// </summary>
    string Compute(string input, int length = 8);
}
