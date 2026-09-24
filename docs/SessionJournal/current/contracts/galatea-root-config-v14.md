# Galatea root config V14（当前合同）

Galatea host 只接受精确整数 `"v": 14`。根字段仍为 `v`、`characters`、`players`、`runtime`；
其余 Character、Player、connections 与 delegates 合同见[配置指南](../../../Galatea/configuration.md)。
未知、重复、缺失字段及非 canonical JSON 继续 fail closed。

每个 Character 必须指定 `defaultConnectionId` 和 1..256 项 `connectionOptions`：

```json
"connectionOptions": [
  {"connectionId": "local", "name": "", "trigger": ""}
]
```

每项的三个字段均为必填字符串。`connectionId` 须在 Completion catalog 中精确存在、在该角色内不重复，
且 `defaultConnectionId` 须包含在该角色的选项中。`name` 和 `trigger` 各最多 4096 UTF-8 bytes；
当前只保留配置，不参与模型选择，也不在网页显示。`connections.json` 仍是 V3 catalog，但 Galatea 拒绝
全局 `selectableConnectionIds`；浏览器诊断选择与进程内 runtime override 均受目标角色的选项约束。

`runtime.recapGrid` 仍是必需的 exact object，只有 `maintenance`：

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

从 [V13](galatea-root-config-v13.md) 转换须显式更新配置与 catalog，并在启动前备份；正常启动不迁移文件。
此版本不改变 SessionJournal 中已冻结请求的恢复身份。
