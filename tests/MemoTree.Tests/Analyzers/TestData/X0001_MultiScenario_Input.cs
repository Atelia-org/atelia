// Test data for X0001 analyzer multi-scenario coverage.
// Contains patterns where '(' and first parameter/argument are inline but list spans multiple lines.
// Each region labeled for selective extraction if needed.

class X0001MultiScenarioSamples {
    // 1. Invocation
    void InvokeSamples() {
        Target(1,
            2);
    }

    void Target(int a, int b) { }

    // 2. Object creation
    void ObjectCreation() {
        var obj = new Sample(10,
            20);
    }

    record Sample(int A, int B);

    // 3. Method declaration
    void Decl(int a,
        int b) { }

    // 4. Local function
    void Outer() {
        int Local(int x,
            int y) => x + y;
    }

    // 5. Delegate declaration
    delegate int D(int a,
        int b);

    // 6. Operator
    public static X0001MultiScenarioSamples operator +(X0001MultiScenarioSamples l,
        X0001MultiScenarioSamples r) => l;

    // 7. Record primary constructor (already above) + additional record example
    record R2(int X,
        int Y);

    // 8. Parenthesized lambda
    void Lambda() {
        var f = (int a,
            int b) => a + b;
    }

    // 9. GuardA vs GuardB differentiation cases
    // Each argument/parameter is single-line, but contains a block lambda or an initializer.
    // GuardA (no block/initializer guard) should EXEMPT these (no X0001) because AllItemsSingleLine == true.
    // GuardB (with guard) should DIAGNOSE (X0001) because block/initializer present.
    void GuardVariantSamples() {
        // Block lambda arguments
        ProcessFuncs(x => { return x + 1; },
            y => { return y * 2; }
        );

        // Object initializer arguments
        UseObjects(new O { A = 1 },
            new O { B = 2 }
        );

        // Collection initializer arguments
        UseLists(new List<int> { 1, 2 },
            new List<int> { 3, 4 }
        );
    }

    void ProcessFuncs(Func<int,int> f1, Func<int,int> f2) { }
    void UseObjects(O a, O b) { }
    void UseLists(List<int> a, List<int> b) { }
    class O { public int A { get; set; } public int B { get; set; } }
}
