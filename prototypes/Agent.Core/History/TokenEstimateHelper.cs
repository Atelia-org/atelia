using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Atelia.Agent.Core.Tool;
using Atelia.Completion.Abstractions;

namespace Atelia.Agent.Core.History;

/// <summary>
/// 为 <see cref="HistoryEntry"/> 及其派生类型计算 token 估算值的调度器。
/// </summary>
/// <remarks>
/// <para>
/// 该类型通过遍历条目的结构化成员来计算信息量，从而避免额外的字符串拼接开销，
/// 也便于未来扩展到多模态字段的专用估算逻辑。
/// </para>
/// <para>
/// 典型用法：
/// <list type="number">
///   <item><description>在应用启动阶段通过 <see cref="GetDefault"/> 获取全局实例，并调用 <see cref="Configure(ITokenEstimator)"/> 注入自定义估算器（可选）。</description></item>
///   <item><description>调用 <see cref="Estimate(HistoryEntry)"/> 获取估算值，并由调用者负责写入条目。</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class TokenEstimateHelper {
    private static readonly Lazy<TokenEstimateHelper> DefaultInstance = new(() => new TokenEstimateHelper());
    private static readonly AsyncLocal<TokenEstimateHelper?> OverrideInstance = new();

    /// <summary>
    /// 获取默认的全局 <see cref="TokenEstimateHelper"/> 实例。
    /// </summary>
    public static TokenEstimateHelper GetDefault()
        => OverrideInstance.Value ?? DefaultInstance.Value;

    private readonly object _syncRoot = new();
    private ITokenEstimator? _configuredEstimator;
    private bool _isExplicitlyConfigured;

    /// <summary>
    /// 在有限范围内允许测试代码覆盖估算器的临时作用域。
    /// </summary>
    internal IDisposable BeginScopedOverride(ITokenEstimator estimator) {
        if (estimator is null) { throw new ArgumentNullException(nameof(estimator)); }

        var overrideHelper = new TokenEstimateHelper();
        overrideHelper.Configure(estimator);

        var previous = OverrideInstance.Value;
        OverrideInstance.Value = overrideHelper;

        return new OverrideScope(previous);
    }

    /// <summary>
    /// 配置当前实例使用的 <see cref="ITokenEstimator"/> 实现。
    /// 仅可设置一次；再次调用时必须传入同一实例。
    /// </summary>
    /// <param name="tokenEstimator">用于估算 token 的实例。</param>
    public void Configure(ITokenEstimator tokenEstimator) {
        if (tokenEstimator is null) { throw new ArgumentNullException(nameof(tokenEstimator)); }

        lock (_syncRoot) {
            if (_isExplicitlyConfigured && !ReferenceEquals(_configuredEstimator, tokenEstimator)) { throw new InvalidOperationException("Token estimator has already been configured."); }

            _configuredEstimator = tokenEstimator;
            _isExplicitlyConfigured = true;
        }
    }

    /// <summary>
    /// 估算指定条目的 token 信息量。
    /// </summary>
    /// <param name="entry">需要估算的历史条目。</param>
    /// <returns>估算得到的 token 数量，可为零。</returns>
    /// <exception cref="ArgumentNullException">当 <paramref name="entry"/> 为 <c>null</c>。</exception>
    public uint Estimate(HistoryEntry entry) {
        if (entry is null) { throw new ArgumentNullException(nameof(entry)); }

        return entry switch {
            ActionEntry action => EstimateAction(action),
            ToolResultsEntry toolResults => EstimateToolResults(toolResults),
            ObservationEntry observation => EstimateObservation(observation),
            RecapEntry recap => EstimateRecap(recap),
            _ => EstimateFallback(entry)
        };
    }

    private ITokenEstimator AcquireEstimator() {
        var estimator = Volatile.Read(ref _configuredEstimator);
        if (estimator is not null) { return estimator; }

        lock (_syncRoot) {
            estimator = _configuredEstimator;
            if (estimator is null) {
                estimator = new NaiveTokenEstimator();
                _configuredEstimator = estimator;
            }

            return estimator;
        }
    }

    private uint EstimateAction(ActionEntry action) {
        uint total = 0;

        total += EstimateString(action.Contents);
        total += EstimateToolCalls(action.ToolCalls);
        // total += EstimateCompletionDescriptor(action.Invocation); action.Invocation是History层元信息，不会序列化到LLM调用上下文中

        return total;
    }

    private uint EstimateObservation(ObservationEntry observation) {
        uint total = 0;

        total += EstimateContent(observation.Notifications);

        return total;
    }

    private uint EstimateToolResults(ToolResultsEntry entry) {
        uint total = EstimateObservation(entry);

        if (entry.Results is { Count: > 0 }) {
            foreach (var result in entry.Results) {
                total += EstimateString(result.Status.ToString());
                total += EstimateContent(result.Result);
                total += EstimateString(result.ToolName);
                total += EstimateString(result.ToolCallId);
                // total += EstimateElapsed(result.Elapsed); 元信息，不会单独序列化到LLM调用上下文中，已包含在result.Result.Detail中
            }
        }

        total += EstimateString(entry.ExecuteError);

        return total;
    }

    private uint EstimateRecap(RecapEntry recap) {
        uint total = 0;

        total += EstimateString(recap.Contents);
        // total += EstimateUnsigned(recap.InsteadSerial); 元信息，不会序列化到LLM调用上下文中

        return total;
    }

    private uint EstimateToolCalls(IReadOnlyList<ParsedToolCall> toolCalls) {
        if (toolCalls is not { Count: > 0 }) { return 0; }

        uint total = 0;

        foreach (var call in toolCalls) {
            total += EstimateString(call.ToolName);
            total += EstimateString(call.ToolCallId);
            var rawArgumentsEstimate = EstimateRawArguments(call.RawArguments);
            var argumentsEstimate = EstimateParsedArguments(call.Arguments);
            total += Math.Max(rawArgumentsEstimate, argumentsEstimate);
            // total += EstimateString(call.ParseError); 是元信息，不会序列化到LLM调用上下文中
            // total += EstimateString(call.ParseWarning); 是元信息，不会序列化到LLM调用上下文中
        }

        return total;
    }

    private uint EstimateRawArguments(IReadOnlyDictionary<string, string>? rawArguments) {
        if (rawArguments is null || rawArguments.Count == 0) { return 0; }

        uint total = 0;

        foreach (var kvp in rawArguments) {
            total += EstimateString(kvp.Key);
            total += EstimateString(kvp.Value);
        }

        return total;
    }

    private uint EstimateParsedArguments(IReadOnlyDictionary<string, object?>? parsedArguments) {
        if (parsedArguments is null || parsedArguments.Count == 0) { return 0; }

        uint total = 0;

        foreach (var kvp in parsedArguments) {
            total += EstimateString(kvp.Key);
            total += EstimateArgumentValue(kvp.Value);
        }

        return total;
    }

    private uint EstimateArgumentValue(object? value) {
        if (value is null) { return 0; }

        if (value is string text) { return EstimateString(text); }
        if (value is Enum enumValue) { return EstimateString(enumValue.ToString()); }

        if (value is IReadOnlyDictionary<string, object?> nestedReadonlyDictionary) {
            uint nestedTotal = 0;
            foreach (var kvp in nestedReadonlyDictionary) {
                nestedTotal += EstimateString(kvp.Key);
                nestedTotal += EstimateArgumentValue(kvp.Value);
            }

            return nestedTotal;
        }

        if (value is IDictionary dictionary) {
            uint nestedTotal = 0;
            foreach (DictionaryEntry entry in dictionary) {
                nestedTotal += EstimateArgumentValue(entry.Key);
                nestedTotal += EstimateArgumentValue(entry.Value);
            }

            return nestedTotal;
        }

        if (value is IFormattable formattable) { return EstimateString(formattable.ToString(null, CultureInfo.InvariantCulture)); }

        if (value is IEnumerable enumerable) { return EstimateEnumerable(enumerable); }

        return EstimateString(value.ToString());
    }

    private uint EstimateEnumerable(IEnumerable enumerable) {
        uint total = 0;

        foreach (var item in enumerable) {
            total += EstimateArgumentValue(item);
        }

        return total;
    }

    private uint EstimateContent(LevelOfDetailContent? content) {
        if (content is null) { return 0u; }

        var basicEstimate = EstimateString(content.Basic);
        var detailEstimate = EstimateString(content.Detail);
        var estimate = Math.Max(basicEstimate, detailEstimate);

        return estimate;
    }

    private uint EstimateFallback(HistoryEntry entry)
        => EstimateString(entry.ToString()); // 对未知情况选择高估

    internal uint EstimateString(string? value)
        => AcquireEstimator().Estimate(value); // ITokenEstimator.Estimate已约定null返回0

    private sealed class OverrideScope : IDisposable {
        private readonly TokenEstimateHelper? _previous;
        private bool _disposed;

        public OverrideScope(TokenEstimateHelper? previous) {
            _previous = previous;
        }

        public void Dispose() {
            if (_disposed) { return; }

            OverrideInstance.Value = _previous;

            _disposed = true;
        }
    }
}
