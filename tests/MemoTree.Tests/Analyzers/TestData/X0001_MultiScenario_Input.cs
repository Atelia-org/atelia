// Test data for X0001 analyzer multi-scenario coverage.
// Contains patterns where '(' and first parameter/argument are inline but list spans multiple lines.
// Each region labeled for selective extraction if needed.

class X0001MultiScenarioSamples {
    // 1. Invocation
    void InvokeSamples() {
        Target(
            1,
            2
        );
    }

    void Target(int a, int b) { }

    // 2. Object creation
    void ObjectCreation() {
        var obj = new Sample(
            10,
            20
        );
    }

    record Sample(int A, int B);

    // 3. Method declaration
    void Decl(
        int a,
        int b
    ) { }

    // 4. Local function
    void Outer() {
        int Local(
            int x,
            int y
        ) => x + y;
    }

    // 5. Delegate declaration
    delegate int D(
        int a,
        int b
    );

    // 6. Operator
    public static X0001MultiScenarioSamples operator +(
        X0001MultiScenarioSamples l,
        X0001MultiScenarioSamples r
    ) => l;

    // 7. Record primary constructor (already above) + additional record example
    record R2(
        int X,
        int Y
    );

    // 8. Parenthesized lambda
    void Lambda() {
        var f = (
            int a,
            int b) => a + b;
    }
}
