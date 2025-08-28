# AGENTS Knowledge Base

## RS2007 (Release tracking file format) 处理指引

来源: 已下载 `docs/ReleaseTrackingAnalyzers.Help.upstream.md` 官方文档。RS2007 表示 `AnalyzerReleases.(Shipped|Unshipped).md` 文件格式/必需标题/表头不符合规范。

### 触发常见原因
- 缺少必须的章节标题: `### New Rules`, `### Removed Rules`, `### Changed Rules`（未使用的章节可以省略, 但使用时标题必须精确匹配）。
- 缺少表头行或分隔行 (必须形如):
  - `Rule ID | Category | Severity | Notes`
  - 下一行: `--------|----------|----------|--------------------` (横线数量可不同, 但列管道数量需匹配)。
- 在 *Unshipped* 文件中放置了额外的 release 标题 (`## Release x.y.z`) —— 不允许。Unshipped 文件只包含一个或多个 `### <Section>` 分组。
- 在 Shipped 文件中修改了历史 release 的条目而未按“Changed Rules”记录变更。
- 多余注释/前缀导致解析不到第一个合法的 `### New Rules` 区块。

### 正确结构示例
`AnalyzerReleases.Unshipped.md`:
```
### New Rules

Rule ID | Category  | Severity | Notes
--------|-----------|----------|--------------------
MT0003  | Formatting| Warning  | Multiline argument list indentation
```
(如果没有待发布规则，可以留空文件或仅保留换行)。

`AnalyzerReleases.Shipped.md`:
```
## Release 0.1.0

### New Rules

Rule ID | Category  | Severity | Notes
--------|-----------|----------|--------------------
MT0001  | Formatting| Warning  | Multiple statements on one line
MT0002  | Formatting| Info     | Initializer element indentation inconsistent
```

### 发布新版本步骤
1. 选择版本号 (如 0.2.0)。
2. 将 `AnalyzerReleases.Unshipped.md` 中全部区块剪切到 `AnalyzerReleases.Shipped.md` 末尾，前面加:
   - `## Release 0.2.0`
   - 维持同样的区块结构顺序: New -> Removed -> Changed (仅存在的才写)。
3. 清空 `AnalyzerReleases.Unshipped.md`。
4. 如果是修改已发布诊断的 Category/Severity/Enabled 状态: 不直接改旧 release 表格；而是在新 release 里添加 `### Changed Rules` 区块列出该 Rule。
5. 删除/弃用规则: 新 release 添加 `### Removed Rules` 区块列出该 Rule。原历史 release 不改。

### 快速排查流程
| 步骤 | 检查项 | 动作 |
|------|--------|------|
| 1 | Unshipped 有 `## Release`? | 移除该标题 |
| 2 | 是否存在 `### New Rules` 表头? | 若无且确有新增规则 -> 添加；若无新增可忽略 |
| 3 | 表头列与分隔列数一致? | 调整 `|` 与 `-` 数量 |
| 4 | 放错到 Shipped 的未发布条目? | 移回 Unshipped |
| 5 | 修改过历史 release 内容? | 恢复原状并在新 release 用 Changed/Removed 记录 |

### 建议规范
- 严格使用四列: Rule ID / Category / Severity / Notes（不要临时加 Description 列，会触发 RS2007）。
- 规则新增时只先写入 Unshipped，不在 Shipped 中预留行。
- 发布脚本/CI 可添加检查: 若 Unshipped 非空且准备打包，则自动 fail，提示整理 release。

### 常见修复脚本 (PowerShell 思路)
- 统计 Unshipped 中是否包含 `Rule ID |` 表头与至少一条 `MT\d+` 行。
- 若 Shipped 内最后一个 release 后没有空行，自动补一行，避免合并时出错。

### 记忆要点
- Unshipped: 不要写 `## Release`。
- Shipped: 不修改历史，新增一节即可。
- 四列格式，是最安全的最小集。
- RS2007 几乎都是“表头 或 标题”不匹配引起。

(此文档供未来会话快速回忆 RS2007 规范)
