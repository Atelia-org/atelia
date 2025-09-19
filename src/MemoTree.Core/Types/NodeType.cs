namespace MemoTree.Core.Types {
    /// <summary>
    /// 认知节点类型枚举
    /// 定义不同类型的认知节点，用于分类和处理
    /// </summary>
    public enum NodeType {
        /// <summary>
        /// 概念节点 - 表示抽象的概念、理论或想法
        /// 例如：设计模式、算法概念、业务规则等
        /// </summary>
        Concept = 0,

        /// <summary>
        /// 实体节点 - 表示具体的实体或对象
        /// 例如：类、文件、数据库表、API接口等
        /// </summary>
        Entity = 1,

        /// <summary>
        /// 过程节点 - 表示流程、步骤或操作
        /// 例如：工作流程、算法步骤、部署流程等
        /// </summary>
        Process = 2,

        /// <summary>
        /// 属性节点 - 表示特征、属性或配置
        /// 例如：配置项、属性定义、特征描述等
        /// </summary>
        Property = 3,

        /// <summary>
        /// 事件节点 - 表示事件、状态变化或时间点
        /// 例如：系统事件、状态转换、里程碑等
        /// </summary>
        Event = 4,

        /// <summary>
        /// 资源节点 - 表示资源、工具或引用
        /// 例如：文档、链接、工具、库等
        /// </summary>
        Resource = 5,

        /// <summary>
        /// 问题节点 - 表示问题、疑问或待解决的事项
        /// 例如：Bug、技术债务、设计问题等
        /// </summary>
        Issue = 6,

        /// <summary>
        /// 解决方案节点 - 表示解决方案、答案或方法
        /// 例如：解决方案、最佳实践、解决步骤等
        /// </summary>
        Solution = 7,

        /// <summary>
        /// 注释节点 - 表示注释、说明或备注
        /// 例如：代码注释、设计说明、备注信息等
        /// </summary>
        Note = 8,

        /// <summary>
        /// 容器节点 - 表示容器、分组或组织结构
        /// 例如：模块、包、命名空间、文件夹等
        /// </summary>
        Container = 9
    }

    /// <summary>
    /// 节点类型扩展方法
    /// </summary>
    public static class NodeTypeExtensions {
        /// <summary>
        /// 获取节点类型的显示名称
        /// </summary>
        public static string GetDisplayName(this NodeType nodeType) {
            return nodeType switch {
                NodeType.Concept => "概念",
                NodeType.Entity => "实体",
                NodeType.Process => "过程",
                NodeType.Property => "属性",
                NodeType.Event => "事件",
                NodeType.Resource => "资源",
                NodeType.Issue => "问题",
                NodeType.Solution => "解决方案",
                NodeType.Note => "注释",
                NodeType.Container => "容器",
                _ => nodeType.ToString()
            };
        }

        /// <summary>
        /// 获取节点类型的英文名称
        /// </summary>
        public static string GetEnglishName(this NodeType nodeType) {
            return nodeType switch {
                NodeType.Concept => "Concept",
                NodeType.Entity => "Entity",
                NodeType.Process => "Process",
                NodeType.Property => "Property",
                NodeType.Event => "Event",
                NodeType.Resource => "Resource",
                NodeType.Issue => "Issue",
                NodeType.Solution => "Solution",
                NodeType.Note => "Note",
                NodeType.Container => "Container",
                _ => nodeType.ToString()
            };
        }

        /// <summary>
        /// 获取节点类型的描述
        /// </summary>
        public static string GetDescription(this NodeType nodeType) {
            return nodeType switch {
                NodeType.Concept => "表示抽象的概念、理论或想法",
                NodeType.Entity => "表示具体的实体或对象",
                NodeType.Process => "表示流程、步骤或操作",
                NodeType.Property => "表示特征、属性或配置",
                NodeType.Event => "表示事件、状态变化或时间点",
                NodeType.Resource => "表示资源、工具或引用",
                NodeType.Issue => "表示问题、疑问或待解决的事项",
                NodeType.Solution => "表示解决方案、答案或方法",
                NodeType.Note => "表示注释、说明或备注",
                NodeType.Container => "表示容器、分组或组织结构",
                _ => "未知类型"
            };
        }

        /// <summary>
        /// 检查节点类型是否为结构性类型（容器类型）
        /// </summary>
        public static bool IsStructural(this NodeType nodeType) {
            return nodeType == NodeType.Container;
        }

        /// <summary>
        /// 检查节点类型是否为内容性类型
        /// </summary>
        public static bool IsContent(this NodeType nodeType) {
            return nodeType switch {
                NodeType.Concept or NodeType.Entity or NodeType.Process or
                NodeType.Property or NodeType.Event or NodeType.Resource or
                NodeType.Issue or NodeType.Solution or NodeType.Note => true,
                _ => false
            };
        }
    }
}
