# StateJournal Node-Container Frozen Shape Plan

> 目标：为 `DurableOrderedDict` / `DurableText` 这类基于节点链的容器提供“低复杂度的真 frozen shape”。
> 边界条件：
> - 不引入第二套只读容器实现
> - 保留现有读取索引，不让 frozen/mutable 在读取复杂度上分叉
> - 内存收益以“释放编辑态 overlay”为主，能省多少随缘，不强求极限压缩

## 1. 结论

这类节点型容器可以做“真 frozen”，但收益来源应当收敛为：

- 释放 dirty tracking
- 释放 captured originals
- 折叠 committed/current 的可变 overlay

而不是：

- 删除主体节点存储
- 删除读路径依赖的索引
- 引入另一套只读核心结构

换句话说，近期合理目标是：

> **保留读结构，只拆编辑结构。**

## 2. 现状判断

### 2.1 `LeafChainStore`

`LeafChainStore<TKey, TValue, ...>` 当前同时承担两层职责：

- 主体数据层
  - `_keys`
  - `_values`
  - `_nextSequences`
  - `_sequences`
- 编辑 overlay
  - `_dirtyValues`
  - `_dirtyLinks`
  - `_capturedNodes`
  - `_capturedOriginals`

对 frozen 来说，真正可释放的是第二层。

### 2.2 `SkipListCore`

`SkipListCore` 的塔索引虽然不参与持久化，但它服务读取性能：

- `TryGet`
- `ReadAscendingFrom`
- `ReadKeysAscendingFrom`

因此近期不应在 frozen 时删除塔索引，否则 mutable/frozen 会在读取性能上出现类型分叉。

结论：

- 叶链必须保留
- 塔索引也保留
- 只释放 `LeafChainStore` 的编辑 overlay

### 2.3 `TextSequenceCore`

`TextSequenceCore` 的 `_prevByNodeId` 目前既服务编辑，也承担“节点是否仍 live”的快速判断。

因此近期同样不应在 frozen 时删除它。

结论：

- block 链保留
- `_prevByNodeId` 保留
- 只释放 `LeafChainStore` 的编辑 overlay

## 3. 目标 frozen shape

### 3.1 `LeafChainStore` frozen 态

新增一个轻量 frozen sentinel，表达：

- 当前没有 mutable overlay
- `_currentCount == _committedCount`
- dirty bitvectors 长度为 0
- `_capturedOriginals` 为空

但主体节点数组依旧保留。

### 3.2 mutable clean 态

从 frozen 回到 mutable clean 时：

- 恢复 dirty bitvectors 的长度到 `_committedCount`
- `_capturedOriginals` 重新为空
- committed/current 仍然共享同一组主体节点数组

也就是说，恢复的是“可编辑能力”，不是复制一份节点存储。

## 4. 建议 API

在 `LeafChainStore` 增加与 `DictChangeTracker` 类似的一组能力：

```csharp
bool IsFrozen { get; }

void FreezeFromClean<THelper>()
void FreezeFromCurrent<THelper>()
void UnfreezeToMutableClean()
void MaterializeFrozenFromReconstructedCommitted<THelper>()
```

语义：

- `FreezeFromClean`
  - 仅 clean mutable 态可用
  - 冻结值槽
  - 释放 dirty overlay
- `FreezeFromCurrent`
  - 先 `Commit()`
  - 再 `FreezeFromClean()`
- `UnfreezeToMutableClean`
  - 从 frozen 恢复为 clean mutable
  - 只恢复 overlay，不复制主体节点
- `MaterializeFrozenFromReconstructedCommitted`
  - load/replay 路径使用
  - 对 reconstructed committed 值做 `Freeze`
  - 直接进入 frozen 态

## 5. 对上层核心的接法

### 5.1 `SkipListCore`

增加薄封装：

```csharp
void Commit<VHelper>()
void FreezeFromClean<VHelper>()
void FreezeFromCurrent<VHelper>()
void UnfreezeToMutableClean()
void MaterializeFrozenFromReconstructedCommitted<VHelper>()
```

其中：

- `Commit<VHelper>()` 在 arena frozen 时要保留 frozen 态，而不是意外回到 mutable clean
- `SyncCurrentFromCommitted()` 继续表达 mutable 物化
- `MaterializeFrozenFromReconstructedCommitted<VHelper>()` 表达 frozen 物化

塔索引始终保留并重建，读取复杂度不变。

### 5.2 `TextSequenceCore`

同样增加薄封装：

```csharp
void CommitFrozenAware()
void FreezeFromClean()
void FreezeFromCurrent()
void UnfreezeToMutableClean()
void MaterializeFrozenFromReconstructedCommitted()
```

这里 `_prevByNodeId` 仍照常重建，不尝试删除。

## 6. 对容器层的影响

### 6.1 `DurableOrderedDict`

从当前 `soft frozen` 升级为：

- typed ordered dict：真 frozen overlay 释放
- mixed ordered dict：真 frozen overlay 释放 + 保留 refcount 重建
- durobj ordered dict：真 frozen overlay 释放

### 6.2 `DurableText`

从当前 `soft frozen` 升级为：

- `LeafChainStore` overlay 释放
- `_prevByNodeId` 继续保留

因此它会是“部分真 frozen”，但这已经符合近期目标。

## 7. 明确不做

本轮不做：

- 删除 `SkipListCore` 塔索引
- 删除 `TextSequenceCore._prevByNodeId`
- 引入新的只读容器实现
- 改写序列化格式
- 追求 frozen/mutable 间不同的读路径复杂度

## 8. 预期收益与风险

### 8.1 收益

- `DurableOrderedDict` / `DurableText` 不再只是 object-flag 级 soft frozen
- 可释放 dirty/captured overlay 的额外内存
- 为后续对象图级 frozen snapshot 提供更真实的对象级基础

### 8.2 风险

- `LeafChainStore` 是共享底层，改动需要严守“只加 frozen sentinel，不改 wire shape”的边界
- `NeedRelease == true` 的值类型必须在 frozen 入口做一次值槽 `Freeze`
- clean-freeze discard、ForceMutable、ForceFrozen、retry commit 都要回归测试

## 9. 实施顺序

1. 先改 `LeafChainStore`
2. 再接 `SkipListCore`
3. 再接 `TextSequenceCore`
4. 最后把 `DurableOrderedDict` / `DurableText` 的 `FreezeCore` / load materialization 接到真 frozen shape
5. 跑现有 frozen / replay / ordered dict / text 测试

