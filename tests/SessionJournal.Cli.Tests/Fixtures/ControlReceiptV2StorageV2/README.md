# Storage v2 synthetic fixture

这些样本仅来自相邻旧合成 fixture，未使用真实 Galatea 数据或 provider。旧目录与 ZIP 原样保留；其原始生成说明见 [原 fixture README](../ControlReceiptV2/README.md)。

2026-09-29 使用 atelia-storage commit `5288bd55b4a5181942dadd76516f799002508144` 已构建的 EventJournal.Toolkit，显式声明 `legacy-bb7c4fb` profile，执行 `upgrade-v1tov2 <isolated-events-refs> --profile legacy-bb7c4fb --output <new-bundle>`。此 profile 是升级输入声明，不声称通过磁盘识别出原 writer commit。

只从旧 ZIP 的稳定副本复制 events/refs 到纯 Storage 输入；成功 manifest、全库 audit 与日常核对后将升级后的 events/refs 和 journal.format 合并回完整合成目录。其余 SessionJournal/Control/Timeline/Store/Cadence/config/expected 文件逐字节保留；全部原 .rbf facts 同样按原路径 SHA-256 核对相等，未重编码历史请求/工具结果。ZIP 格式保护了 empty locks 和目录，Unix 权限仍由原 extraction helper恢复。消费测试不需要兄弟仓或 Toolkit。

`StorageStrictTailOpen=true` 的测试编译选择此目录；默认公开旧包仍使用旧目录。

| 文件 | 原 ZIP SHA-256 | 新 ZIP SHA-256 |
|---|---|---|
| `prepared-with-old-tool-result.zip` | `7e14511299a3bd204ffc4268bdf1f034e00a0bf8a3a1f392af5d20c70631449d` | `08048193ee606eac917edfd69556475b90c7080ecfb59934c7bae9f130ad43dd` |
| `after-tool-result.zip` | `5f42c26489842eba331395991e2487c7f1b712c654dd288e45170044fa73192b` | `b581dfde19835622a7009395becb3829033c905467194aaa7b57bbcbc0f7aef8` |
| `after-control-commit.zip` | `75733c8fcb285d3ca9c0e535cf6ef037e4f4c2f70cae42a7de3cd5316b794515` | `f3e77ed5ff9767fe49b6a60da5a465b27b6a68b0114958d88836b0d5a63f417d` |
