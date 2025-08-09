namespace MemoTree.Core.Types
{
    /// <summary>
    /// LOD (Level of Detail) 级别枚举
    /// 定义认知节点正文内容的详细程度层次
    /// 注意：Title始终显式，与LOD级别正交，属于元数据
    /// </summary>
    public enum LodLevel
    {
        /// <summary>
        /// 要点级别 - 1-2句核心要点
        /// 最小可理解的正文内容单元
        /// 子节点策略：不显示子节点
        /// </summary>
        Gist = 0,

        /// <summary>
        /// 摘要级别 - 要点 + 关键细节摘要
        /// 中等详细的正文内容，适合快速理解和决策
        /// 子节点策略：显示直接子节点的Gist级别
        /// </summary>
        Summary = 1,

        /// <summary>
        /// 完整级别 - 所有正文内容
        /// 完整的正文展示，适合深度阅读和编辑
        /// 子节点策略：显示完整子节点层次
        /// </summary>
        Full = 2
    }

    /// <summary>
    /// LOD级别扩展方法
    /// </summary>
    public static class LodLevelExtensions
    {
        /// <summary>
        /// 获取LOD级别的显示名称
        /// </summary>
        public static string GetDisplayName(this LodLevel level)
        {
            return level switch
            {
                LodLevel.Gist => "要点",
                LodLevel.Summary => "摘要",
                LodLevel.Full => "完整",
                _ => level.ToString()
            };
        }

        /// <summary>
        /// 获取LOD级别的英文名称
        /// </summary>
        public static string GetEnglishName(this LodLevel level)
        {
            return level switch
            {
                LodLevel.Gist => "Gist",
                LodLevel.Summary => "Summary",
                LodLevel.Full => "Full",
                _ => level.ToString()
            };
        }

        /// <summary>
        /// 检查是否包含指定级别的内容
        /// </summary>
        public static bool Includes(this LodLevel current, LodLevel target)
        {
            return (int)current >= (int)target;
        }

        /// <summary>
        /// 获取下一个更详细的级别
        /// </summary>
        public static LodLevel? GetNextLevel(this LodLevel current)
        {
            return current switch
            {
                LodLevel.Gist => LodLevel.Summary,
                LodLevel.Summary => LodLevel.Full,
                LodLevel.Full => null,
                _ => null
            };
        }

        /// <summary>
        /// 获取上一个更简略的级别
        /// </summary>
        public static LodLevel? GetPreviousLevel(this LodLevel current)
        {
            return current switch
            {
                LodLevel.Summary => LodLevel.Gist,
                LodLevel.Full => LodLevel.Summary,
                LodLevel.Gist => null,
                _ => null
            };
        }

        /// <summary>
        /// 判断当前级别是否应该显示子节点
        /// </summary>
        public static bool ShouldShowChildren(this LodLevel level)
        {
            return level switch
            {
                LodLevel.Gist => false,      // 不显示子节点
                LodLevel.Summary => true,    // 显示直接子节点的Gist级别
                LodLevel.Full => true,       // 显示完整子节点层次
                _ => false
            };
        }

        /// <summary>
        /// 获取子节点应该使用的LOD级别
        /// </summary>
        public static LodLevel GetChildrenLodLevel(this LodLevel parentLevel)
        {
            return parentLevel switch
            {
                LodLevel.Summary => LodLevel.Gist,    // Summary显示子节点的Gist级别
                LodLevel.Full => LodLevel.Full,       // Full显示子节点的Full级别
                _ => LodLevel.Gist                    // 默认使用Gist级别
            };
        }
    }
}
