using CodeCortex.Core.Util;

namespace CodeCortex.Core.Hashing;

internal sealed class DefaultHashFunction : IHashFunction {
    public string Compute(string input, int length = 8) => HashUtil.Sha256Base32Trunc(input, length);
}
