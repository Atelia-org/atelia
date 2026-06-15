# MemoTree 概念与术语

> 用途：收束 MemoTree 当前设计中的核心概念、命名与关系，减少“同一概念多种叫法”造成的理解漂移。
>
> 当前阶段：v0 术语基线。若后续设计变化，请优先更新本文，再回写其他文档。

## 0. 术语基线

MemoTree 当前推荐使用这组核心术语：

| 术语 | 英文/标识符 | 定义 |
|---|---|---|
| MemoTree | `MemoTree` | 面向 LLM Agent 的长期外置记忆系统 |
| Memo Graph | `memo graph` | MemoTree 的本体数据模型；权威真相 |
| Memo Node | `node` | Memo Graph 中的统一节点；可同时拥有正文与子节点 |
| Contains Tree | `contains` tree | 由 `contains` 关系形成的主导航树 |
| Node Title | `title` | 节点标题；主要可读名称 |
| Node Body | `body` | 节点正文；节点内部的主内容载体 |
| Body Block | `block` | Node Body 中可稳定寻址的最小正文单元 |
| Body Split Helper | `SplitBodyBlockByText` | 用前后文本提示把块内切分点稳定化为新的 block 边界 |
| Gist | `gist` | 节点在 `Gist` LOD 下保留的一句话印象 |
| Summary | `summary` | 节点自身正文的摘要，不含子节点内容 |
| Tags | `tags` | 节点上的轻量次索引，不替代主树导航 |
| Pinned Set | `pinned` | 需要优先保留或优先排序的一小组节点 |
| Node Split | `SplitNode` | 把一个节点切成两个相邻 sibling 的结构原语 |
| Node Merge | `MergeNodeWithNextSibling` | 把一个节点与下一个 sibling 合并的结构原语 |
| Window | `Window` | 在 LLM 调用时动态生成的上下文投影视图 |
| Index | `index` | Window 中展示主结构骨架的区域 |
| Node Card | `node card` | Window flatten 区中平铺展示的单节点卡片 |
| Flatten List | `flatten` | Node Card 的平铺列表区域 |
| LOD | `LOD` | 节点在 Window 中的可见内容层级 |

## 1. 概念关系

推荐按下面四层理解 MemoTree：

1. 本体层  
   MemoTree 的权威真相是 Memo Graph。

2. 结构层  
   当前 v0 只公开 `contains` 关系，因此业务上表现为 Contains Tree。

3. 内容层  
   每个 Memo Node 可以有 Title、Body、Gist、Summary、Tags。

4. 视图层  
   Window 把 Contains Tree 投影成 `index + flatten` 布局。

## 2. 命名约定

### 2.1 `Gist` vs `Impression`

当前统一使用 `Gist`。

原因：

- 它与 `Summary`、`Full` 一起更容易形成 LOD 三层对应关系。
- 它已经是 Window/UI 草稿里的主叫法。
- `Impression` 容易让人误以为它是更自由、更主观的注释字段。

因此：

- 概念层统一叫 `Gist`
- API 层也应优先使用 `Gist`

### 2.2 `Body` vs `Content` vs `Text`

当前统一使用：

- `Body`：节点正文这一层概念
- `Body Block`：正文块
- `BodyText`：某次读取或重写时得到的整段文本表示

不推荐在同一层面混用 `content` / `text` / `body` 指向不同东西。

### 2.3 `Index` / `Node Card` / `Flatten`

当前统一使用：

- `Index`：结构骨架区
- `Node Card`：单个展开节点的平铺卡片
- `Flatten`：Node Card 的平铺列表布局

`flatten` 是布局术语，不是节点类型。

### 2.4 `Pinned` vs `Hotset`

当前 v0 优先使用 `Pinned`。

原因：

- `Pinned` 更明确，表示显式提升优先级
- `Hotset` 更像后续可能加入的动态热度概念

因此当前文档里若同时提到二者，推荐理解为：

- `Pinned`：当前已承诺的显式机制
- `Hotset`：未来可能出现的动态排序策略

### 2.5 `Tree` vs `Graph`

当前统一理解为：

- `Memo Graph`：本体层名词
- `Contains Tree`：当前 v0 的主结构投影

不要把“当前只公开一棵树”误解为“本体永远不是图”。

## 3. v0 最重要的定义

### 3.1 Memo Node

Memo Node 是一个统一知识单元：

- 可以只有正文
- 可以只有子节点
- 可以同时有正文和子节点

它不是 `Directory` 或 `File` 的二选一。

### 3.2 Summary

Summary 只概括本节点自己的 Body，不包含子节点内容。

### 3.3 Gist

Gist 是比 Summary 更轻的一句话印象，用于：

- 结构骨架存在感
- 预算紧张时的快速回忆

### 3.4 Tags

Tags 是可选次索引，用于：

- 横向聚合
- 过滤
- 辅助搜索

不用于替代 Contains Tree 的主导航职责。

### 3.5 RewriteBodyText

`RewriteBodyText` 是危险入口：

- 它是“整段重写”，不是默认维护动作
- 它可能导致 Body Block 重建
- 因而可能使旧 block 引用失效

### 3.6 SplitBodyBlockByText

`SplitBodyBlockByText` 是文本层 helper：

- 它不直接改变节点结构
- 它的作用是把“前后文本描述的切分点”变成新的 block 边界
- 后续 `SplitNode` 再消费这些 block 边界

### 3.7 SplitNode

`SplitNode` 是节点层结构原语：

- 它只认结构边界
- 典型边界是 `AfterBodyBlockId` / `BeforeChildId`
- 左节点保留原 `nodeId`
- 右节点获得新 `nodeId`

### 3.8 MergeNodeWithNextSibling

`MergeNodeWithNextSibling` 是节点层结构原语：

- 当前节点保留原 `nodeId`
- 下一个 sibling 被吸收并删除
- 默认应视为对 `gist` / `summary` 有失效风险的重组操作

## 4. 关系图

可用下面这段简化图理解：

```text
MemoTree
  -> Memo Graph
    -> Memo Node
      -> Title
      -> Body
        -> Body Blocks
      -> Gist
      -> Summary
      -> Tags
      -> Children
      -> Node Split / Node Merge
  -> Contains Tree
  -> Window
    -> Index
    -> Flatten List
      -> Node Cards
```

## 5. 术语使用建议

写后续文档或代码注释时，优先遵守：

- 先说 `Memo Node`，再说“像目录/像文件”的使用形态
- 先说 `Body`，不要随手切成 `content/text`
- 先说 `Gist`，不要再和 `Impression` 并用
- 先说 `Pinned`，把 `Hotset` 留给未来动态排序语义
- 先说 `Node Card`，把 `flatten` 保留为布局术语
