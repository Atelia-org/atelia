namespace Atelia.Agent.Core.Text;

/// <summary>
/// 默认分块策略：按 <c>\n</c> 逐行拆分。
/// 作为兜底策略，适用于无格式特定需求的纯文本。
/// </summary>
public sealed class DefaultBlockizer : IBlockizer {
    public static readonly DefaultBlockizer Instance = new();

    private DefaultBlockizer() { }

    public string[] Blockize(string text) => text.Split('\n');
}
