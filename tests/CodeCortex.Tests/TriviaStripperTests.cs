using Xunit;
using CodeCortex.Core.Hashing;

namespace CodeCortex.Tests;

public class TriviaStripperTests {
    [Fact]
    public void Removes_LineAndBlockComments() {
        var code = "// head\nint x = 1; /* mid */ int y = 2; // tail";
        var stripper = new DefaultTriviaStripper();
        var stripped = stripper.Strip(code);
        Assert.DoesNotContain("head", stripped);
        Assert.DoesNotContain("mid", stripped);
        Assert.DoesNotContain("tail", stripped);
        Assert.Contains("intx=1;inty=2;".Replace(" ", string.Empty), stripped.Replace(" ", string.Empty));
    }
}
