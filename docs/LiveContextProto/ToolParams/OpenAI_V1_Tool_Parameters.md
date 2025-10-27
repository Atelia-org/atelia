# OpenAI v1 工具参数使用说明

## 背景
OpenAI v1 的工具（Tools）接口采用 JSON Schema 来描述参数结构，与传统的函数签名不同。本文总结了我在实现 Live Context 原型时对工具参数的理解，以及如何在调用、校验与序列化过程中落实这些约定。

## OpenAI v1 工具参数模型
- 每个工具以 `type: "function"` 声明，并在 `function.parameters` 中嵌入 JSON Schema。
- Schema 的核心字段：
  - `type`: 顶层通常是 `"object"`，用于容纳命名参数。
  - `properties`: 字段名到子 Schema 的映射，描述每个参数的类型、说明、取值范围等。
  - `required`: 数组，列出必须出现的参数名。
  - `additionalProperties`: 建议设为 `false`，阻止未定义的参数进入。
  - `description`: 对参数或函数的额外解释，可用于辅助生成 UI 或文档。
- 支持的基本类型遵循 JSON Schema：`string`、`number`、`integer`、`boolean`、`array`、`object`。可通过 `enum`、`pattern`、`minItems` 等关键字约束范围。
- 复合与嵌套：`properties` 可递归定义子对象，或通过 `items` 描述数组元素的结构。

### 示例
```json
{
  "type": "function",
  "function": {
    "name": "fetch_node_context",
    "description": "为 Live Context 获取节点详情",
    "parameters": {
      "type": "object",
      "required": ["nodeId"],
      "properties": {
        "nodeId": {
          "type": "string",
          "description": "上下文节点的全局唯一标识"
        },
        "includeChildren": {
          "type": "boolean",
          "description": "是否连同子节点一起返回",
          "default": false
        },
        "maxDepth": {
          "type": "integer",
          "minimum": 0,
          "maximum": 8,
          "description": "递归展开的最大深度"
        }
      },
      "additionalProperties": false
    }
  }
}
```

## 参数解析与校验流程
1. **Schema 注册**：在初始化时，把 JSON Schema 作为工具定义的一部分注册到模型端。
2. **模型输出**：模型调用工具时，返回 `arguments` 字符串，需解析为 JSON 对象。
3. **Schema 校验**：使用 JSON Schema 校验库（如 `System.Text.Json` + 自定义验证、`NJsonSchema` 等）验证对象是否满足定义。
4. **转换映射**：通过显式映射将 JSON 字段转换为内部数据结构（DTO、record、匿名类型均可）。
5. **执行与回写**：在业务逻辑中消费这些参数，执行完毕后返回结构化结果（通常也是 JSON）。

## 与标准 C# 参数模型的差异
| 维度 | OpenAI v1 工具参数 | C# 方法参数 |
| --- | --- | --- |
| 定义方式 | JSON Schema，强调数据结构与约束 | 编译时函数签名，强调类型与调用约定 |
| 编译期检查 | 无编译期检查，依赖运行时 Schema 校验 | 编译器在编译期确保类型与必选参数正确 |
| 可选/默认值 | 通过省略 `required` 或设置 `default` | `Optional` 参数或方法重载；默认值在 IL 中固化 |
| 复合类型 | 自由嵌套 JSON 对象/数组 | 需定义类型（class/struct/record）或使用匿名类型 |
| 约束表达 | `enum`、`pattern`、`minimum` 等 Schema 关键字 | 需借助属性（Attribute）或手写校验逻辑 |
| 额外字段 | 默认允许，需显式 `additionalProperties: false` | 不存在额外字段，签名固定 |
| 重载与命名 | 工具名唯一，无重载，参数通过 JSON Key 区分 | 支持重载、泛型、ref/out 修饰等 |

### 关键差异说明
- **动态 vs. 静态**：工具参数在模型侧是动态 JSON，需要显式校验；C# 在编译与运行时都有强类型保护。
- **约束表达能力**：JSON Schema 内建的约束可以直接指导模型输出，而 C# 多数约束需额外代码。
- **容错策略**：工具参数通常在运行时进行“宽进严出”校验；C# 则在编译期限制输入，运行时异常较少。

## 实践建议
- **Schema 即契约**：保持 `description` 丰富，帮助模型理解如何填充参数。
- **控制额外字段**：除非明确需要，设置 `additionalProperties: false`，避免模型产生无用参数。
- **反序列化安全**：先校验再映射，避免直接将模型输出绑定到业务对象。
- **类型对齐**：在 C# 中创建与 Schema 对应的 DTO，让后续逻辑沿用强类型模型。
- **测试用例**：为每个工具准备至少一组有效参数与若干非法参数，确保 Schema/验证逻辑覆盖边界情况。

## 结论
OpenAI v1 工具参数把“函数调用”抽象为 JSON Schema 驱动的数据交换。理解这一点有助于在 Live Context 的各项实验中，将模型输出安全地转换为 C# 领域对象，并利用 Schema 提供的约束提升模型调用的稳定性。与标准 C# 参数相比，它们更灵活、更依赖运行时约束，适合跨语言、跨系统的集成场景。