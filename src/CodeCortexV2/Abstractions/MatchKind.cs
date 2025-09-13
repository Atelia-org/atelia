
namespace CodeCortexV2.Abstractions;

public enum MatchKind {
    Id = 0,
    Exact = 1,
    ExactIgnoreCase = 2,
    Prefix = 3,
    Contains = 4,
    Suffix = 5,
    Wildcard = 6,
    GenericBase = 7,
    Fuzzy = 8
}
