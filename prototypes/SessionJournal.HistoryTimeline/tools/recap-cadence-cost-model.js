/*
 * Steady-state API cost model for SessionJournal HistoryTimeline cadence.
 *
 * Prices use USD per million provider tokens. Costs returned by this module
 * use USD per million newly produced HistoryLoad units.
 */
(function installRecapCadenceCostModel(root) {
"use strict";

    const MODEL_VERSION = "atelia.recap-cadence-cost.fluid-v1";

    function requireFiniteNumber(name, value, minimum, allowEqual = true) {
        if (!Number.isFinite(value)
            || (allowEqual ? value < minimum : value <= minimum)) {
            const relation = allowEqual ? ">=" : ">";
            throw new RangeError(`${name} must be finite and ${relation} ${minimum}.`);
        }
    }

    function requireSafeInteger(name, value) {
        if (!Number.isSafeInteger(value)) {
            throw new RangeError(`${name} must be a safe integer.`);
        }
    }

    function normalizeInputs(input) {
        const normalized = {
            minimumRecentHistoryLoad: Number(input.minimumRecentHistoryLoad),
            currentIntervalHistoryLoad: Number(input.currentIntervalHistoryLoad),
            historyLoadPerRequest: Number(input.historyLoadPerRequest),
            providerTokensPerHistoryLoad: Number(
                input.providerTokensPerHistoryLoad
            ),
            stablePromptTokens: Number(input.stablePromptTokens),
            publishedRecapTokens: Number(input.publishedRecapTokens),
            onlineOutputTokensPerRequest: Number(
                input.onlineOutputTokensPerRequest
            ),
            fixedRewriteInputTokens: Number(input.fixedRewriteInputTokens),
            sourcePassesPerBuild: Number(input.sourcePassesPerBuild),
            repeatInputPrice: Number(input.repeatInputPrice),
            firstPassInputPrice: Number(input.firstPassInputPrice),
            recapRefreshInputPrice: Number(input.recapRefreshInputPrice),
            rewriteInputPrice: Number(input.rewriteInputPrice),
            outputPrice: Number(input.outputPrice),
            minimumIntervalHistoryLoad: Number(
                input.minimumIntervalHistoryLoad
            ),
            maximumIntervalHistoryLoad:
                input.maximumIntervalHistoryLoad === null
                || input.maximumIntervalHistoryLoad === undefined
                || input.maximumIntervalHistoryLoad === ""
                    ? null
                    : Number(input.maximumIntervalHistoryLoad)
        };

        requireFiniteNumber(
            "minimumRecentHistoryLoad",
            normalized.minimumRecentHistoryLoad,
            0
        );
        requireFiniteNumber(
            "currentIntervalHistoryLoad",
            normalized.currentIntervalHistoryLoad,
            0,
            false
        );
        requireFiniteNumber(
            "historyLoadPerRequest",
            normalized.historyLoadPerRequest,
            0,
            false
        );
        requireFiniteNumber(
            "providerTokensPerHistoryLoad",
            normalized.providerTokensPerHistoryLoad,
            0,
            false
        );
        requireFiniteNumber(
            "minimumIntervalHistoryLoad",
            normalized.minimumIntervalHistoryLoad,
            0,
            false
        );
        for (const name of [
            "minimumRecentHistoryLoad",
            "currentIntervalHistoryLoad",
            "minimumIntervalHistoryLoad"
        ]) {
            requireSafeInteger(name, normalized[name]);
        }

        for (const name of [
            "stablePromptTokens",
            "publishedRecapTokens",
            "onlineOutputTokensPerRequest",
            "fixedRewriteInputTokens",
            "sourcePassesPerBuild",
            "repeatInputPrice",
            "firstPassInputPrice",
            "recapRefreshInputPrice",
            "rewriteInputPrice",
            "outputPrice"
        ]) {
            requireFiniteNumber(name, normalized[name], 0);
        }

        if (normalized.maximumIntervalHistoryLoad !== null) {
            requireFiniteNumber(
                "maximumIntervalHistoryLoad",
                normalized.maximumIntervalHistoryLoad,
                0,
                false
            );
            if (normalized.maximumIntervalHistoryLoad
                < normalized.minimumIntervalHistoryLoad) {
                throw new RangeError(
                    "maximumIntervalHistoryLoad must be greater than or "
                    + "equal to minimumIntervalHistoryLoad."
                );
            }
            requireSafeInteger(
                "maximumIntervalHistoryLoad",
                normalized.maximumIntervalHistoryLoad
            );
        }
        if (normalized.firstPassInputPrice
            < normalized.repeatInputPrice) {
            throw new RangeError(
                "firstPassInputPrice must be greater than or equal to "
                + "repeatInputPrice."
            );
        }
        if (normalized.recapRefreshInputPrice
            < normalized.repeatInputPrice) {
            throw new RangeError(
                "recapRefreshInputPrice must be greater than or equal to "
                + "repeatInputPrice."
            );
        }

        return normalized;
    }

    function deriveCoefficients(input) {
        const value = normalizeInputs(input);
        const {
            minimumRecentHistoryLoad: recent,
            historyLoadPerRequest: loadPerRequest,
            providerTokensPerHistoryLoad: tokenScale,
            stablePromptTokens,
            publishedRecapTokens,
            onlineOutputTokensPerRequest,
            fixedRewriteInputTokens,
            sourcePassesPerBuild,
            repeatInputPrice,
            firstPassInputPrice,
            recapRefreshInputPrice,
            rewriteInputPrice,
            outputPrice
        } = value;

        // Repeated recent suffix grows from R to R+B, so its steady-state
        // average is R+B/2. This coefficient multiplies B.
        const growingContextCoefficient =
            tokenScale * repeatInputPrice / (2 * loadPerRequest);

        // The repeated-price baseline already counts the first request after a
        // build. Only the price difference for the invalidated prefix belongs
        // in the fixed-per-build term.
        const refreshTokens = publishedRecapTokens + tokenScale * recent;
        const cacheRefreshPriceDelta =
            recapRefreshInputPrice - repeatInputPrice;
        const cacheRefreshNumerator =
            refreshTokens * cacheRefreshPriceDelta;

        // Dividing this numerator by B yields USD per million new HistoryLoad.
        const fixedBuildNumerator =
            fixedRewriteInputTokens * rewriteInputPrice
            + publishedRecapTokens * outputPrice
            + cacheRefreshNumerator;

        if (!(growingContextCoefficient > 0)) {
            throw new RangeError(
                "The repeat-context coefficient must be positive; set a "
                + "positive repeat input price."
            );
        }
        if (!(fixedBuildNumerator > 0)) {
            throw new RangeError(
                "The fixed per-build cost must be positive after cache-price "
                + "adjustment."
            );
        }

        const constantComponents = {
            onlineStableInput:
                repeatInputPrice
                * (stablePromptTokens + publishedRecapTokens)
                / loadPerRequest,
            onlineRecentReserve:
                repeatInputPrice * tokenScale * recent / loadPerRequest,
            newHistoryFirstPassSurcharge:
                tokenScale * (firstPassInputPrice - repeatInputPrice),
            onlineOutput:
                outputPrice * onlineOutputTokensPerRequest / loadPerRequest,
            rewriteSourceInput:
                tokenScale * sourcePassesPerBuild * rewriteInputPrice
        };
        const constantCost = Object.values(constantComponents)
            .reduce((sum, component) => sum + component, 0);

        return {
            input: value,
            growingContextCoefficient,
            fixedBuildNumerator,
            fixedBuildCostDollars: fixedBuildNumerator / 1_000_000,
            refreshTokens,
            cacheRefreshPriceDelta,
            constantComponents,
            constantCost
        };
    }

    function costAtInterval(coefficients, intervalHistoryLoad) {
        requireFiniteNumber(
            "intervalHistoryLoad",
            intervalHistoryLoad,
            0,
            false
        );

        const b = intervalHistoryLoad;
        const growingSuffix = coefficients.growingContextCoefficient * b;
        const fixedBuild = coefficients.fixedBuildNumerator / b;
        const breakdown = {
            ...coefficients.constantComponents,
            onlineGrowingSuffix: growingSuffix,
            rewriteFixedInput:
                coefficients.input.fixedRewriteInputTokens
                * coefficients.input.rewriteInputPrice / b,
            rewriteOutput:
                coefficients.input.publishedRecapTokens
                * coefficients.input.outputPrice / b,
            postBuildCacheRefresh:
                coefficients.refreshTokens
                * coefficients.cacheRefreshPriceDelta / b
        };
        const total = Object.values(breakdown)
            .reduce((sum, component) => sum + component, 0);

        return {
            intervalHistoryLoad: b,
            total,
            cadenceSensitive: growingSuffix + fixedBuild,
            growingSuffix,
            fixedBuild,
            breakdown,
            requestsPerBuild: b / coefficients.input.historyLoadPerRequest,
            buildsPerMillionHistoryLoad: 1_000_000 / b
        };
    }

    function clamp(value, minimum, maximum) {
        return Math.min(maximum, Math.max(minimum, value));
    }

    function bestIntegerInterval(coefficients, continuous) {
        const minimum = Math.ceil(
            coefficients.input.minimumIntervalHistoryLoad
        );
        const maximum = coefficients.input.maximumIntervalHistoryLoad === null
            ? Number.MAX_SAFE_INTEGER
            : Math.floor(coefficients.input.maximumIntervalHistoryLoad);
        const constrained = clamp(continuous, minimum, maximum);
        const candidates = new Set([
            minimum,
            Math.floor(constrained),
            Math.ceil(constrained)
        ]);
        if (coefficients.input.maximumIntervalHistoryLoad !== null) {
            candidates.add(maximum);
        }

        return [...candidates]
            .filter(candidate => Number.isSafeInteger(candidate))
            .filter(candidate => candidate >= minimum && candidate <= maximum)
            .map(candidate => costAtInterval(coefficients, candidate))
            .sort((left, right) =>
                left.total - right.total
                || left.intervalHistoryLoad - right.intervalHistoryLoad
            )[0];
    }

    function sensitivityBand(optimum, tolerance) {
        requireFiniteNumber("tolerance", tolerance, 0);
        const sum = 2 * (1 + tolerance);
        const discriminant = Math.sqrt(sum * sum - 4);
        return {
            tolerance,
            lower: optimum * (sum - discriminant) / 2,
            upper: optimum * (sum + discriminant) / 2
        };
    }

    function solve(input) {
        const coefficients = deriveCoefficients(input);
        const continuousOptimum = Math.sqrt(
            coefficients.fixedBuildNumerator
            / coefficients.growingContextCoefficient
        );
        const recommended = bestIntegerInterval(
            coefficients,
            continuousOptimum
        );
        const current = costAtInterval(
            coefficients,
            coefficients.input.currentIntervalHistoryLoad
        );
        const unconstrained = costAtInterval(
            coefficients,
            continuousOptimum
        );

        return {
            modelVersion: MODEL_VERSION,
            coefficients,
            continuousOptimum,
            unconstrained,
            recommended,
            current,
            constrained:
                (continuousOptimum
                    < coefficients.input.minimumIntervalHistoryLoad)
                || (coefficients.input.maximumIntervalHistoryLoad !== null
                    && continuousOptimum
                        > coefficients.input.maximumIntervalHistoryLoad),
            totalSavingsFraction:
                (current.total - recommended.total) / current.total,
            sensitiveSavingsFraction:
                (current.cadenceSensitive - recommended.cadenceSensitive)
                / current.cadenceSensitive,
            fivePercentSensitiveBand: sensitivityBand(
                continuousOptimum,
                0.05
            )
        };
    }

    const api = Object.freeze({
        MODEL_VERSION,
        normalizeInputs,
        deriveCoefficients,
        costAtInterval,
        sensitivityBand,
        solve
    });

    root.RecapCadenceCostModel = api;
    if (typeof module !== "undefined" && module.exports) {
        module.exports = api;
    }
})(typeof globalThis === "undefined" ? this : globalThis);
