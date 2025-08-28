## X0001 OpenParenNewLine 三个格式化变体对比报告（Pure / GuardA / GuardB）

本报告客观汇总当前代码库中三种变体（Pure、GuardA、GuardB）对非 Analyzer / CodeFix 代码的影响，并分析差异、优劣与采纳考量。目标是在无需引入配置项的前提下，择优确定唯一方案。

### 1. 变体简述
- **Pure**: 最激进；对所有符合规则触发条件的参数列表一律格式化。
- **GuardA**: 在 Pure 基础上，对“仅包含单行成员（arguments 或 parameters）”的参数列表豁免，减少不必要换行 / 重排。
- **GuardB**: 在 GuardA 基础上，取消对 Block / Initializer 场景的额外豁免（即仍然应用规则）。在当前代码库的非 Analyzer 代码中尚未体现额外差异（现有文件未触及该特例触发点），因此对非 Analyzer 部分的实际变更 == GuardA。

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
1. 将原本保持紧凑的单行参数列表/调用拆分为多行（括号后换行）。
2. 对含少量简单参数（无嵌套、无长表达式）的方法签名或调用进行重新缩进与折行。
3. 测试输入文件中（`X0001_MultiScenario_Input.cs`）的多场景样本被统一“展开”，增加视觉高度。

回退之后（GuardA/B）：
- 短参数列表保持单行紧凑。
- 统一性略降（出现“部分多行/部分单行”并存）。

### 5. 评估维度对比
| 维度 | Pure | GuardA | GuardB (与 GuardA 相同部分仅列差异) |
|------|------|--------|-------------------------------------|
| 视觉一致性 | 最高：所有触发点统一格式 | 中等：短小参数列表保持紧凑，与多行形成对比 | 与 GuardA 相同（当前代码库） |
| 行数膨胀 | 最大 | 较小 | 相同 |
| Git blame 噪声 | 初次引入更高 | 较低 | 相同 |
| Merge 冲突风险 | 稍高 | 较低 | 相同 |
| 阅读聚焦 | 统一，快速识别模式 | 单/多行对比提供语义暗示 | 相同 |
| 学习成本 | 低（绝对规则） | 中（需理解豁免条件） | 稍高（未来若出现 Block/Initializer 特例） |
| 惊讶度 | 低 | 中 | 可能略高（若将来出现特例） |
| 可扩展性 | 添加豁免会破坏一致性 | 既有豁免框架，可增量调整 | 同 GuardA |
| 对长参数列表可读性 | 良好 | 良好 | 良好 |
| 对短参数列表可读性 | 可能过度占行 | 紧凑 | 紧凑 |

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

### 13. 附录：典型差异代码片段（Pure 独有的附加格式化）
以下摘自 `X0001_Pure_vs_GuardA.diff`，展示 Pure 拆分而 GuardA/GuardB 保持单行的代表性模式：

1) 日志调用括号参数列表
```
- _logger.LogDebug(
-     "Saved parent-children info for {ParentId} with {ChildCount} children",
-     hierarchyInfo.ParentId, hierarchyInfo.ChildCount
- );
+ _logger.LogDebug("Saved parent-children info for {ParentId} with {ChildCount} children",
+     hierarchyInfo.ParentId, hierarchyInfo.ChildCount
+ );
```
特征: 方法名 + 字符串常量 + 少量参数；Pure 将方法名与第一个参数不分行（保持在一行）并移除前置换行，保留后续参数换行缩进；GuardA/GuardB 在这些案例中保留了原始多行头部吗？(原始 main 中为多行；GuardA 回退为多行头部，Pure 收敛为首行紧凑)。

2) Validation 构造器调用
```
- ValidationWarning.Create(
-     "CHILDREN_CHECK_SKIPPED",
-     "Child nodes existence check requires storage access", "HasChildren"
- )
+ ValidationWarning.Create("CHILDREN_CHECK_SKIPPED",
+     "Child nodes existence check requires storage access", "HasChildren"
+ )
```
特征: 工厂方法 + 纯字面量参数；Pure 内联第一个参数，使调用头更短；GuardA/GuardB 保持分行，强调每个参数列对齐。

3) 简短方法/本地函数/委托/record 声明参数列表
```
- void Decl(
-     int a,
-     int b
- ) { }
+ void Decl(int a,
+     int b
+ ) { }

- int Local(
-     int x,
-     int y
- ) => x + y;
+ int Local(int x,
+     int y
+ ) => x + y;

- delegate int D(
-     int a,
-     int b
- );
+ delegate int D(int a,
+     int b
+ );
```
特征: 参数极短（两个简单标识符）；Pure 将第一个参数上移到签名行；GuardA/GuardB 认为该列表是“单行成员集合”而豁免，不触发收敛（保持所有参数各占一行）。

4) 对象/方法调用示例
```
- Target(
-     1,
-     2
- );
+ Target(1,
+     2
+ );

- var obj = new Sample(
-     10,
-     20
- );
+ var obj = new Sample(10,
+     20
+ );
```
特征: 简单数值参数；Pure 将首个实参内联；GuardA/GuardB 保持竖排样式。

5) Lambda 参数列表
```
- var f = (
-     int a,
-     int b
- ) => a + b;
+ var f = (int a,
+     int b
+ ) => a + b;
```
特征: 括号参数列表 + 箭头函数主体极短；Pure 内联首参数。

总结: Pure 模式倾向“把第一参数与方法/类型头放同一行”，并保留后续参数纵向排列；GuardA/GuardB 则在“短列表”场景维持完全竖排的对齐块，以保持视觉分组。差异集中于首行是否拥挤 vs 是否保留一个视觉锚点列。

对选择的影响: 若团队更偏好在扫描签名或调用时立即看到首个参数（提升横向密度），Pure 得分更高；若强调结构列对齐（便于逐行比对、减少首行过长），GuardA/GuardB 更符合。

---
（完）
