# Galatea root config V13（历史合同）

当前宿主使用 [V14](galatea-root-config-v14.md)。本页记录 V13，不代表当前代码接受此版本。

Galatea host 只接受精确整数 `"v": 13`。根字段仍为 `v`、`characters`、`players`、`runtime`；
其余 Character、Player、connections 与 delegates 合同见[配置指南](../../../Galatea/configuration.md)。
未知、重复、缺失字段及非 canonical JSON 继续 fail closed。

`runtime.recapGrid` 是必需的 exact object，只有 `maintenance`：

```json
{
  "maintenance": {
    "connectionId": "local",
    "maximumConcurrency": 1,
    "dispatchTimeoutMilliseconds": 900000
  }
}
```

`connectionId` 非空；`maximumConcurrency` 为 1..1024；`dispatchTimeoutMilliseconds`
为 1..86,400,000。维护调用复用同一个连接注册表、retry invoker 和全局并发 lane。
默认 RecapGrid asset 由 Galatea 代码选择，已持久化 `RowWork` 按其 actual producer 延迟构造 exact route。

V13 删除 `historicalAgentControlProfileFiles`，同时删除模型可见的 `recap_grid_control` 工具实现。
普通新回合不提供该工具；冻结的旧工具 runtime 若仍处于当前会话尾部，恢复返回
`tool-runtime-unsupported`，不会忽略工具调用或以新策略重派发。已经结清工具结果、且后续
Prepared 没有工具 runtime 身份的普通模型请求仍可按自身 frozen completion identity 恢复。

从 [V12](galatea-root-config-v12.md) 转换须在停服、备份和只读检查 live session 尾部后显式完成：
把 `v` 改为 `13`，移除 `historicalAgentControlProfileFiles`。正常启动不迁移文件。
