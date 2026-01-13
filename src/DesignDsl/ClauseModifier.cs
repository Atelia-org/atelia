namespace Atelia.DesignDsl;

/// <summary>
/// Clause 修饰符类型。
/// </summary>
public enum ClauseModifier {
    /// <summary>
    /// Decision（决策）：定义约束和目标，不可轻易改动。
    /// </summary>
    Decision,

    /// <summary>
    /// Spec（规格）：为满足决策的约束。
    /// </summary>
    Spec,

    /// <summary>
    /// Derived（推导）：解释性内容。
    /// </summary>
    Derived
}
