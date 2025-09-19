namespace CodeCortex.Core.Hashing;

/// <summary>
/// Removes trivia (whitespace/comments) from code fragments for impl hashing.
/// </summary>
public interface ITriviaStripper {
    /// <summary>Strip whitespace and comments from a code fragment yielding a stable body representation.</summary>
    string Strip(string codeFragment);
}
