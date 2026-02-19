# StateJournal鸟瞰
## 提供的核心价值
- Data-Model: Object and Value
- Object-Diff: 高效生成Delta
- 对象图的增量序列化/反序列化
- 基于提交的版本管理


## 存储
- 以独占的文件系统目录作为存储载体。
- 载体目录内管理多个[Rbf文件](atelia/docs/Rbf/rbf-interface.md)作为数据存储。*RBF是一种为逆序读取优化的二进制分帧文件格式，写入操作方面只支持追加帧。*
- 以RbfFrame作为读写单元。

## 对象模型

DurableBase
  - ValueBox Immutable
    - Unsigned-Integer: CBOR风格的变长编码。{1,2,4,8*,16*}字节编码。
    - Signed-Integer: 在unsigned integer bits的基础上叠加ZigZag编码。
    - Boolean:
    - Float-Point:
    - Taged-Integer: 扩展点，{int Tag,整数}对
    - Taged-Byte-String: 扩展点，{int Tag,byte[]}对
    - Char-String: utf-8, utf-16（为中文+C#环境优化）
    - Decimal:
    - Guid:
    - Char:
    - 待进一步补充: 比如时间戳、时间间隔、地理坐标等。

  - DurableObject Mutable
    - DurableList<T>: 变长序列
    - DurableDict<TKey, TValue>: 键值映射
    - DurableArray<T>: 定长数组
    - DurableText: 多行可变文本，是特化的字符串容器。*是否应该引入还存疑。*

泛型容器类型：
  - 支持具体类型特化。容器元数据集中存储类型信息，每个元素直接存储值，例如Base128+ZigZag编码的整数，类似Protobuf。
  - 支持基于抽象基类的混杂存储。混杂存储时，每个元素都包含类型+编码长度信息，类似CBOR。
