using Xunit;
using CodeCortex.Core.Models;

namespace Atelia.CodeCortex.Tests;

public class OutlineTests {
    [Fact]
    public void Placeholder() => Assert.True(TypeHashes.Empty.Structure == "");
}
