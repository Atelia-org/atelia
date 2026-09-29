# Storage v2 synthetic fixture

这些样本仅来自相邻旧合成 fixture，未使用真实 Galatea 数据或 provider。旧目录与 ZIP 原样保留；其原始生成说明见 [原 fixture README](../RowIdentityV2/README.md)。

2026-09-29 使用 atelia-storage commit `5288bd55b4a5181942dadd76516f799002508144` 已构建的 EventJournal.Toolkit，显式声明 `legacy-bb7c4fb` profile，执行 `upgrade-v1tov2 <isolated-events-refs> --profile legacy-bb7c4fb --output <new-bundle>`。此 profile 是升级输入声明，不声称通过磁盘识别出原 writer commit。

只从旧 ZIP 的稳定副本复制 events/refs 到纯 Storage 输入；成功 manifest、全库 audit 与日常核对后将升级后的 events/refs 和 journal.format 合并回完整合成目录。其余 SessionJournal/Control/Timeline/Store/Cadence/config/expected 文件逐字节保留；全部原 .rbf facts 同样按原路径 SHA-256 核对相等，未重编码历史请求/工具结果。ZIP 格式保护了 empty locks 和目录，Unix 权限仍由原 extraction helper恢复。消费测试不需要兄弟仓或 Toolkit。

`StorageStrictTailOpen=true` 的测试编译选择此目录；默认公开旧包仍使用旧目录。

| 文件 | 原 ZIP SHA-256 | 新 ZIP SHA-256 |
|---|---|---|
| `repository.zip` | `8c4675049f539ba7ac610fa646abe957c0adcb54b972e740dd68fe385121bc6d` | `15c4d2eabeb6369e72d7bdc7a459fd9a1e22e54aae346c08fbfa1d60b1f388ea` |

原 `manifest.json`（Control backup expected manifest）或 `commands.json`（冻结 Control operations）作为测试期望逐字节复制；它们仍描述原领域样本，不是新 Storage 升级证明。新的 Storage 来源与升级 manifest 统一在 `storage-upgrade-evidence.json`。
