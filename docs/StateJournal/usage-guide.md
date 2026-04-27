# StateJournal Usage Guide for LLM Agents

> 用途：给即将使用 `src/StateJournal/` 的 LLM Agent 一次性加载。
> 定位：这是使用者手册；维护者事实地图见 [`memory-notebook.md`](memory-notebook.md)。
> 当前阶段：早期实验项目，API 可按未来需要继续重构。本文只描述当前主线。

---

## 0. 一句话心智模型

StateJournal 是一个**可持久化的增量对象图工作态引擎**。

使用时把它想成三层：

| 层 | 类型 | 使用者视角 |
|---|---|---|
| Repository | `Repository` | 一个进程独占打开的状态仓库，管理 branch、segment 文件、repo lock |
| Workspace | `Revision` | 一个 branch 当前 checkout 出来的可编辑对象图会话 |
| Durable Objects | `DurableDict` / `DurableDeque` / `DurableOrderedDict` / `DurableText` | 被 `Revision` 管理、可提交、可重载的对象图节点 |

最常用路径：

```text
Repository.Create/Open
  -> CreateBranch/CheckoutBranch 得到 Revision
  -> Revision.Create* 创建 DurableObject
  -> 修改 DurableObject
  -> Repository.Commit(root)
  -> 之后 Open + CheckoutBranch 恢复 Revision.GraphRoot
```

不要把 `CommitTicket` / `SizedPtr` 当业务 ID。它们是持久化层的 frame 地址凭据。

---

## 1. Quick Start

### 1.1 创建仓库、写入、提交

```csharp
using Atelia.StateJournal;

var repoDir = "/tmp/my-state-journal";

using var repo = Repository.Create(repoDir).Value;
var rev = repo.CreateBranch("main").Value;

var root = rev.CreateDict<string>();
root.Upsert("title", "first note");
root.Upsert("count", 1);
root.Upsert("enabled", true);

var items = rev.CreateDeque<string>();
items.PushBack("alpha");
items.PushBack("beta");
root.Upsert("items", items);

var outcome = repo.Commit(root).Value;
```

要点：

- `Repository.Create(path)` 要求目标目录不存在或为空。
- 新 repo 不会自动创建 `main`，必须显式 `CreateBranch("main")`。
- `Repository.Commit(root)` 会从 `root` 开始遍历可达对象图，只持久化可达对象。
- `root` 必须属于这个 repo 管理的 `Revision`。

### 1.2 重新打开并读取

```csharp
using Atelia.StateJournal;

using var repo = Repository.Open(repoDir).Value;
var rev = repo.CheckoutBranch("main").Value;

var root = (DurableDict<string>)rev.GraphRoot!;

var title = root.GetOrThrow<string>("title");
var count = root.GetOrThrow<int>("count");
var items = root.GetOrThrow<DurableDeque<string>>("items");

var first = items.GetAt(0);
```

要点：

- `CheckoutBranch("main")` 会按需加载该 branch 的 `Revision`。
- `rev.GraphRoot` 是最近一次 commit 使用的 root；unborn branch 上为 `null`。
- 当前主线是全量加载，不是 lazy loading。

### 1.3 修改后再次提交

```csharp
var root = (DurableDict<string>)rev.GraphRoot!;

root.Upsert("count", root.GetOrThrow<int>("count") + 1);
root.Remove("enabled");

repo.Commit(root).Value;
```

要点：

- 修改发生在内存中的 `Revision` 工作态里。
- 成功 commit 后，dirty 对象变为 clean，branch head 前进。
- 失败时常见结果是 `AteliaResult` failure；不要假设异常是唯一错误通道。

---

## 2. Repository 工作流

### 2.1 创建与打开

```csharp
using var repo = Repository.Create(repoDir).Value;
using var reopened = Repository.Open(repoDir).Value;
```

规则：

- 一个 repository 目录通过 `state-journal.lock` 做进程独占锁。
- `Repository.Open` 只恢复 repo 元数据；具体对象图在 `CheckoutBranch` 时加载。
- `Repository` 不是线程安全的对象图编辑器；内部锁只保护 repo 元数据。
- 出现 branch metadata CAS 失败后，当前 `Repository` 实例会进入 poisoned 状态，应 dispose 并 reopen。

### 2.2 Branch

```csharp
var main = repo.CreateBranch("main").Value;
var feature = repo.CreateBranch("feature", "main").Value;
var checkedOut = repo.CheckoutBranch("main").Value;
```

规则：

- `CreateBranch(name)` 创建 unborn branch，`HeadId == default`，`GraphRoot == null`。
- `CreateBranch(name, fromBranch)` 从源 branch 的**已提交 HEAD**派生，不复制未提交工作态。
- branch 名只允许 ASCII 字母、数字、`. _ - /`，必须以字母或数字开头，不能以 `/ . -` 结尾。
- 可用 `Repository.ValidateBranchName(name)` 预检。

### 2.3 Segment

Repository 会维护：

```text
{repoDir}/
  state-journal.lock
  refs/branches/*.json
  recent/*.sj.rbf
  archive/*/*.sj.rbf
```

使用者通常不直接操作 segment。需要知道：

- 默认 rotation threshold 是 2GB。
- `SetRotationThreshold(long)` 可调整阈值；测试中常设极小值触发轮换。
- `MaintainSegmentLayout()` 是 best-effort 归档旧 recent segment，不是 commit 事务的一部分。

---

## 3. Revision 与对象图规则

`Revision` 是一个已打开的可编辑对象图会话。公开工厂入口都在 `Revision` 上：

```csharp
var dict = rev.CreateDict<string, int>();          // typed dict
var mixed = rev.CreateDict<string>();              // mixed dict
var deque = rev.CreateDeque<string>();             // typed deque
var mixedDeque = rev.CreateDeque();                // mixed deque
var ordered = rev.CreateOrderedDict<int, string>(); // typed ordered dict
var mixedOrdered = rev.CreateOrderedDict<int>();   // mixed ordered dict
var text = rev.CreateText();                       // DurableText
```

关键规则：

- 新建对象会自动绑定到当前 `Revision`，分配 `LocalId`，状态为 `TransientDirty`。
- 从磁盘打开的对象状态为 `Clean`。
- `DurableState` 只表达 dirty / clean / detached 生命周期；`IsFrozen` 是与之正交的只读语义。
- 业务代码应从 `Revision.Create*` 创建对象；`Durable.*` 工厂多用于内部和测试，直接创建的对象可能未绑定 `Revision`。
- 对象一经绑定，不可转移到其他 `Revision`。
- 不能把其他 `Revision` 的 `DurableObject` 存入当前对象图。
- commit 只保留从 graph root 可达的对象；不可达对象会被 sweep，并进入 `Detached`。
- detached 对象不能重新挂回对象图，也不能作为 root 提交。

典型错误：

```csharp
var rev1 = repo.CreateBranch("a").Value;
var rev2 = repo.CreateBranch("b").Value;

var root = rev1.CreateDict<string, DurableDict<string, int>>();
var foreign = rev2.CreateDict<string, int>();

root.Upsert("bad", foreign); // InvalidOperationException
```

### 3.1 Fork committed state

当前只有 `DurableDict` 路线公开支持 fork：

```csharp
var template = rev.CreateDict<string, int>();
template.Upsert("a", 1);
repo.Commit(template).Value;

var draft = template.ForkCommittedAsMutable();
draft.Upsert("b", 2);
```

语义要点：

- fork 会创建同一 `Revision` 内的**新对象**和**新 `LocalId`**。
- fork 复制的是 source 的 **committed state**，不是 working state。
- source 上未提交的普通修改会被忽略；fork 看到的是上次 commit 的内容。
- `DurableObject` 子引用是浅拷贝；fork parent 不会深拷贝整棵子图。
- fork 后如果对象还没挂到 root，可在后续 commit 中被 sweep 掉。

当前不支持：

- `DurableDeque` / `DurableOrderedDict` / `DurableText` 没有 public `ForkCommittedAsMutable()`。

### 3.2 Freeze / frozen

`Freeze()` 是 `DurableObject` 基类上的对象级能力；当前 `DurableDict` 与 `DurableDeque` 路线已正式支持：

```csharp
var deque = rev.CreateDeque<int>();
deque.PushBack(1);

deque.Freeze();
bool frozen = deque.IsFrozen; // true

deque.PushBack(2); // ObjectFrozenException
```

语义要点：

- frozen 对象可读，不可修改。
- clean/tracked 对象执行 `Freeze()` 后，即使内容没变，下一次 commit 也会写入 object flags，保证 reopen 后仍是 frozen。
- dirty 对象执行 `Freeze()` 会把当前 working state 固化为 frozen snapshot，并要求下一次保存走 rebase。
- dirty frozen 且尚未提交的 source 不能再 `ForkCommittedAsMutable()`；先 commit，再 fork。

当前不支持：

- `DurableOrderedDict<...>` / `DurableText` 上调用 `Freeze()` 会抛 `NotSupportedException`。
- `DurableDeque<T>` / `DurableDeque` 当前仍不支持 public `ForkCommittedAsMutable()`。

---

## 4. 容器选择

### 4.1 Typed vs Mixed

优先选 typed，只有确实需要异构值时才选 mixed。

| 需求 | 推荐 |
|---|---|
| key/value schema 明确 | `DurableDict<TKey, TValue>` |
| 队列元素类型明确 | `DurableDeque<T>` |
| 需要按 key 有序遍历 / range query | `DurableOrderedDict<TKey, TValue>` |
| 同一容器里要混放 int/string/ByteString/bool/DurableObject | `DurableDict<TKey>` / `DurableDeque` / `DurableOrderedDict<TKey>` |
| 需要稳定 block ID 的文本编辑 | `DurableText` |

Typed 容器的优点：

- 编译期类型更清晰。
- API 更直接。
- 更适合作为长期 schema。

typed 容器里的字符串类型选择：

- `string` 是值语义 payload-backed 字符串，适合作为普通 typed key/value。
- `ByteString` 是值语义 payload-backed 字节串，适合 mixed 容器里的 blob/opaque bytes 值。
- `Symbol` 是显式 symbol-backed facade，适合需要表达“身份/驻留字符串”语义的字段。
- mixed 容器里的 `Symbol` 走 intern 池；mixed 容器里的 `string` / `ByteString` 走独立 owned payload，不再 silent intern。

例如：

```csharp
var dict = rev.CreateDict<string, string>();
dict.Upsert("prompt", "a long one-off message");
```

Mixed 容器的优点：

- 适合 LLM Agent 原型期的“对象属性袋”。
- 可通过 `ValueKind` 检查实际存储类型。
- 支持 `OfInt32` / `OfString` / `Of<T>()` 等 exact typed view。

### 4.2 支持的 mixed value 类型

Mixed 容器主要支持：

```text
bool
Symbol
string
ByteString
DurableObject
double / float / Half
ulong / uint / ushort / byte
long / int / short / sbyte
null
```

注意：

- `DateTime`、任意 POCO、任意 enum 当前不是 mixed value 直接支持类型。
- DurableObject 子类型可以通过泛型便捷 API 读取，如 `GetOrThrow<DurableDict<string>>()`。
- `Of<DurableDict<string>>()` 这种 DurableObject 子类型 view 不支持；用泛型 `Get/TryGet/GetOrThrow`。
- `ByteString.Empty` 表示一个具体的空字节串（`ValueKind.Blob`），不等价于 mixed `null`。
- `new ByteString(byte[])` 会 defensive clone 外部可变数组；高级零拷贝入口是 `ByteString.FromTrustedOwned(byte[])`。
- 但默认 mixed `Upsert` / deque 写入仍会在 face 入池时再次 clone，端到端零拷贝需显式 trusted 入池路径：
    - `DurableDict<TKey>` / `DurableOrderedDict<TKey>`：`UpsertTrustedBlob(key, value)`。
    - `DurableDeque`：`PushFrontTrustedBlob(value)`、`PushBackTrustedBlob(value)`、`TrySetFrontTrustedBlob(value)`、`TrySetBackTrustedBlob(value)`、`TrySetAtTrustedBlob(index, value)`。
    - 这些 trusted API 要求传入由 `ByteString.FromTrustedOwned(byte[])` 构造的值，或具备等价的“caller 独占 + 后续不可变”契约；转交后继续 mutate 原数组会静默破坏 StateJournal 内部状态。

### 4.3 null 语义

所有容器类型参数都有 `where T : notnull`，但这不表示引用类型值不能存 `null`。

```csharp
var typedString = rev.CreateDict<string, string>();
typedString.Upsert("nickname", null);
typedString.Get("nickname", out string? nickname); // nickname == "", issue == None

var typedSymbol = rev.CreateDict<string, Symbol>();
typedSymbol.Upsert("owner", null);
typedSymbol.Get("owner", out Symbol owner); // owner.IsNull

var mixed = rev.CreateDict<string>();
mixed.Upsert<string>("nickname", null);
mixed.TryGet("nickname", out string? mixedNickname); // mixedNickname == null
mixed.Upsert("child", (DurableObject?)null);
mixed.TryGet("child", out DurableDict<string, int>? child); // child == null
```

也就是说：typed `string` 会把 `null` 规范化为空字符串；mixed `string`、typed `Symbol` 和 mixed `DurableObject` 则保留显式 null facade；`ByteString` 是值类型，没有 `null` 概念，`default(ByteString)` 等价于 `ByteString.Empty`。

对值类型，如 `int`，`TValue?` 在 `where TValue : notnull` 下只是 nullable annotation，不是 `Nullable<T>` 包装。

---

## 5. Dict API

### 5.1 Typed Dict

```csharp
var users = rev.CreateDict<string, int>();

users.Upsert("alice", 10);
users.Upsert("bob", 20);

if (users.Get("alice", out int score) == GetIssue.None) {
    score += 1;
}

users.Remove("bob");

foreach (var key in users.Keys) {
    // unordered
}
```

常用成员：

- `Upsert(key, value)` -> `UpsertStatus.Inserted/Updated`
- `Get(key, out value)` -> `GetIssue`
- `ContainsKey(key)`
- `Remove(key)`
- `Count`
- `Keys`
- `ForkCommittedAsMutable()`

也可用 extension：

```csharp
var value = users.GetOrThrow("alice");
var fallback = users.GetOr("missing", 0);
```

### 5.2 Mixed Dict

```csharp
var model = rev.CreateDict<string>();

model.Upsert("title", "Document");
model.Upsert("wordCount", 1234);
model.Upsert("visible", true);

string? title = model.GetOrThrow<string>("title");
int wordCount = model.GetOrThrow<int>("wordCount");

model.OfString.Upsert("title", "New title");
model.OfInt32.Upsert("wordCount", 2048);

if (model.TryGetValueKind("title", out var kind)) {
    // kind == ValueKind.String
}
```

也可 fork：

```csharp
var snapshot = model.ForkCommittedAsMutable();
snapshot.Upsert("title", "Draft");
```

`GetIssue` 的常见值：

| 值 | 含义 |
|---|---|
| `None` | 成功，out value 有意义 |
| `NotFound` | key 不存在 |
| `TypeMismatch` | key 存在，但请求类型不匹配 |
| `UnsupportedType` | 请求类型不是 mixed 容器支持的类型 |
| `LoadFailed` | DurableObject 引用加载失败，通常表示持久化损坏或内部错误 |

---

## 6. Deque API

### 6.1 Typed Deque

```csharp
var deque = rev.CreateDeque<int>();

deque.PushBack(2);
deque.PushFront(1);
deque.PushBack(3);

int first = deque.GetFrontOrThrow();
int last = deque.GetBackOrThrow();
int middle = deque.GetAt(1);

deque.TrySetAt(1, 20);
deque.TryPopFront(out int popped);
```

常用成员：

- `PushFront(value)` / `PushBack(value)`
- `PeekFront(out value)` / `PeekBack(out value)`
- `GetAt(index, out value)`
- `TrySetAt(index, value)` / `TrySetFront(value)` / `TrySetBack(value)`
- `PopFront(out value)` / `PopBack(out value)`
- `Count`

空队列：

- `PeekFront/PeekBack/PopFront/PopBack` 返回 `GetIssue.NotFound`。
- throwing helper 会抛 `InvalidOperationException`。

越界：

- `GetAt` 返回 `GetIssue.OutOfRange`。
- `TrySetAt` 返回 `false`。

### 6.2 Mixed Deque

```csharp
var deque = rev.CreateDeque();

deque.PushBack("tail");
deque.PushFront(42);

deque.TryPeekFront(out int n);
deque.TryPeekBack(out string? s);

deque.OfInt32.TrySetFront(7);
deque.OfString.TrySetBack("new-tail");
```

DurableObject 子类型用泛型方法族：

```csharp
var child = rev.CreateDict<int, int>();
deque.PushBack(child);

deque.TryGetAt<DurableDict<int, int>>(0, out var loadedChild);
var sameChild = deque.GetBack<DurableDict<int, int>>();
```

---

## 7. Ordered Dict API

OrderedDict 按 key 自然序维护数据，适合 index、时间线、range query。

### 7.1 Typed Ordered Dict

```csharp
var index = rev.CreateOrderedDict<int, string>();

index.Upsert(30, "c");
index.Upsert(10, "a");
index.Upsert(20, "b");

var keys = index.GetKeys(); // [10, 20, 30]
var page = index.ReadAscendingFrom(15, 2); // 20, 30
```

常用成员：

- `Upsert(key, value)`
- `Get(key, out value)` / `TryGet(key, out value)`
- `Remove(key)`
- `GetKeys()`
- `ReadAscendingFrom(minInclusive, maxCount)`

### 7.2 Mixed Ordered Dict

```csharp
var index = rev.CreateOrderedDict<int>();

index.Upsert(1, 42);
index.Upsert(2, "hello");

index.TryGet<int>(1, out var number);
index.TryGet<string>(2, out var text);

var keys = index.GetKeys();
var nextKeys = index.GetKeysFrom(2, 10);
```

Mixed ordered dict 有 `GetKeysFrom`，但没有 typed ordered dict 的 `ReadAscendingFrom` 值页读取接口。

---

## 8. DurableText

`DurableText` 是带稳定 block ID 的持久化文本容器。它适合 LLM Agent 做“按块编辑”，避免靠内容复述定位。

```csharp
var text = rev.CreateText();

var intro = text.Append("intro");
var body = text.Append("body");
var middle = text.InsertAfter(intro, "middle");

text.SetContent(body, "updated body");
text.Delete(middle);

var blocks = text.GetAllBlocks();
```

常用成员：

- `Append(content)` / `Prepend(content)`
- `InsertAfter(blockId, content)` / `InsertBefore(blockId, content)`
- `SetContent(blockId, newContent)`
- `Delete(blockId)`
- `GetBlock(blockId)`
- `GetAllBlocks()`
- `GetBlocksFrom(startBlockId, maxCount)`
- `LoadBlocks(ReadOnlySpan<string>)`
- `LoadText(string)`

注意：

- block ID 创建后稳定，删除后不会复用。
- `LoadBlocks` / `LoadText` 仅空文本可用。
- `LoadText` 按 `\n` 分块；`\r\n` 中的 `\r` 不会自动剥离。

---

## 9. Commit 语义

### 9.1 Graph Root

`Repository.Commit(root)` 的 `root` 定义了本次 commit 的对象图边界。

```csharp
var root = rev.CreateDict<string>();
var child = rev.CreateDict<string, int>();

root.Upsert("child", child);
repo.Commit(root).Value; // child 可达，会持久化

root.Remove("child");
repo.Commit(root).Value; // child 不可达，会被 sweep/detach
```

建议：

- 为每个 branch 维护一个长期 root，例如 mixed dict 或 typed schema root。
- 不要频繁换 root，除非明确想改变持久对象图边界。
- 业务入口对象应从 `rev.GraphRoot` 恢复，而不是保存旧进程里的对象引用。

### 9.2 Delta / Rebase

使用者不用手动选择 delta 或 rebase。

内部会根据对象状态和版本链策略写入增量或全量帧：

- dirty 对象会写。
- clean 且已 tracked 的对象通常不重写。
- segment 轮换或 cross-file snapshot 会强制可达对象写成可独立打开的快照。

### 9.3 ExportTo / SaveAs

`Revision.ExportTo` / `Revision.SaveAs` 是内部/测试层更常见的底层能力。普通使用优先走 `Repository.Commit`。

区别：

- `ExportTo(root, targetFile)`：把当前内存图导出到另一个 RBF 文件，不改变当前 `Revision.HeadId`。
- `SaveAs(root, targetFile)`：把当前图写入新文件，并把当前 `Revision` 的 head 切换过去。
- Repository segment rotation 内部会使用 SaveAs 路线。

---

## 10. 错误处理习惯

公开入口大量使用 `AteliaResult<T>`。

推荐封装一个本地 helper，而不是到处 `.Value`：

```csharp
static T Unwrap<T>(AteliaResult<T> result) {
    if (result.IsFailure) {
        throw new InvalidOperationException(result.Error!.ToString());
    }

    return result.Value;
}
```

读取容器值时优先按语义选择：

| 目标 | API |
|---|---|
| 正常缺失是业务分支 | `Get(..., out value)` / `TryGet(...)` |
| 缺失就是 bug | `GetOrThrow(...)` / deque throwing helpers |
| mixed 容器要先探测类型 | `TryGetValueKind(...)` |
| deque 越界是可预期分支 | `GetAt(..., out value)` 或 `TrySetAt(...)` |

---

## 11. LLM Agent 使用建议

### 11.1 推荐 root schema

原型期可以用 mixed root：

```csharp
var root = rev.CreateDict<string>();

root.Upsert("schemaVersion", 1);
root.Upsert("sessionId", sessionId);
root.Upsert("notes", rev.CreateText());
root.Upsert("events", rev.CreateDeque<DurableDict<string>>());
root.Upsert("index", rev.CreateOrderedDict<long, DurableObject>());
```

优点：演化快，适合探索。

当 schema 稳定后，逐步迁移到 typed 容器：

```csharp
var events = rev.CreateDeque<DurableDict<string>>();
var byTimestamp = rev.CreateOrderedDict<long, DurableDict<string>>();
```

### 11.2 事件/消息建模

StateJournal 当前没有任意 object serializer。不要直接尝试存 POCO。

推荐把消息建成 durable record：

```csharp
DurableDict<string> NewMessage(Revision rev, string role, string content, long createdAt) {
    var msg = rev.CreateDict<string>();
    msg.Upsert("kind", "message");
    msg.Upsert("role", role);
    msg.Upsert("content", content);
    msg.Upsert("createdAt", createdAt);
    return msg;
}
```

如果字段固定且类型明确，可用多个 typed 容器拆列存储；如果字段会快速演化，用 mixed dict 更省心。

### 11.3 文本内容

长文本或需要精确编辑定位的内容优先放 `DurableText`，不要塞进单个巨大 string：

```csharp
var note = rev.CreateText();
note.LoadText(markdown);
root.Upsert("note", note);
```

LLM Agent 应保存 block ID，并在后续编辑中使用 `InsertAfter/InsertBefore/SetContent/Delete`。

### 11.4 提交流程

建议把每个外部可观测步骤结束时 commit：

```csharp
ApplyUserMessage(root, message);
repo.Commit(root).Value;

ApplyToolResult(root, toolResult);
repo.Commit(root).Value;

ApplyAssistantDecision(root, decision);
repo.Commit(root).Value;
```

这样 crash 后最多丢失当前未提交步骤。

---

## 12. 当前边界与易踩坑

- 当前 `Open` 是全量加载，不要假设按对象 lazy load。
- StateJournal 不是任意 C# 对象图 serializer；只持久化 durable 容器和支持的 scalar。
- 不要跨 `Revision` 存 DurableObject。
- 不要继续使用被 GC sweep 后的 detached 对象。
- `Commit(root)` 的 root 决定可达性；没挂在 root 下的对象不会保留。
- 当前只有 `DurableDict` 正式支持 `ForkCommittedAsMutable()`。
- 当前 `DurableDict` 与 `DurableDeque` 正式支持 `Freeze()` / `IsFrozen`；`DurableDeque` 仍未暴露 public fork，`OrderedDict` / `DurableText` 仍不支持 freeze。
- `ForkCommittedAsMutable()` 复制 committed state，不复制 source 的未提交 working state。
- `Freeze()` 后的修改会抛 `ObjectFrozenException`。
- dirty frozen source 不能直接 fork；先 commit 让 frozen snapshot 落盘。
- mixed 容器里的 `double` 默认可能采用紧凑编码；需要精确保存所有 double bit 时使用 `UpsertExactDouble` 或 exact double helpers。
- typed `string` 和 mixed `string` 都走值语义 payload 路线；typed/mixed `Symbol` 才走 intern 池。注意 typed `string` 的 `null` 规范化为空字符串，而 mixed `string` 的 `null` 存为 `ValueBox.Null`。
- `SymbolTable` 是持久化 mirror，不是业务可见数据表。
- `Repository` 目录文件不要手工改；branch refs 是 CAS 保护的元数据。
- 业务对象创建优先用 `Revision.Create*`，不要直接用 `Durable.*` 工厂绕过绑定。
- `Revision` 裸构造和 `RbfFile` 直连 commit 多见于测试；实际集成优先用 `Repository`。

---

## 13. 相关文件

代码入口：

- [`src/StateJournal/Repository.cs`](../../src/StateJournal/Repository.cs)
- [`src/StateJournal/Revision.cs`](../../src/StateJournal/Revision.cs)
- [`src/StateJournal/DurableDict.Typed.cs`](../../src/StateJournal/DurableDict.Typed.cs)
- [`src/StateJournal/DurableDict.Mixed.cs`](../../src/StateJournal/DurableDict.Mixed.cs)
- [`src/StateJournal/DurableDeque.Typed.cs`](../../src/StateJournal/DurableDeque.Typed.cs)
- [`src/StateJournal/DurableDeque.Mixed.cs`](../../src/StateJournal/DurableDeque.Mixed.cs)
- [`src/StateJournal/DurableOrderedDict.cs`](../../src/StateJournal/DurableOrderedDict.cs)
- [`src/StateJournal/DurableOrderedDict.Mixed.cs`](../../src/StateJournal/DurableOrderedDict.Mixed.cs)
- [`src/StateJournal/DurableText.cs`](../../src/StateJournal/DurableText.cs)

测试样例：

- [`tests/StateJournal.Tests/RepositoryTests.cs`](../../tests/StateJournal.Tests/RepositoryTests.cs)
- [`tests/StateJournal.Tests/DurableDictApiTests.cs`](../../tests/StateJournal.Tests/DurableDictApiTests.cs)
- [`tests/StateJournal.Tests/DurableDequeApiTests.cs`](../../tests/StateJournal.Tests/DurableDequeApiTests.cs)
- [`tests/StateJournal.Tests/DurableOrderedDictTests.cs`](../../tests/StateJournal.Tests/DurableOrderedDictTests.cs)
- [`tests/StateJournal.Tests/MixedOrderedDictTests.cs`](../../tests/StateJournal.Tests/MixedOrderedDictTests.cs)
- [`tests/StateJournal.Tests/DurableTextTests.cs`](../../tests/StateJournal.Tests/DurableTextTests.cs)

背景文档：

- [`docs/StateJournal/memory-notebook.md`](memory-notebook.md)
- [`docs/StateJournal/repository-note.md`](repository-note.md)
- [`docs/StateJournal/container-api-design-note.md`](container-api-design-note.md)
- [`docs/StateJournal/fork-as-mutable-design.md`](fork-as-mutable-design.md)
- [`docs/StateJournal/frozen-durable-object-design.md`](frozen-durable-object-design.md)
- [`docs/Rbf/rbf-interface.md`](../Rbf/rbf-interface.md)
- [`docs/Data/Draft/SizedPtr.md`](../Data/Draft/SizedPtr.md)
