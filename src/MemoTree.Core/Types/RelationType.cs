namespace MemoTree.Core.Types {
    /// <summary>
    /// 节点间关系类型枚举
    /// 定义认知节点之间的语义关系
    /// </summary>
    public enum RelationType {
        /// <summary>
        /// 引用关系 - 一个节点引用另一个节点
        /// 例如：代码引用、文档引用、概念引用等
        /// </summary>
        References = 0,

        /// <summary>
        /// 依赖关系 - 一个节点依赖于另一个节点
        /// 例如：模块依赖、功能依赖、数据依赖等
        /// </summary>
        DependsOn = 1,

        /// <summary>
        /// 组合关系 - 一个节点是另一个节点的组成部分
        /// 例如：类的成员、模块的组件、系统的子系统等
        /// </summary>
        ComposedOf = 2,

        /// <summary>
        /// 继承关系 - 一个节点继承自另一个节点
        /// 例如：类继承、接口实现、概念扩展等
        /// </summary>
        InheritsFrom = 3,

        /// <summary>
        /// 实现关系 - 一个节点实现另一个节点定义的接口或规范
        /// 例如：接口实现、规范实现、协议实现等
        /// </summary>
        Implements = 4,

        /// <summary>
        /// 关联关系 - 两个节点之间存在某种关联
        /// 例如：业务关联、逻辑关联、功能关联等
        /// </summary>
        AssociatedWith = 5,

        /// <summary>
        /// 相似关系 - 两个节点在某些方面相似
        /// 例如：功能相似、结构相似、概念相似等
        /// </summary>
        SimilarTo = 6,

        /// <summary>
        /// 对立关系 - 两个节点存在对立或冲突
        /// 例如：设计冲突、功能对立、概念对立等
        /// </summary>
        ConflictsWith = 7,

        /// <summary>
        /// 前置关系 - 一个节点是另一个节点的前置条件
        /// 例如：流程前置、依赖前置、学习前置等
        /// </summary>
        Precedes = 8,

        /// <summary>
        /// 后续关系 - 一个节点是另一个节点的后续
        /// 例如：流程后续、结果后续、发展后续等
        /// </summary>
        Follows = 9,

        /// <summary>
        /// 替代关系 - 一个节点可以替代另一个节点
        /// 例如：方案替代、工具替代、实现替代等
        /// </summary>
        Replaces = 10,

        /// <summary>
        /// 扩展关系 - 一个节点扩展另一个节点的功能
        /// 例如：功能扩展、概念扩展、能力扩展等
        /// </summary>
        Extends = 11,

        /// <summary>
        /// 包含关系 - 一个节点包含另一个节点
        /// 例如：集合包含、范围包含、概念包含等
        /// </summary>
        Contains = 12,

        /// <summary>
        /// 使用关系 - 一个节点使用另一个节点
        /// 例如：工具使用、服务使用、资源使用等
        /// </summary>
        Uses = 13,

        /// <summary>
        /// 产生关系 - 一个节点产生另一个节点
        /// 例如：过程产生结果、操作产生输出等
        /// </summary>
        Produces = 14,

        /// <summary>
        /// 自定义关系 - 用户定义的特殊关系类型
        /// 用于扩展系统的关系类型
        /// </summary>
        Custom = 15
    }

    /// <summary>
    /// 关系类型扩展方法
    /// </summary>
    public static class RelationTypeExtensions {
        /// <summary>
        /// 获取关系类型的显示名称
        /// </summary>
        public static string GetDisplayName(this RelationType relationType) {
            return relationType switch {
                RelationType.References => "引用",
                RelationType.DependsOn => "依赖",
                RelationType.ComposedOf => "组合",
                RelationType.InheritsFrom => "继承",
                RelationType.Implements => "实现",
                RelationType.AssociatedWith => "关联",
                RelationType.SimilarTo => "相似",
                RelationType.ConflictsWith => "冲突",
                RelationType.Precedes => "前置",
                RelationType.Follows => "后续",
                RelationType.Replaces => "替代",
                RelationType.Extends => "扩展",
                RelationType.Contains => "包含",
                RelationType.Uses => "使用",
                RelationType.Produces => "产生",
                RelationType.Custom => "自定义",
                _ => relationType.ToString()
            };
        }

        /// <summary>
        /// 获取关系类型的英文名称
        /// </summary>
        public static string GetEnglishName(this RelationType relationType) {
            return relationType switch {
                RelationType.References => "References",
                RelationType.DependsOn => "Depends On",
                RelationType.ComposedOf => "Composed Of",
                RelationType.InheritsFrom => "Inherits From",
                RelationType.Implements => "Implements",
                RelationType.AssociatedWith => "Associated With",
                RelationType.SimilarTo => "Similar To",
                RelationType.ConflictsWith => "Conflicts With",
                RelationType.Precedes => "Precedes",
                RelationType.Follows => "Follows",
                RelationType.Replaces => "Replaces",
                RelationType.Extends => "Extends",
                RelationType.Contains => "Contains",
                RelationType.Uses => "Uses",
                RelationType.Produces => "Produces",
                RelationType.Custom => "Custom",
                _ => relationType.ToString()
            };
        }

        /// <summary>
        /// 获取关系类型的描述
        /// </summary>
        public static string GetDescription(this RelationType relationType) {
            return relationType switch {
                RelationType.References => "一个节点引用另一个节点",
                RelationType.DependsOn => "一个节点依赖于另一个节点",
                RelationType.ComposedOf => "一个节点是另一个节点的组成部分",
                RelationType.InheritsFrom => "一个节点继承自另一个节点",
                RelationType.Implements => "一个节点实现另一个节点定义的接口或规范",
                RelationType.AssociatedWith => "两个节点之间存在某种关联",
                RelationType.SimilarTo => "两个节点在某些方面相似",
                RelationType.ConflictsWith => "两个节点存在对立或冲突",
                RelationType.Precedes => "一个节点是另一个节点的前置条件",
                RelationType.Follows => "一个节点是另一个节点的后续",
                RelationType.Replaces => "一个节点可以替代另一个节点",
                RelationType.Extends => "一个节点扩展另一个节点的功能",
                RelationType.Contains => "一个节点包含另一个节点",
                RelationType.Uses => "一个节点使用另一个节点",
                RelationType.Produces => "一个节点产生另一个节点",
                RelationType.Custom => "用户定义的特殊关系类型",
                _ => "未知关系类型"
            };
        }

        /// <summary>
        /// 检查关系是否为有向关系
        /// </summary>
        public static bool IsDirected(this RelationType relationType) {
            return relationType switch {
                RelationType.References or RelationType.DependsOn or RelationType.ComposedOf or
                RelationType.InheritsFrom or RelationType.Implements or RelationType.Precedes or
                RelationType.Follows or RelationType.Replaces or RelationType.Extends or
                RelationType.Contains or RelationType.Uses or RelationType.Produces => true,
                RelationType.AssociatedWith or RelationType.SimilarTo or RelationType.ConflictsWith => false,
                RelationType.Custom => true, // 默认为有向
                _ => true
            };
        }

        /// <summary>
        /// 获取关系的反向类型（如果存在）
        /// </summary>
        public static RelationType? GetInverse(this RelationType relationType) {
            return relationType switch {
                RelationType.Precedes => RelationType.Follows,
                RelationType.Follows => RelationType.Precedes,
                RelationType.Contains => RelationType.ComposedOf,
                RelationType.ComposedOf => RelationType.Contains,
                _ => null
            };
        }
    }
}
