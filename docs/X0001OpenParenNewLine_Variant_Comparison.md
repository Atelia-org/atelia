## X0001 OpenParenNewLine 三个格式化变体对比报告（Pure / GuardA / GuardB）

本报告客观汇总当前代码库中三种变体（Pure、GuardA、GuardB）对非 Analyzer / CodeFix 代码的影响，并分析差异、优劣与采纳考量。目标是在无需引入配置项的前提下，择优确定唯一方案。

### 1. 变体简述（纠正说明）
先前描述混淆了 Pure 与 GuardA 的含义，这里修正：

- **Pure (无豁免)**: 只要参数/实参列表是多行且首个参数仍与 '(' 同行，就强制在 '(' 后换行，让首参数独占下一行（形成完全竖排对称块）。
- **GuardA (AllItemsSingleLine 豁免)**: 若列表中所有参数/实参各自都是单行（多行只是因为分隔符换行），允许首参数继续内联，不强制换行；否则退回与 Pure 相同处理。
- **GuardB**: 在 GuardA 基础上，如果这些单行项中存在含 block（`{}`）或 initializer 的结构，则撤销豁免继续强制换行。当前代码库未触发与 GuardA 的差异，因此 GuardB == GuardA（现状）。

### 2. 统计范围与方法
基线: `main` 分支；排除 `src/Analyzers/**/X0001OpenParenNewLine*`。

命令（示例）:
```
git diff --stat main..X0001OpenParenNewLine-Pure -- . ':(exclude)src/Analyzers/**/X0001OpenParenNewLine*'
git diff --stat main..X0001OpenParenNewLine-GuardA -- . ':(exclude)src/Analyzers/**/X0001OpenParenNewLine*'
git diff --stat main..X0001OpenParenNewLine-GuardB -- . ':(exclude)src/Analyzers/**/X0001OpenParenNewLine*'
git diff --stat X0001OpenParenNewLine-Pure..X0001OpenParenNewLine-GuardA -- . ':(exclude)src/Analyzers/**/X0001OpenParenNewLine*'
git diff --stat X0001OpenParenNewLine-GuardA..X0001OpenParenNewLine-GuardB -- . ':(exclude)src/Analyzers/**/X0001OpenParenNewLine*'
```

### 3. 总体差异概览
| 指标 | Pure vs main | GuardA vs main | GuardB vs main | GuardA 相对 Pure | GuardB 相对 GuardA |
|------|--------------|----------------|----------------|------------------|--------------------|
| 受影响非 Analyzer 文件数 | 18 | 16 | 16 | -2 | 0 |
| 插入行 (approx) | 519 | ≈519 | ≈519 | 若干文件回退/减少修改 | 0 |
| 删除行 (approx) | 398 | ≈398 | ≈398 | 同上 | 0 |
| 仅 GuardA/B 与 Pure 不同的文件 | `CowNodeHierarchyStorage.cs`, `VersionedStorageImpl.cs`, `DefaultBusinessRuleValidator.cs`, `DefaultConfigurationValidator.cs`, `X0001_MultiScenario_Input.cs` | - | - | 5 个文件差异 | 无 |

说明: Pure 在上述 5 个文件中产生的附加修改（主要为参数列表或调用处排版调整）被 GuardA/GuardB 回避；GuardA 与 GuardB 在非 Analyzer 代码上无进一步增量差异。

### 4. 差异类型分类
Pure 专有增量（被 GuardA/B 回退的部分）主要表现为：
1. 将“首参数留在首行 + 后续每行一个”的紧凑模式改写为“首参数另起一行” 的完全竖排模式。
2. 对短小参数列表（两个或少量简单标识符、常量、短调用）造成额外垂直高度。
3. 测试样本（`X0001_MultiScenario_Input.cs`）中本可视为“全部单行项”的分布被完全展开。

GuardA/B 回退后：
- 保留紧凑行（首行内联首参数）作为“简单性”信号。
- 仍对内部存在多行结构/复杂项的列表执行换行，保持结构突出。

### 5. 评估维度对比
| 维度 | Pure (无豁免) | GuardA (单行集豁免) | GuardB (当前与 GuardA 等同) |
|------|------|--------|-------------------------------------|
| 视觉一致性 | 最高：全部竖排统一 | 中：短列表紧凑，复杂列表竖排 | 相同 |
| 行数膨胀 | 最大 | 较小 | 相同 |
| Git blame 噪声 | 初次更高 | 较低 | 相同 |
| Merge 冲突风险 | 略高 | 较低 | 相同 |
| 阅读聚焦 | 统一但可能稀释“复杂”信号 | 简单 vs 复杂对比明显 | 相同 |
| 学习成本 | 低（单一规则） | 中（理解豁免） | 略高（未来若触发特例） |
| 惊讶度 | 低 | 中 | 可能略高 |
| 可扩展性 | 后加豁免=风格转弯 | 可迭代微调 | 同 GuardA |
| 对长参数列表 | 良好 | 良好 | 良好 |
| 对短参数列表 | 冗余风险 | 紧凑 | 紧凑 |

### 6. GuardB 的现实有效性
当前非 Analyzer 代码中，GuardB 特有逻辑未触发任何额外差异：
- 尚无“单行 + Block / Initializer 作为唯一成员且被 GuardA 豁免”案例。
- GuardB 当前仅增加规则复杂度，不产生实际收益。

### 7. 风险与影响评估
| 风险类别 | Pure | GuardA / GuardB |
|----------|------|-----------------|
| 初次合入 diff 噪声 | 更高 | 较低 |
| 触及稳定/核心文件次数 | 更多（+5 文件） | 更少 |
| 后续演进破坏性 | 较高（改变=回撤一致性） | 较低（可添加/调整豁免） |
| 贡献者心智负担 | 低 | 中 |

### 8. 决策要点
1. 是否优先“格式绝对统一” (=> Pure)。
2. 是否更重视减少无语义 diff、降低评审噪声 (=> GuardA)。
3. 是否想利用“单行=简单”这一视觉暗示 (=> GuardA)。
4. GuardB 目前无效果增量，仅在未来出现特例时才可能区分。

### 9. 推荐（中立权衡）
- 若优先级: 减噪 / 可迭代 / 保留表达紧凑信号 → GuardA 更均衡。
- 若优先级: 统一性压倒一切 → Pure。
- GuardB 在当前仓库无独特价值，不宜作为最终方案。

结论（当前状态下）：GuardA = GuardB (实际效果) < Pure（改动侵入度）。

### 10. 建议的后续验证动作（可选）
1. 静态扫描：统计潜在会触发 GuardB 差异的模式（单行 lambda / object initializer 作为唯一参数）。
2. 在近期 PR 上并行试验 Pure vs GuardA 的冲突与评审反馈。
3. 若采纳 GuardA：整理规则说明、添加回归测试确保单行短列表不被误拆。

### 11. 附录：Pure 相对 GuardA/GuardB 额外修改文件
```
src/MemoTree.Core/Storage/Hierarchy/CowNodeHierarchyStorage.cs
src/MemoTree.Core/Storage/Versioned/VersionedStorageImpl.cs
src/MemoTree.Core/Validation/DefaultBusinessRuleValidator.cs
src/MemoTree.Core/Validation/DefaultConfigurationValidator.cs
tests/MemoTree.Tests/Analyzers/TestData/X0001_MultiScenario_Input.cs
```

### 12. 若最终选择 GuardA 的清理步骤
1. 删除 GuardB 分支或合并回 GuardA。
2. 在文档（如 README / 贡献指南）中记录“单行短参数列表豁免” rationale。
3. 增加 1~2 个测试覆盖：
   - 单行短参数列表保持不变。
   - 多参数/复杂表达式被强制换行。
4. 考虑添加脚本：检测大规模格式化差异，避免误触发额外风格变动。

### 13. 附录：典型差异代码片段（GuardA vs Pure）
展示 GuardA（紧凑豁免） vs Pure（强制竖排）差异：

1) 日志调用
```
GuardA: _logger.LogDebug("Saved parent-children info for {ParentId} with {ChildCount} children",
         hierarchyInfo.ParentId, hierarchyInfo.ChildCount);
Pure:   _logger.LogDebug(
         "Saved parent-children info for {ParentId} with {ChildCount} children",
         hierarchyInfo.ParentId, hierarchyInfo.ChildCount
      );
```

2) 工厂方法
```
GuardA: ValidationWarning.Create("CHILDREN_CHECK_SKIPPED",
         "Child nodes existence check requires storage access", "HasChildren");
Pure:   ValidationWarning.Create(
         "CHILDREN_CHECK_SKIPPED",
         "Child nodes existence check requires storage access", "HasChildren"
      );
```

3) 短方法声明
```
GuardA: void Decl(int a, int b);
Pure:   void Decl(
         int a,
         int b
      );
```

4) 简单调用/构造
```
GuardA: Target(1,
            2);
Pure:   Target(
         1,
         2
      );
```

5) 简短 lambda
```
GuardA: var f = (int a, int b) => a + b;
Pure:   var f = (
         int a,
         int b
      ) => a + b;
```

总结: Pure = 绝对对称（更统一但更高行数）；GuardA = 保留“简单=紧凑”信号（减少噪声）。当前仓库 GuardB 与 GuardA 无差异。

选择指引: 若团队期望最大统一性与工具“不可辩驳”风格 → Pure；若期望在保持结构突出同时最小化无意义 diff → GuardA。

---
（完）
