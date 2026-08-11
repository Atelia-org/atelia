"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const model = require("./recap-cadence-cost-model.js");

function nominalInput(overrides = {}) {
    return {
        minimumRecentHistoryLoad: 18_000,
        currentIntervalHistoryLoad: 21_000,
        historyLoadPerRequest: 950,
        providerTokensPerHistoryLoad: 1,
        stablePromptTokens: 6_000,
        publishedRecapTokens: 3_000,
        onlineOutputTokensPerRequest: 900,
        fixedRewriteInputTokens: 8_000,
        sourcePassesPerBuild: 2,
        repeatInputPrice: 0.5,
        firstPassInputPrice: 6.25,
        recapRefreshInputPrice: 6.25,
        rewriteInputPrice: 5,
        outputPrice: 25,
        minimumIntervalHistoryLoad: 1,
        maximumIntervalHistoryLoad: null,
        ...overrides
    };
}

test("solves the convex fluid model analytically", () => {
    const result = model.solve(nominalInput());
    const expected = Math.sqrt(
        result.coefficients.fixedBuildNumerator
        / result.coefficients.growingContextCoefficient
    );

    assert.equal(result.continuousOptimum, expected);
    assert.ok(Math.abs(
        result.unconstrained.growingSuffix
        - result.unconstrained.fixedBuild
    ) < 1e-10);
    assert.ok(
        result.recommended.total
        <= model.costAtInterval(
            result.coefficients,
            result.recommended.intervalHistoryLoad - 1
        ).total
    );
    assert.ok(
        result.recommended.total
        <= model.costAtInterval(
            result.coefficients,
            result.recommended.intervalHistoryLoad + 1
        ).total
    );
});

test("a fourfold fixed build cost doubles the optimum", () => {
    const base = model.solve(nominalInput({
        repeatInputPrice: 1,
        firstPassInputPrice: 1,
        recapRefreshInputPrice: 1,
        fixedRewriteInputTokens: 1_000,
        publishedRecapTokens: 1_000,
        outputPrice: 1
    }));
    const fourfold = model.solve(nominalInput({
        repeatInputPrice: 1,
        firstPassInputPrice: 1,
        recapRefreshInputPrice: 1,
        fixedRewriteInputTokens: 4_000,
        publishedRecapTokens: 4_000,
        outputPrice: 1
    }));

    assert.ok(Math.abs(
        fourfold.continuousOptimum / base.continuousOptimum - 2
    ) < 1e-12);
});

test("a fourfold repeat-context price halves the optimum", () => {
    const base = model.solve(nominalInput({
        repeatInputPrice: 1,
        firstPassInputPrice: 1,
        recapRefreshInputPrice: 1
    }));
    const fourfold = model.solve(nominalInput({
        repeatInputPrice: 4,
        firstPassInputPrice: 4,
        recapRefreshInputPrice: 4
    }));

    assert.ok(Math.abs(
        fourfold.continuousOptimum / base.continuousOptimum - 0.5
    ) < 1e-12);
});

test("equal refresh and repeat prices remove the cache refresh surcharge", () => {
    const coefficients = model.deriveCoefficients(nominalInput({
        repeatInputPrice: 5,
        firstPassInputPrice: 5,
        recapRefreshInputPrice: 5
    }));
    const cost = model.costAtInterval(coefficients, 21_000);

    assert.equal(coefficients.cacheRefreshPriceDelta, 0);
    assert.equal(cost.breakdown.postBuildCacheRefresh, 0);
});

test("applies an explicit upper interval constraint", () => {
    const result = model.solve(nominalInput({
        maximumIntervalHistoryLoad: 10_000
    }));

    assert.equal(result.recommended.intervalHistoryLoad, 10_000);
    assert.equal(result.constrained, true);
});

test("rejects cross-field and degenerate inputs", () => {
    assert.throws(
        () => model.solve(nominalInput({
            minimumIntervalHistoryLoad: 20,
            maximumIntervalHistoryLoad: 10
        })),
        /maximumIntervalHistoryLoad/
    );
    assert.throws(
        () => model.solve(nominalInput({ repeatInputPrice: 0 })),
        /repeat-context coefficient/
    );
    assert.throws(
        () => model.solve(nominalInput({
            repeatInputPrice: 1,
            firstPassInputPrice: 0.5
        })),
        /firstPassInputPrice/
    );
    assert.throws(
        () => model.solve(nominalInput({
            currentIntervalHistoryLoad: 10.5
        })),
        /safe integer/
    );
});
