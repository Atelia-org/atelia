namespace MemoTree.Cli;

internal static class IndentTest {
    private static void Demo() {
        {
            Console.WriteLine("Block no indent");
        }

        _ = new DemoClass() {
            A = 1,
            B = 2,
        };
    }

    private class DemoClass {
        public int A {
            get; set;
        }
        public int B {
            get; set;
        }
    }
}
