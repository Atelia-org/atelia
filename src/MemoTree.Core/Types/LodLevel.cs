namespace MemoTree.Core.Types
{
    /// <summary>
    /// LOD (Level of Detail) 级别枚举
    /// 定义认知节点内容的详细程度层次
    /// </summary>
    public enum LodLevel
    {
        /// <summary>
        /// 标题级别 - 仅包含节点的标题信息
        /// 用于快速浏览和导航
        /// </summary>
        Title = 0,

        /// <summary>
        /// 简要级别 - 包含标题和简要描述
        /// 用于快速理解节点的核心内容
        /// </summary>
        Brief = 1,

        /// <summary>
        /// 详细级别 - 包含详细的内容描述
        /// 用于深入理解节点的具体内容
        /// </summary>
        Detail = 2,

        /// <summary>
        /// 完整级别 - 包含所有可用的内容信息
        /// 用于完整的内容展示和编辑
        /// </summary>
        Full = 3
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
                LodLevel.Title => "标题",
                LodLevel.Brief => "简要",
                LodLevel.Detail => "详细",
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
                LodLevel.Title => "Title",
                LodLevel.Brief => "Brief",
                LodLevel.Detail => "Detail",
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
                LodLevel.Title => LodLevel.Brief,
                LodLevel.Brief => LodLevel.Detail,
                LodLevel.Detail => LodLevel.Full,
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
                LodLevel.Brief => LodLevel.Title,
                LodLevel.Detail => LodLevel.Brief,
                LodLevel.Full => LodLevel.Detail,
                LodLevel.Title => null,
                _ => null
            };
        }
    }
}
