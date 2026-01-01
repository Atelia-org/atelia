---
docId: "W-0002-spec"
title: "DocGraph v0.1 实现规范文档"
produce_by:
  - "wishes/active/wish-0002-doc-graph-tool.md"
issues:
  - description: "需要添加更多测试用例覆盖边界情况"
    status: "open"
    assignee: "QA"
---

# DocGraph v0.1 - 实现规范文档

> **版本**：v0.1.0  
> **状态**：已实现  
> **目的**：定义v0.1简化版MVP的具体实现约束和验收标准

---

## 1. 规范约定

### 1.1 条款编号规范
条款使用 `[类别-领域-编号]` 格式：
- **类别**：`S`(Structural/结构), `A`(Algorithmic/算法), `F`(Functional/功能), `R`(Resource/资源)
- **领域**：`DOCGRAPH`(核心), `FRONTMATTER`(frontmatter), `PATH`(路径), `ERROR`(错误)
- **编号**：三位数字，按领域分组

### 1.2 严重度关键词
- **MUST**：必须实现，否则功能不完整
- **SHOULD**：建议实现，但可暂时省略
- **MAY**：可选实现，不影响核心功能
- **MUST NOT**：禁止实现，避免功能偏离

### 1.3 引用关系
- 本规范引用 `scope.md` 作为功能边界
- 本规范引用 `api.md` 作为接口定义
- 本规范是实现的**唯一权威约束**

---

## 2. Frontmatter 解析约束

### 2.1 边界检测

**[S-FRONTMATTER-001] MUST**：frontmatter 边界检测必须遵循以下规则：
1. 仅识别文件首部的 `---` 起始标记
2. 识别后续第一个 `---` 作为结束标记
3. 起始标记前允许空白字符，但不允许其他内容
4. 若文件首部不是 `---`，则视为无 frontmatter

**验收测试**：
- 给定文件 `content: "---\ntitle: test\n---\n正文"` → 识别 frontmatter
- 给定文件 `content: "正文\n---\ntitle: test\n---"` → 视为无 frontmatter
- 给定文件 `content: "  \n---\ntitle: test\n---"` → 识别 frontmatter（允许前导空白）

### 2.2 YAML 解析限制

**[S-FRONTMATTER-002] MUST NOT**：frontmatter 中禁止使用 YAML anchor/alias（`&` 和 `*`）。

**理由**：
1. anchor/alias 使错误定位困难（无法确定字段值的实际来源）
2. 别名展开可能导致资源消耗不确定性
3. v0.1 简化优先，避免复杂解析逻辑

**[S-FRONTMATTER-003] MUST**：frontmatter 解析必须实施资源上限约束：

| 约束项 | 上限值 | 超出处理 |
|:-------|:-------|:---------|
| 总字节数 | 64 KB | 产生 `DOCGRAPH_YAML_SIZE_EXCEEDED` 错误 |
| 嵌套深度 | 10 层 | 产生 `DOCGRAPH_YAML_DEPTH_EXCEEDED` 错误 |
| 字符串长度 | 8 KB | 截断并记录警告 |
| 数组长度 | 1024 项 | 产生 `DOCGRAPH_YAML_ARRAY_TOO_LONG` 错误 |

**[S-FRONTMATTER-004] MUST**：YAML 解析错误必须产生明确的错误信息：
1. 语法错误：`DOCGRAPH_YAML_SYNTAX_ERROR`
2. 编码错误：`DOCGRAPH_YAML_ENCODING_ERROR`
3. 结构错误：`DOCGRAPH_YAML_STRUCTURE_ERROR`

### 2.3 字段处理约束

**[S-FRONTMATTER-005] MUST**：frontmatter 采用开放 schema 模式：
1. **核心字段**：严格验证（类型、必填性）
2. **扩展字段**：自由使用，仅做基本类型检查
3. **未知字段**：允许存在，不产生警告

**核心字段定义**：
| 文档类型 | 字段名 | 类型 | 必填 | 验证规则 |
|:---------|:-------|:-----|:-----|:---------|
| Wish文档 | `title` | string | ✅ | 非空字符串 |
| Wish文档 | `produce` | string[] | ✅ | 非空数组，元素非空 |
| 产物文档 | `docId` | string | ✅ | 非空字符串 |
| 产物文档 | `title` | string | ✅ | 非空字符串 |
| 产物文档 | `produce_by` | string[] | ✅ | 非空数组，元素非空 |

**[S-FRONTMATTER-006] MUST**：字段类型不匹配必须产生可预测的错误：
1. 必填字段缺失 → `DOCGRAPH_FRONTMATTER_REQUIRED_FIELD_MISSING` (Error)
2. 字段类型不匹配 → `DOCGRAPH_FRONTMATTER_FIELD_TYPE_MISMATCH` (Error)
3. 字段值无效 → `DOCGRAPH_FRONTMATTER_FIELD_VALUE_INVALID` (Warning)

**类型匹配规则**：
- `string`：接受任何标量值，自动转换为字符串
- `string[]`：必须为 YAML 序列，元素自动转换为字符串
- 空字符串 `""` 视为字段缺失
- 空数组 `[]` 视为有效值

### 2.4 已知扩展字段

**[S-FRONTMATTER-007] SHOULD**：声明以下已知扩展字段，供 Visitor 使用：

| 字段名 | 用途 | 格式约定 |
|:-------|:-----|:---------|
| `defines` | 术语定义 | 对象数组，每个对象包含 `term`(string) 和 `definition`(string) |
| `issues` | 问题跟踪 | 对象数组，每个对象包含 `description`(string)、`status`(string)、`assignee`(string?) |

**[S-FRONTMATTER-008] MUST**：扩展字段缺失时，Visitor 必须优雅降级：
1. 字段不存在 → 跳过该文档的相关处理
2. 字段类型不匹配 → 记录警告，跳过该字段
3. 不因扩展字段问题导致 Visitor 执行失败

---

## 3. 路径处理约束

### 3.1 路径格式规范

**[S-PATH-001] MUST**：`produce` 和 `produce_by` 字段中的路径必须符合以下格式：
1. **相对路径**：相对于 workspace 根目录
2. **分隔符**：统一使用 `/`（跨平台兼容）
3. **禁止**：不得包含 `..` 越界引用
4. **禁止**：不得使用绝对路径或 URI 格式

**有效示例**：
- `"atelia/docs/DocGraph/api.md"`
- `"wishes/active/wish-0001.md"`
- `"./relative/path.md"`（`.` 将被规范化移除）

**无效示例**：
- `"../outside.md"`（越界）
- `"C:\\absolute\\path.md"`（绝对路径）
- `"file:///network/share.md"`（URI）

### 3.2 路径规范化

**[S-PATH-002] MUST**：路径必须进行最小规范化处理：
1. 消解 `.` 当前目录引用
2. 消解 `..` 上级目录引用（在 workspace 边界内）
3. 统一路径分隔符为 `/`
4. 移除末尾的 `/`（目录引用除外）

**规范化算法**：
```csharp
// 伪代码示例
string NormalizePath(string path) {
    // 1. 统一分隔符
    path = path.Replace('\\', '/');
    
    // 2. 分割路径组件
    var components = path.Split('/');
    var result = new List<string>();
    
    // 3. 处理每个组件
    foreach (var component in components) {
        if (component == "." || string.IsNullOrEmpty(component))
            continue;
            
        if (component == "..") {
            if (result.Count > 0)
                result.RemoveAt(result.Count - 1);
            continue;
        }
        
        result.Add(component);
    }
    
    // 4. 重新组合
    return string.Join("/", result);
}
```

**[S-PATH-003] MUST**：规范化后必须检查路径越界：
1. 路径不得为空
2. 路径不得越出 workspace 根目录
3. 越界路径产生 `DOCGRAPH_PATH_OUT_OF_WORKSPACE` 错误

### 3.3 文件存在性检查

**[S-PATH-004] MUST**：关系验证时必须检查目标文件存在性：
1. `produce` 关系：目标文件必须存在
2. `produce_by` 关系：源文件必须存在
3. 文件不存在产生 `DOCGRAPH_RELATION_DANGLING_LINK` 错误

**[S-PATH-005] MUST**：文件存在性检查基于规范化后的路径：
1. 使用规范化路径进行文件系统检查
2. 路径大小写敏感性遵循操作系统规则
3. 符号链接按实际目标文件检查

---

## 4. 文档图构建约束

### 4.1 Root Nodes 扫描

**[A-DOCGRAPH-001] MUST**：Root Nodes 扫描必须遵循以下规则：
1. **扫描目录**：`wishes/active` 和 `wishes/completed`（可配置）
2. **递归扫描**：包括所有子目录
3. **文件过滤**：仅处理 `.md` 后缀文件
4. **frontmatter 过滤**：跳过无 frontmatter 的文件

**[A-DOCGRAPH-002] MUST**：推导字段必须按以下规则计算：

| 字段 | 推导规则 | 边界情况处理 |
|:-----|:---------|:-------------|
| `docId` (Wish) | 从文件名推导：`wish-(\d{4}).md` → `W-$1` | 不匹配模式时使用文件名 stem，记录警告 |
| `status` | 从目录推导：`active/` → `"active"`, `completed/` → `"completed"` | 子目录不影响 status，以根目录名为准 |

**文件名推导示例**：
- `wish-0001.md` → `W-0001`
- `wish-0042.md` → `W-0042`
- `custom-name.md` → `custom-name`（记录警告）

### 4.2 关系提取与闭包构建

**[A-DOCGRAPH-003] MUST**：`produce` 关系提取必须：
1. 从 Wish 文档的 `produce` 字段提取路径数组
2. 对每个路径进行规范化处理
3. 递归追踪所有 `produce` 关系，构建完整闭包
4. 检测循环引用但不禁止，记录信息性警告

**[A-DOCGRAPH-004] MUST**：闭包构建必须保证确定性：
1. 节点按 `FilePath` 字典序排序
2. 边按 `TargetPath` 字典序排序
3. 相同输入必须产生相同输出（幂等性）

**闭包构建算法**：
```csharp
// 伪代码示例
DocumentGraph BuildClosure(IEnumerable<DocumentNode> rootNodes) {
    var allNodes = new Dictionary<string, DocumentNode>();
    var queue = new Queue<DocumentNode>(rootNodes);
    
    while (queue.Count > 0) {
        var node = queue.Dequeue();
        if (allNodes.ContainsKey(node.FilePath))
            continue;
            
        allNodes[node.FilePath] = node;
        
        // 处理 produce 关系
        foreach (var targetPath in node.ProducePaths) {
            var normalizedPath = NormalizePath(targetPath);
            if (!allNodes.ContainsKey(normalizedPath)) {
                var targetNode = LoadOrCreateNode(normalizedPath);
                queue.Enqueue(targetNode);
            }
        }
    }
    
    // 构建最终图结构
    return new DocumentGraph(allNodes.Values.OrderBy(n => n.FilePath).ToList());
}
```

### 4.3 双向链接验证

**[A-DOCGRAPH-005] MUST**：双向链接验证必须检查以下完整性：

| 验证类型 | 检查规则 | 错误码 |
|:---------|:---------|:-------|
| **produce 关系** | 目标文件必须存在且有 frontmatter | `DOCGRAPH_RELATION_DANGLING_LINK` |
| **produce_by 关系** | 目标文件的 `produce_by` 必须包含源文件路径 | `DOCGRAPH_RELATION_MISSING_BACKLINK` |
| **悬空 produce_by** | `produce_by` 引用的源文件必须存在 | `DOCGRAPH_RELATION_DANGLING_BACKLINK` |

**[A-DOCGRAPH-006] MUST**：验证报告必须按以下优先级排序：
1. 严重度：Fatal → Error → Warning → Info
2. 错误码：字母序
3. 源文件路径：字典序
4. 目标文件路径：字典序
5. 行号：升序

---

## 5. 错误处理约束

### 5.1 错误码命名规范

**[S-ERROR-001] MUST**：所有错误码必须使用 `DOCGRAPH_` 前缀，格式：`DOCGRAPH_{CATEGORY}_{DESCRIPTION}`

**错误码分类**：
| 前缀 | 类别 | 示例 |
|:-----|:-----|:-----|
| `FRONTMATTER` | frontmatter 相关 | `DOCGRAPH_FRONTMATTER_REQUIRED_FIELD_MISSING` |
| `YAML` | YAML 解析相关 | `DOCGRAPH_YAML_SYNTAX_ERROR` |
| `RELATION` | 关系验证相关 | `DOCGRAPH_RELATION_DANGLING_LINK` |
| `PATH` | 路径处理相关 | `DOCGRAPH_PATH_OUT_OF_WORKSPACE` |
| `IO` | 文件系统相关 | `DOCGRAPH_IO_DECODE_FAILED` |
| `VISITOR` | Visitor 执行相关 | `DOCGRAPH_VISITOR_EXECUTION_FAILED` |

### 5.2 错误信息格式

**[S-ERROR-002] MUST**：错误信息必须包含三层建议结构：

```yaml
errorCode: "DOCGRAPH_FRONTMATTER_REQUIRED_FIELD_MISSING"
severity: "Error"
message: "必填字段缺失"
filePath: "wishes/active/wish-0001.md"
details:
  line: 3
  column: 5
  snippet: "title: 需求文档模板"
suggestion:
  quick: "第3行缺少produce字段"                    # 5秒能理解
  detail: |                                        # 30秒能修复
    错误：Wish文档必须包含produce字段
    修复：添加produce字段，如：
      produce: ["path/to/api.md", "path/to/spec.md"]
  reference: "https://github.com/.../frontmatter-schema.md"  # 按需深入
```

**[S-ERROR-003] MUST**：错误严重度必须使用视觉标记：

| 严重度 | 视觉标记 | 行动标签 | CLI 颜色 |
|:-------|:---------|:---------|:---------|
| Info | 🔵 | `[FYI]` | 青色 |
| Warning | 🟡 | `[SHOULD FIX]` | 黄色 |
| Error | 🔴 | `[MUST FIX]` | 红色 |
| Fatal | ❌ | `[FATAL]` | 红色（加粗） |

### 5.3 错误聚合与退出码

**[A-ERROR-001] MUST**：必须聚合所有错误后输出报告，不得遇首错即停。

**例外情况**：
1. workspace 根目录无法确定 → 立即退出（Fatal）
2. 配置文件解析失败 → 立即退出（Fatal）
3. 其他 Fatal 错误 → 继续收集但最终退出

**[A-ERROR-002] MUST**：退出码必须遵循以下语义：

| 退出码 | 含义 | 条件 |
|:-------|:-----|:-----|
| 0 | 成功 | 无 Error/Fatal，允许有 Warning/Info |
| 1 | 警告 | 有 Warning，无 Error/Fatal |
| 2 | 错误 | 有 Error，无 Fatal |
| 3 | 致命 | 有 Fatal 错误 |

**[S-DOCGRAPH-NO-WRITE-ON-FATAL] MUST**：当验证阶段存在 Fatal 错误时，不得进入修复执行阶段。
1. 即使指定了 `--fix`，Fatal 错误也会阻止修复操作
2. 修复执行阶段遇到 Fatal 错误必须立即终止，已执行的操作不可回滚（v0.1简化）
3. 除错误报告外不得写入/覆盖任何派生文件

### 5.4 修复模式特定约束

**[A-DOCGRAPH-FIX-DRYRUN] MUST**：`--dry-run` 模式必须执行完整的验证和修复计划生成，但在执行阶段前终止。
1. 必须显示完整的修复计划（文件列表、操作类型、预览内容）
2. 不得产生任何文件系统副作用
3. 退出码应反映验证结果，而非修复执行结果

**[A-DOCGRAPH-FIX-CONFIRM] MUST**：未指定 `--yes` 时，修复执行前必须显示批量预览并获得用户确认。
1. 必须显示修复计划摘要（文件数量、操作类型）
2. 必须提供明确的确认提示（默认 `[Y/n]`）
3. stdin 不可用时必须报错并提示使用 `--yes`

**[S-DOCGRAPH-FIX-SCOPE-V01] MUST**（v0.1 限定）：修复功能仅支持"创建缺失的产物文件"。
1. 不得修改现有文件内容
2. 不得删除或重命名文件
3. 仅当目标文件不存在时才创建

**[A-DOCGRAPH-EXITCODE-FIX] MUST**：`--fix` 模式的退出码语义必须扩展：

| 退出码 | 场景 | 说明 |
|:-------|:-----|:-----|
| 0 | 验证通过 + 修复全部成功（或无需修复） | 修复执行成功或无修复需求 |
| 1 | 验证有警告 + 修复成功 | 警告不影响修复执行 |
| 2 | 验证有错误，未执行修复 | 错误阻止修复执行 |
| 3 | 验证 Fatal 或修复执行失败 | Fatal错误或修复执行中失败 |

**大规模操作安全升级**：
- 当 `--fix` 会创建超过 5 个文件时，确认提示应自动改为 `[y/N]`（默认不执行）
- 用户仍可通过 `--yes` 强制自动执行

---

## 6. Visitor 模式约束

### 6.1 Visitor 接口设计

**[S-VISITOR-001] MUST**：Visitor 接口必须采用整图粒度设计：

```csharp
public interface IDocumentGraphVisitor {
    string Name { get; }
    string OutputPath { get; }
    IReadOnlyList<string> RequiredFields { get; }
    string Generate(DocumentGraph graph);
}
```

**[S-VISITOR-002] SHOULD**：DocumentGraph 应提供便利遍历方法：

```csharp
public class DocumentGraph {
    // ... 其他成员
    
    public void ForEachDocument(Action<DocumentNode> visitor) {
        foreach (var node in AllNodes) {
            visitor(node);
        }
    }
}
```

### 6.2 Visitor 执行约束

**[A-VISITOR-001] MUST**：Visitor 必须独立执行：
1. 每个 Visitor 在独立的 try/catch 块中执行
2. 一个 Visitor 失败不得阻塞其他 Visitor
3. Visitor 失败必须记录 `DOCGRAPH_VISITOR_EXECUTION_FAILED` 错误

**[A-VISITOR-002] MUST**：Visitor 必须优雅处理字段缺失：
1. 依赖字段不存在 → 跳过该文档的相关处理
2. 依赖字段类型不匹配 → 记录警告，跳过该字段
3. 不因单个文档的字段问题导致 Visitor 整体失败

### 6.3 输出文件约束

**[S-VISITOR-003] MUST**：输出文件必须符合以下约定：
1. **文件后缀**：`.gen.md`
2. **文件声明**：文件开头必须包含机器生成声明
3. **时间戳**：必须包含生成时间
4. **再生成命令**：必须包含再生成命令提示

**文件头模板**：
```markdown
<!-- 本文档由DocGraph工具自动生成，手动编辑无效 -->
<!-- 生成时间：2026-01-01 12:34:56 UTC -->
<!-- 再生成命令：docgraph generate {visitor-name} -->
```

**[S-VISITOR-004] MUST**：输出文件命名必须：
1. 优先使用 Visitor 声明的 `OutputPath`
2. 默认约定：`{VisitorName}.gen.md`
3. 路径必须在 workspace 内
4. 不得覆盖非 `.gen.md` 文件

### 6.4 内置 Visitor 规范

**[F-VISITOR-001] MUST**：v0.1 必须包含以下内置 Visitor：

#### 6.4.1 术语表生成器 (GlossaryVisitor)

**功能**：从 `defines` 字段生成紧凑术语表

**输出格式**：
```markdown
# 术语表

## api.md
- **DocumentNode**：文档图中的节点，表示一个文档
- **Produce关系**：wish文档到产物文档的链接

## spec.md
- **Frontmatter**：文档头部的YAML元信息块
- **Visitor模式**：遍历文档图生成汇总的访问者模式
```

**排序规则**：
1. 文档按 `FilePath` 字典序排序
2. 术语按 `term` 字典序排序

#### 6.4.2 问题汇总器 (IssueAggregator)

**功能**：从 `issues` 字段生成分类问题表格

**输出格式**：
```markdown
# 问题汇总

## 统计概览
- 总问题数：15
- 按状态分布：
  - open：8个
  - in-progress：4个
  - resolved：3个

## open 的问题
| 问题描述 | 来源文档 | 负责人 |
|:---------|:---------|:-------|
| API设计需要细化 | [api.md](api.md) | 张三 |
| 性能优化待验证 | [perf.md](perf.md) | 李四 |

## in-progress 的问题
...
```

**分组规则**：
1. 按 `status` 字段分组
2. 每组内按 `sourceDocument` 字典序排序
3. 支持的状态值：`open`, `in-progress`, `resolved`, `closed`

---

## 7. 写入策略约束

### 7.1 自动维护约束

**[A-WRITE-001] MUST**：自动创建 frontmatter 必须遵循极简原则：

**模板内容**：
```yaml
---
docId: "{从文件名推导}"
title: "待填写"
produce_by: ["{源文档路径}"]
---
```

**推导规则**：
1. `docId`：使用文件名 stem（不含扩展名）
2. `title`：固定占位符 "待填写"
3. `produce_by`：包含触发创建的源文档路径

**[A-DOCGRAPH-WRITE-EXPLICIT] MUST**：任何会修改工作区文件的行为必须在显式开关（`--fix`）开启时才允许。
1. `--fix` 参数同时表达"修复意图"和"写入权限"
2. 默认模式（无 `--fix`）：只验证，不写入
3. 交互模式（`--fix` 无 `--yes`）：显示批量预览，需要用户确认
4. 自动模式（`--fix --yes`）：跳过确认，自动执行

### 7.2 原子写入约束

**[S-WRITE-001] MUST**：所有文件写入必须使用原子替换策略：

**写入流程**：
```
1. 写入临时文件：{output}.tmp
2. 验证临时文件完整性
3. 原子重命名为目标文件
4. 清理临时文件（如果失败）
```

**[S-WRITE-002] MUST**：写入失败必须：
1. 清理所有临时文件
2. 不污染现有文件
3. 产生明确的错误信息
4. 退出码为 3（Fatal）

### 7.3 副作用控制

**[S-WRITE-003] MUST NOT**：不得修改现有 frontmatter 内容。

**允许的操作**：
1. 创建缺失的 frontmatter（全新文件）
2. 创建 `.gen.md` 汇总文档
3. 创建验证报告

**禁止的操作**：
1. 修改现有 frontmatter 字段值
2. 添加或删除现有 frontmatter 字段
3. 修改文档正文内容

---

## 8. 性能与资源约束

### 8.1 内存使用约束

**[R-PERF-001] SHOULD**：内存使用应在合理范围内：

| 场景 | 预估内存 | 可接受上限 |
|:-----|:---------|:-----------|
| 100 文档 | ~10 MB | 50 MB |
| 1000 文档 | ~50 MB | 200 MB |
| 5000 文档 | ~200 MB | 500 MB |

**[R-PERF-002] MUST**：必须避免内存泄漏：
1. 及时释放不再使用的资源
2. 使用 `using` 语句管理文件流
3. 避免大型对象长期驻留

### 8.2 执行时间约束

**[R-PERF-003] SHOULD**：执行时间应在合理范围内：

| 操作 | 复杂度 | 预估时间（1000文档） |
|:-----|:-------|:-------------------|
| 文件扫描 | O(n) | ~100ms |
| YAML 解析 | O(n × m) | ~500ms |
| 图构建 | O(n + e) | ~50ms |
| 闭包计算 | O(n + e) | ~50ms |
| Visitor 生成 | O(n × v) | ~100ms |
| **总计** | — | ~800ms |

**[R-PERF-004] MUST**：必须提供进度反馈：
1. 文件数 > 10：显示进度条
2. 文件数 > 100：显示当前处理文件名
3. 长时间操作：显示预计剩余时间

### 8.3 并发与线程安全

**[R-PERF-005] MUST NOT**：v0.1 不得使用并发处理。

**理由**：
1. 简化实现复杂度
2. 避免线程同步问题
3. v0.1 性能需求不高

**[R-PERF-006] MUST**：必须保证单线程下的线程安全：
1. 避免共享可变状态
2. 使用不可变数据结构
3. 纯函数设计

---

## 9. 测试约束

### 9.1 测试覆盖要求

**[F-TEST-001] MUST**：必须实现以下测试组：

| 测试组 | 最小用例数 | 覆盖重点 |
|:-------|:-----------|:---------|
| Schema/类型 | 5 | 缺字段、错类型、空值、重复、未知字段 |
| 路径/闭包 | 5 | `./`、`../`、越界、大小写、循环引用 |
| 报告/幂等 | 3 | 同输入同输出、错误排序、JSON schema |
| Visitor 输出 | 2 | 术语表、问题汇总格式正确 |
| 错误处理 | 4 | 错误码、严重度、建议、退出码 |

### 9.2 测试数据约定

**[S-TEST-001] MUST**：测试数据必须：
1. 使用独立的测试目录结构
2. 包含边界情况示例
3. 有明确的预期输出
4. 支持快照测试（snapshot testing）

**测试目录结构**：
```
tests/
├── data/
│   ├── valid/
│   │   ├── wish-0001.md
│   │   └── api.md
│   ├── invalid/
│   │   ├── missing-field.md
│   │   └── invalid-path.md
│   └── edge-cases/
│       ├── circular-reference/
│       └── large-frontmatter/
├── expected/
│   ├── glossary.gen.md.snapshot
│   └── validation-report.json.snapshot
└── TestDocGraph.cs
```

### 9.3 集成测试要求

**[F-TEST-002] SHOULD**：应该包含端到端集成测试：
1. 完整流程测试（扫描→验证→生成）
2. CLI 接口测试
3. 错误恢复测试
4. 性能基准测试

---

## 10. 非目标清单（v0.1 明确不做）

### 10.1 技术复杂性规避

**[F-NOT-001] MUST NOT**：不解析 Markdown 正文：
- 忽略 `---` 之后的所有内容
- 不提取正文中的链接、标题、代码块等
- 不分析正文语义

**[F-NOT-002] MUST NOT**：不处理文档链接：
- 不提取 `[text](path.md)` 格式的链接
- 不验证链接目标是否存在
- 不分析链接关系

**[F-NOT-003] MUST NOT**：不自动修复双向链接：
- 检测缺失的反向链接但仅报告
- 不自动添加或修改 `produce_by` 字段
- 不提供一键修复功能

### 10.2 高级功能规避

**[F-NOT-004] MUST NOT**：不实现增量扫描：
- 每次全量扫描
- 无文件监听机制
- 无缓存机制

**[F-NOT-005] MUST NOT**：不实现并行处理：
- 单线程执行
- 无任务并行化
- 无流水线优化

**[F-NOT-006] MUST NOT**：不分析文档语义：
- 不理解文档内容含义
- 不进行分类或标签建议
- 不提供智能摘要

### 10.3 业务逻辑简化

**[F-NOT-007] MUST NOT**：不管理文档状态：
- 不跟踪文档版本历史
- 不检测文档冲突
- 不分配维护任务

**[F-NOT-008] MUST NOT**：不提供高级路径处理：
- 不规范化符号链接
- 不处理网络路径
- 不解析 URI 格式

---

## 11. 验收标准矩阵

### 11.1 功能验收标准

| ID | 验收项 | 测试方法 | 通过标准 |
|:---|:-------|:---------|:---------|
| F-001 | 扫描 wish 目录 | 给定测试目录，验证能正确识别 Wish 文档 | 识别所有 `.md` 文件，跳过无 frontmatter 的 |
| F-002 | 推导字段计算 | 给定标准和非标准文件名，验证推导逻辑 | 标准文件正确推导，非标准文件记录警告 |
| F-003 | produce 关系提取 | 给定包含 produce 字段的文档，验证关系提取 | 正确提取所有路径，规范化处理 |
| F-004 | 文档图闭包构建 | 给定多层 produce 关系，验证闭包完整性 | 包含所有可达文档，处理循环引用 |
| F-005 | 双向链接验证 | 给定完整和不完整的关系，验证检查逻辑 | 正确报告悬空引用和缺失反向链接 |
| F-006 | 术语表生成 | 给定包含 defines 字段的文档，验证输出格式 | 生成正确的紧凑列表，排序稳定 |
| F-007 | 问题汇总生成 | 给定包含 issues 字段的文档，验证输出格式 | 生成正确的分类表格，统计准确 |

### 11.2 质量验收标准

| ID | 验收项 | 测试方法 | 通过标准 |
|:---|:-------|:---------|:---------|
| Q-001 | 幂等性 | 相同输入运行两次，比较输出 | 输出字节完全相同 |
| Q-002 | 错误处理 | 注入各种错误，验证处理逻辑 | 不崩溃，错误信息明确，退出码正确 |
| Q-003 | 性能基准 | 使用 1000 文档测试集，测量执行时间 | 总时间 < 2秒，内存 < 200MB |
| Q-004 | 测试覆盖 | 运行测试套件，计算覆盖率 | 核心逻辑 ≥ 80% |

### 11.3 用户体验标准

| ID | 验收项 | 测试方法 | 通过标准 |
|:---|:-------|:---------|:---------|
| U-001 | 错误信息可读性 | 查看错误输出，评估可理解性 | 5秒内能理解问题，30秒内知道如何修复 |
| U-002 | 进度反馈 | 处理大量文件时观察反馈 | 有进度指示，无"黑屏等待" |
| U-003 | 输出文件易读性 | 查看生成的 `.gen.md` 文件 | 格式清晰，有导航，包含再生成提示 |
| U-004 | CLI 帮助信息 | 运行 `docgraph --help` | 命令结构清晰，示例有用 |

---

## 12. 变更记录

| 版本 | 日期 | 作者 | 变更说明 |
|:-----|:-----|:-----|:---------|
| 0.1.0-draft | 2026-01-01 | 刘德智 | 基于畅谈会共识创建初始草案 |

---

## 13. 引用文档

1. **[scope.md](./scope.md)** - 功能边界文档
2. **[api.md](./api.md)** - API 设计文档
3. **[future/spec.md](../future/spec.md)** - 完整版规范（仅参考）

---

**使用说明**：本文档是 v0.1 实现的**唯一权威约束**。任何实现必须遵循本文档的所有 MUST 条款，SHOULD 条款应尽可能实现，MAY 条款可根据实际情况选择。