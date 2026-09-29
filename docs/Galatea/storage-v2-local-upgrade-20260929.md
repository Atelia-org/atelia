# Galatea 本机 storage v2 切换记录（2026-09-29）

本记录描述当前 Linux 工作区的本地开发包与两个真实 SessionJournal repository 的显式升级。它不是公开包发布或 Windows 验收。

## 包与构建配置

- 上游：`atelia-storage` commit `5288bd55b4a5181942dadd76516f799002508144`。
- 五包版本：`0.2.0-dev.20260929020652`，位于兄弟仓 `artifacts/dev-feed`；来源与哈希以该 feed 的 `manifest.0.2.0-dev.20260929020652.json` 为准。
- 已运行上游 `eng/Pack.ps1` 和 `eng/Test-Package.ps1 -AdditionalSegmentSmoke`；隔离 PackageReference 验证、两个公开 smoke、来源/PDB/90个本地源码 checksum 校验通过。未向网络发布。
- 本机 ignored `eng/StorageDependency.Local.props` 固定新版本、来源与 `StorageStrictTailOpen=true`；ignored `eng/NuGet.Storage.Local.config` 合并 Storage 与既有 Completion feed。Completion pin `0.1.0-dev.20260923214739` 保持。
- 普通 restore/build/run 使用新包，无须 CLI 属性。提交的默认配置仍使用原公开包，模板和切换步骤见[存储依赖](../storage-dependency.md)。
- v2 已移除旧 recovery options。CLI 通过显式 API 能力编译标记选择严格打开；旧包分支仍显式关闭 recovery，避免默认公开包下 rewind 意外修尾。

## 数据范围与回退材料

原路径为 `prototypes/Galatea/.atelia/galatea/sessions/` 下 `cyber-session-journal-recap-grid` 和 `gpt`。
使用 `upgrade-v1tov2 --profile legacy-bb7c4fb` 处理完整停写备份中的 `events/refs`；所有 RBF 原路径和字节保持，添加 `journal.format`、两个 `active.segment` 及 `refs/catalog.snapshot`。
应用候选保留全部 `control/derived`，包括历史 SQLite 备份文件。没有改写会话内容、ref/head、Prepared 或外部调用恢复状态。

本机私有证据和完整备份位于：

```text
/repos/Atelia-org/atelia-storage/artifacts/galatea-cutover-20260929020652/
  <repository>/original-backup/       # 升级前完整独立备份
  <repository>/upgrade-bundle/        # Toolkit manifest 与纯 storage 候选
  <repository>/application-candidate/ # 包含所有应用 sidecar 的候选
  <repository>/original-retired/      # 从原路径替换下来的完整 v1 目录
  <repository>/write-validation-clone/ # 只用于续写测试，不可部署
```

回退必须停服，成对恢复升级前完整 repository 与旧包配置。不要只把包降级后继续打开 v2，也不要把已续写的v2与旧backup混用。上线后新增事实不在旧备份内；需要另行处理，不能以旧备份覆盖。

## 验证与运行状态

两个原路径已替换为 v2。每个 repository 的14个原文件SHA-256保持一致，仅新增4个元数据文件。切换事务记录与安装清单见证据目录的 `cutover-transactions.jsonl` 和 `installed-data-check.json`。

- 两库 Toolkit full audit 和 daily check通过；SessionJournal selected-chain validate、Timeline verify、Control verify、Cadence inspect、RecapGrid Store verify均通过。
- selected chain分别838/1239事件，均Idle；不是物理事件总数，后者还包含非当前链事实。
- 两个独立write-validation-clone通过公开 `ReconcileDesiredSetup` 实际追加、冷重开和完整SessionJournal validate。正式目录没有这些测试写入。
- 六个SQLite文件（含历史备份）immutable只读 `quick_check`通过，另有上述领域验证作为应用证据。
- 消费仓全solution Release build 0 warnings/0 errors；44个涉及Storage的活动solution项目assets均解析五包新版本，无Storage ProjectReference。最终12个相关项目 **2803 passed / 5 skipped / 0 failed**，不是整个solution全测试集；排除Performance/Stress命名及三个指定live classes。两个65537极限opt-in和三个Release诊断测试为实际skip。
- Galatea.Server默认并发两次暴露既有note timing test超时；原测试独立通过，保持deadline与产品代码不变，用相同filter加 `-- xUnit.MaxParallelThreads=4` 完整重跑后1379passed/3skipped。前两次失败日志与最终日志均保留，不把隔离单测通过代替完整结果。
- 本次维护支持新包、旧公开包的编译分支；当前实际测试使用新dev包。旧公开包分支保留原strict选项和原fixtures，本轮未另跑旧包完整测试。
- 已核对并恢复所有原目录/文件的Unix mode，uid/gid保持；新metadata沿用原ref-op-log的mode。
- 用户要求完成后保持停服，由用户自行启动；没有真实provider调用。

## 原有路径配置问题

本机 `config.json`/`delegates.json` 仍引用 `/repos/focus/atelia`，该目录在当前机器缺失；实际仓位于 `/repos/Atelia-org/atelia`。真实四个delegation/CharacterMemory数据库的session repository identity均匹配旧路径SHA-256，不匹配新路径。不能批量改sessionDir或修改SQLite身份来掩盖问题。此问题独立于EventJournal布局升级。已在private mount namespace中将新仓bind mount到旧路径，用真实配置构造maintenance GalateaHostService：两角色均Idle，各读取6条recent turns，Cadence freshness均exact（cyber below-target，gpt awaiting-recent-reserve）。后者是正常未达recent reserve，不是损坏。验证factory拒绝provider构造，实际创建数为0；真实两个repo与21个外部配置/sidecar文件hash保持不变。

该mount仅存在于验证进程；退出已清除，当前没有全局 `/repos/focus/atelia` 别名。用户尚待选择是否建立本机临时bind mount，或另行处理路径身份迁移。完成此项之前，直接正常启动仍会在旧路径解析阶段失败；不能把本次数据与包升级理解为已消除该独立启动障碍。不要建立软链接，sidecar会拒绝reparse路径。用户若选择临时bind mount，应记录它在重启后需重新建立；本轮不修改fstab。

验证使用本地artifact内的临时probe；仅为访问现有internal config loader使用reflection，读取与续写均调用现有公开Host/SessionJournal APIs，未更改生产API或配置。


固定旧synthetic fixtures中的SessionJournal/Control/Timeline历史格式保持不变，另建StorageV2 sibling ZIP，只增加底层storage metadata。原ZIP继续验证公开旧包分支。Galatea的missing/empty/file不变；present-but-incomplete目录在v2下验证精确 `FormatUnsupported/LegacyOrIncompleteLayout`，生产catch没有吞并该错误。
