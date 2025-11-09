using System.Globalization;
using System.Reflection;
using Atelia.Agent.Core.Tool;
using Atelia.Completion.Abstractions;

namespace Atelia.Agent.Core;

/// <summary>
/// 把带有 <see cref="ToolAttribute"/> 与 <see cref="ToolParamAttribute"/> 注解的方法包装为 <see cref="ITool"/> 实例。
/// </summary>
/// <remarks>
/// <para>本文件聚焦于如何构建 <see cref="MethodToolWrapper"/>，包含面向调用方的公开 API 与基础注解。</para>
/// <para>
/// 同目录下的 <c>MethodToolWrapper.Impl.cs</c> 负责参数推断、可空性分析与执行委托的生成，以下规则是针对调用方总结的使用指南，
/// 便于在不了解内部实现细节的情况下定义新工具：
/// </para>
/// <list type="bullet">
/// <item>
/// <description>目标方法必须显式标注 <see cref="ToolAttribute"/>，返回类型固定为 <see cref="ValueTask{TResult}"/>，其中 <c>TResult</c> 为 <see cref="LodToolExecuteResult"/>。</description>
/// </item>
/// <item>
/// <description>方法参数列表的最后一个参数必须为 <see cref="CancellationToken"/>，且无需额外标注 Attribute；其它业务参数必须逐个标注 <see cref="ToolParamAttribute"/>。</description>
/// </item>
/// <item>
/// <description>仅支持值类型 <c>bool</c>、<c>int</c>、<c>long</c>、<c>float</c>、<c>double</c>、<c>decimal</c> 以及引用类型 <c>string</c>（含对应的可空版本），且不接受 <c>ref</c>/<c>out</c> 参数。</description>
/// </item>
/// <item>
/// <description>可空性通过 C# 可空注解推断：引用类型需使用如 <c>string?</c> 的注解，值类型需使用 <c>Nullable&lt;T&gt;</c>，否则视为必填。</description>
/// </item>
/// <item>
/// <description>若参数声明了默认值（可选参数），包装器会把它视为“可省略”并生成 <see cref="ParamDefault"/>；默认值为 <c>null</c> 时必须配合可空签名。</description>
/// </item>
/// <item>
/// <description>最终生成的参数描述会在 <see cref="ToolParamAttribute"/> 提供的文本后自动附加「必填/可省略」「默认值」「允许 null」等提示，调用方无需手动维护。</description>
/// </item>
/// <item>
/// <description>实例方法包装时需要传入实际对象；若使用 <see cref="FromDelegate(Delegate)"/>，请确保委托仅绑定单个方法。</description>
/// </item>
/// </list>
/// </remarks>
public sealed partial class MethodToolWrapper : ITool {

    /// <summary>
    /// 使用单播委托推断目标方法并创建工具包装器。
    /// </summary>
    /// <param name="methodDelegate">指向目标方法的委托，必须仅包装一个方法。</param>
    /// <param name="formatArgs">
    /// 传递给 <see cref="string.Format(string, object?[])"/> 的占位符实参；
    /// 若目标方法或其参数的注解（见 <see cref="ToolAttribute"/> / <see cref="ToolParamAttribute"/>) 无需格式化，可省略。
    ///
    /// <para>
    /// 例如：
    /// <code language="csharp">
    /// [Tool("{0}_replace", "编辑 {1} 文本")]
    /// ValueTask&lt;LodToolExecuteResult&gt; ReplaceAsync(...)
    /// </code>
    /// 调用时可传 <c>MethodToolWrapper.FromDelegate(method, "recap", "Recap")</c>，
    /// 使 name/description 在注册阶段被格式化。
    /// </para>
    /// </param>
    /// <returns>可被代理运行时调用的工具实例。</returns>
    /// <exception cref="ArgumentNullException">当 <paramref name="methodDelegate"/> 为 <c>null</c> 时。</exception>
    /// <exception cref="ArgumentException">当委托绑定多个方法时。</exception>
    public static MethodToolWrapper FromDelegate(Delegate methodDelegate, params object?[] formatArgs) {
        if (methodDelegate is null) { throw new ArgumentNullException(nameof(methodDelegate)); }

        var invocationList = methodDelegate.GetInvocationList();
        if (invocationList.Length != 1) { throw new ArgumentException("Delegate must reference exactly one method.", nameof(methodDelegate)); }

        var singleDelegate = invocationList[0];
        return FromMethod(singleDelegate.Target, singleDelegate.Method, formatArgs);
    }

    public static MethodToolWrapper FromDelegate(
        Func<CancellationToken, ValueTask<LodToolExecuteResult>> methodDelegate,
        params object?[] formatArgs
    ) => FromDelegate((Delegate)methodDelegate, formatArgs);

    public static MethodToolWrapper FromDelegate<T1>(
        Func<T1, CancellationToken, ValueTask<LodToolExecuteResult>> methodDelegate,
        params object?[] formatArgs
    ) => FromDelegate((Delegate)methodDelegate, formatArgs);

    public static MethodToolWrapper FromDelegate<T1, T2>(
        Func<T1, T2, CancellationToken, ValueTask<LodToolExecuteResult>> methodDelegate,
        params object?[] formatArgs
    ) => FromDelegate((Delegate)methodDelegate, formatArgs);

    public static MethodToolWrapper FromDelegate<T1, T2, T3>(
        Func<T1, T2, T3, CancellationToken, ValueTask<LodToolExecuteResult>> methodDelegate,
        params object?[] formatArgs
    ) => FromDelegate((Delegate)methodDelegate, formatArgs);

    public static MethodToolWrapper FromDelegate<T1, T2, T3, T4>(
        Func<T1, T2, T3, T4, CancellationToken, ValueTask<LodToolExecuteResult>> methodDelegate,
        params object?[] formatArgs
    ) => FromDelegate((Delegate)methodDelegate, formatArgs);

    public static MethodToolWrapper FromDelegate<T1, T2, T3, T4, T5>(
        Func<T1, T2, T3, T4, T5, CancellationToken, ValueTask<LodToolExecuteResult>> methodDelegate,
        params object?[] formatArgs
    ) => FromDelegate((Delegate)methodDelegate, formatArgs);

    /// <summary>
    /// 通过反射信息创建工具包装器。
    /// </summary>
    /// <param name="targetInstance">实例方法的目标对象；调用静态方法时传 <c>null</c>。</param>
    /// <param name="method">带有 <see cref="ToolAttribute"/> 的方法元数据。</param>
    /// <param name="formatArgs">
    /// 传递给 <see cref="string.Format(string, object?[])"/> 的占位符实参；
    /// 当 <see cref="ToolAttribute"/> 或 <see cref="ToolParamAttribute"/> 在 name/description 中使用如 <c>{0}</c> 的格式化占位符时，
    /// 可通过该参数注入实际值。若无需格式化，可留空。
    /// </param>
    /// <returns>可供代理执行的工具定义。</returns>
    /// <exception cref="ArgumentNullException">当 <paramref name="method"/> 为 <c>null</c> 时。</exception>
    /// <exception cref="InvalidOperationException">当方法签名或注解不满足工具约束时。</exception>
    /// <remarks>
    /// <para>适用于通过反射注册静态或实例方法，规则如下：</para>
    /// <list type="bullet">
    /// <item>
    /// <description>方法需要标注 <see cref="ToolAttribute"/>，并返回 <see cref="ValueTask{LodToolExecuteResult}"/>。</description>
    /// </item>
    /// <item>
    /// <description>最后一个参数必须是 <see cref="CancellationToken"/>，其余参数必须逐一标注 <see cref="ToolParamAttribute"/>。</description>
    /// </item>
    /// <item>
    /// <description>允许的业务参数类型为 <c>string</c>、<c>bool</c>、<c>int</c>、<c>long</c>、<c>float</c>、<c>double</c>、<c>decimal</c> 及其可空变体，且不支持 <c>ref</c>/<c>out</c>。</description>
    /// </item>
    /// <item>
    /// <description>包装器会根据 C# 可空性注解推断是否允许传入 <c>null</c>；若声明了默认值，则被视作可省略并在元数据中记录默认值文本。</description>
    /// </item>
    /// <item>
    /// <description>对于实例方法，<paramref name="targetInstance"/> 必须是方法声明类型的实例；静态方法则传 <c>null</c>。</description>
    /// </item>
    /// </list>
    /// <para>若违反上述约束（例如缺少 Attribute、类型不受支持、默认值与可空性不匹配等），会抛出 <see cref="InvalidOperationException"/> 或 <see cref="NotSupportedException"/>。</para>
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// internal sealed class SampleToolHost {
    ///     [ToolAttribute("demo.echo", "回显输入文本")]
    ///     public ValueTask&lt;LodToolExecuteResult&gt; EchoAsync(
    ///         [ToolParamAttribute("要回显的文本")] string text,
    ///         CancellationToken cancellationToken
    ///     ) =&gt; new LodToolExecuteResult(text);
    /// }
    ///
    /// var host = new SampleToolHost();
    /// var method = typeof(SampleToolHost).GetMethod(nameof(SampleToolHost.EchoAsync))!;
    /// var tool = MethodToolWrapper.FromMethod(host, method);
    /// // tool 现在可以交由 ToolExecutor 或代理运行时调用
    /// </code>
    /// </example>
    public static MethodToolWrapper FromMethod(object? targetInstance, MethodInfo method, params object?[] formatArgs) {
        return FromMethodImpl(targetInstance, method, formatArgs ?? Array.Empty<object?>());
    }
}

/// <summary>
/// 标注一个面向代理公开的工具方法。
/// </summary>
/// <remarks>
/// 运行时由 <see cref="MethodToolWrapper.FromMethod(object?, MethodInfo)"/> 反射扫描读取此特性，结合方法签名生成工具元数据。
///
/// <para>
/// <strong>格式化占位符：</strong>
/// <see cref="NameFormat"/> 与 <see cref="DescriptionFormat"/> 支持标准的 <see cref="string.Format(string, object?[])"/> 占位符。
/// 若调用方在注册时调用 <see cref="MethodToolWrapper.FromMethod(object?, MethodInfo, object?[])"/> 并提供 <c>formatArgs</c>，
/// 则会在工具实例化过程中将这些占位符替换为实际值；未提供时视为普通常量。
/// </para>
/// </remarks>
/// <see cref="ToolParamAttribute"/>
/// <see cref="MethodToolWrapper.FromMethod(object?, MethodInfo)"/>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ToolAttribute : Attribute {
    private readonly string _nameFormat;
    private readonly string _descriptionFormat;

    /// <summary>
    /// 创建一个工具定义。
    /// </summary>
    /// <param name="name">工具名称模板，可包含 <c>{0}</c> 形式的占位符。</param>
    /// <param name="description">描述文本模板，可包含 <c>{0}</c> 形式的占位符。</param>
    /// <exception cref="ArgumentException">当参数为空或仅包含空白字符时抛出。</exception>
    public ToolAttribute(string name, string description) {
        if (string.IsNullOrWhiteSpace(name)) { throw new ArgumentException("Tool name cannot be null or whitespace.", nameof(name)); }

        if (string.IsNullOrWhiteSpace(description)) { throw new ArgumentException("Tool description cannot be null or whitespace.", nameof(description)); }

        _nameFormat = name.Trim();
        _descriptionFormat = description.Trim();
    }

    /// <summary>
    /// 工具名称模板，可能包含 <see cref="string.Format(string, object?[])"/> 占位符。
    /// </summary>
    public string Name => _nameFormat;

    /// <summary>
    /// 工具用途描述模板，可能包含 <see cref="string.Format(string, object?[])"/> 占位符。
    /// </summary>
    public string Description => _descriptionFormat;

    internal string FormatName(object?[] formatArgs)
        => FormatWithArgs(_nameFormat, formatArgs);

    internal string FormatDescription(object?[] formatArgs)
        => FormatWithArgs(_descriptionFormat, formatArgs);

    private static string FormatWithArgs(string format, object?[] formatArgs) {
        if (formatArgs.Length == 0) { return format; }

        return string.Format(CultureInfo.InvariantCulture, format, formatArgs);
    }
}

/// <summary>
/// 为工具方法的参数提供元数据补充。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ToolParamSpec"/> 会在运行时根据参数的类型、可空注解以及默认值自动推断：
/// </para>
/// <list type="bullet">
/// <item>
/// <description>可选参数应使用 C# 的默认值语法（例如 <c>string text = ""</c>）；若默认值为 <c>null</c>，需要将参数声明为可空类型。</description>
/// </item>
/// <item>
/// <description>描述文本会自动附加「必填/可省略」「默认值」「允许 null」等提示信息。</description>
/// </item>
/// <item>
/// <description>不支持传入 <c>ref</c>/<c>out</c> 参数，参数类型限定为 <c>string</c> 与基本数值/布尔类型（含各自的可空版本）。</description>
/// </item>
/// </list>
///
/// <para>
/// <strong>格式化占位符：</strong><see cref="DescriptionFormat"/> 同样支持 <see cref="string.Format(string, object?[])"/> 占位符，
/// 由 <see cref="MethodToolWrapper.FromMethod(object?, MethodInfo, object?[])"/> 的 <c>formatArgs</c> 在工具注册时替换。
/// </para>
/// </remarks>
/// <see cref="ToolAttribute"/>
/// <see cref="MethodToolWrapper.FromMethod(object?, MethodInfo)"/>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class ToolParamAttribute : Attribute {
    private readonly string _descriptionFormat;

    /// <summary>
    /// 初始化参数描述定义。
    /// </summary>
    /// <param name="description">参数描述模板，可包含 <c>{0}</c> 形式的占位符。</param>
    /// <exception cref="ArgumentException">当 <paramref name="description"/> 为空或仅包含空白字符时抛出。</exception>
    public ToolParamAttribute(string description) {
        if (string.IsNullOrWhiteSpace(description)) { throw new ArgumentException("Parameter description cannot be null or whitespace.", nameof(description)); }

        _descriptionFormat = description.Trim();
    }

    /// <summary>
    /// 参数描述模板，可能包含 <see cref="string.Format(string, object?[])"/> 占位符。
    /// </summary>
    public string Description => _descriptionFormat;

    internal string FormatDescription(object?[] formatArgs)
        => formatArgs.Length == 0
            ? _descriptionFormat
            : string.Format(CultureInfo.InvariantCulture, _descriptionFormat, formatArgs);
}
