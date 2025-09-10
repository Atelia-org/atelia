namespace CodeCortexV2.DevCli;

public static class E2eAllCommand {
    public static async Task<int> RunAsync() {
        var steps = new (string Name, Func<Task<int>> Run)[] {
            ("e2e-add-method", E2eAddMethodCommand.RunAsync),
            ("e2e-generic-base-search", E2eGenericBaseSearchCommand.RunAsync),
            ("e2e-doc-removed", E2eDocRemovedCommand.RunAsync),
            ("e2e-partial-type-remove", E2ePartialTypeRemoveCommand.RunAsync),
            ("e2e-namespace-deep-cascade", E2eNamespaceDeepCascadeCommand.RunAsync),
            ("e2e-suffix-ambiguity", E2eSuffixAmbiguityCommand.RunAsync),
            ("e2e-case-insensitive-exact", E2eCaseInsensitiveExactCommand.RunAsync),
            ("e2e-wildcard-search", E2eWildcardSearchCommand.RunAsync),
            ("e2e-fuzzy-fallback", E2eFuzzyFallbackCommand.RunAsync),
            ("e2e-debounce-batch", E2eDebounceBatchCommand.RunAsync),
            ("e2e-with-delta-upsert-order", E2eWithDeltaUpsertOrderCommand.RunAsync),
            ("e2e-project-removed", E2eProjectRemovedCommand.RunAsync),
        };

        int failures = 0;
        foreach (var step in steps) {
            Console.WriteLine($"[e2e-all] >>> {step.Name}");
            var code = await step.Run().ConfigureAwait(false);
            if (code != 0) {
                failures++;
                Console.Error.WriteLine($"[e2e-all] {step.Name} FAILED (exit {code})");
            } else {
                Console.WriteLine($"[e2e-all] {step.Name} OK");
            }
        }

        if (failures > 0) {
            Console.Error.WriteLine($"[e2e-all] Completed with {failures} failure(s)");
            return 1;
        }
        Console.WriteLine("[e2e-all] All passed.");
        return 0;
    }
}
