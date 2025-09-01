# Version Encoding Refactoring - 改进方案总结

## 问题描述

在原始实现中，version字段的编码和解码逻辑分散在不同的地方：
- **编码**：在`VersionedStoragePathProvider.GetDataFilePath()`中直接转换为十进制字符串
- **解码**：在`IKeySerializer.TryParseFileName()`中进行解析

这种设计有以下问题：
1. 职责不清：`IKeySerializer`承担了文件名解析的全部责任
2. 耦合度高：版本格式变更需要修改多个地方
3. 可扩展性差：无法轻松支持不同的版本编码格式（如hex）

## 改进方案

### 1. 创建专门的版本格式化器

引入`IVersionFormatter`接口，专门负责版本号的编码和解码：

```csharp
public interface IVersionFormatter
{
    string FormatVersion(long version);
    long? ParseVersion(string versionString);
}
```

### 2. 实现多种版本格式化器

- **`DecimalVersionFormatter`**：十进制格式（保持向后兼容）
- **`HexVersionFormatter`**：十六进制格式（减少文件名长度）

### 3. 重构职责分离

- **`IKeySerializer`**：只负责key的序列化和反序列化
- **`VersionedStoragePathProvider`**：统一处理文件名的组合和解析
- **`IVersionFormatter`**：专门处理版本号的编码和解码

### 4. 修改接口和实现

- 移除`IKeySerializer.TryParseFileName()`方法
- 在`VersionedStoragePathProvider`中添加`TryParseFileName()`方法
- 修改所有相关的构造函数以支持`IVersionFormatter`参数

## 改进效果

### 优点

1. **职责清晰**：每个组件的职责更加明确
2. **易于扩展**：可以轻松添加新的版本格式化器
3. **向后兼容**：默认使用十进制格式
4. **文件名优化**：hex格式可以显著减少大版本号的文件名长度

### 性能对比

对于版本号 `1,000,000,000`（10亿）：
- **十进制**: `1000000000` (10个字符)
- **十六进制**: `3B9ACA00` (8个字符)

对于 `long.MaxValue`：
- **十进制**: `9223372036854775807` (19个字符)
- **十六进制**: `7FFFFFFFFFFFFFFF` (16个字符)

### 使用示例

```csharp
// 使用十进制格式
var decimalFormatter = new DecimalVersionFormatter();
var storage1 = VersionedStorageFactory.CreateStorageAsync(options, keySerializer, logger, decimalFormatter);

// 使用十六进制格式（默认）
var hexFormatter = new HexVersionFormatter();
var storage2 = VersionedStorageFactory.CreateStorageAsync(options, keySerializer, logger, hexFormatter);
```

## 测试覆盖

已添加完整的测试覆盖：
- `VersionFormatterTests`：验证各种格式化器的编码和解码功能
- `VersionedStoragePathProviderTests`：验证文件路径生成和解析功能

## 结论

这次重构成功解决了版本编码逻辑分散的问题，提供了更清晰的架构和更好的可扩展性。用户可以根据需要选择十进制或十六进制格式，在文件名长度和可读性之间取得平衡。
