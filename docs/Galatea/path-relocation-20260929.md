# Galatea 本机路径迁移记录（2026-09-29）

已把活动配置从 `/repos/focus/atelia` 迁到 `/repos/Atelia-org/atelia`，并显式重绑定相关持久身份。用户要求继续停服，自行启动。没有建立旧路径别名、bind mount 或 symlink，没有调用 provider。

## 相对路径与保留的绝对路径

本机 ignored `prototypes/Galatea/.atelia/galatea/config.json` 的9个路径改为相对 config 文件所在目录：

- 两角色 `sessionDir` → `sessions/<repository>`。
- `delegationStateDir` → `delegation-state/<character>`。
- `characterMemoryStateDir` → `character-memory/<character>`。
- `characterContextTemplateFile` → `prompts/<character>.md`。
- `runtime.callLogDir` → `../../../../gitignore/...`，解析后仍是原日志目录的新位置。

`delegates.json` 的 sidecar entryPoint / codexCommand 按现有严格读取器合同保持规范绝对路径，只把repo根换为新根。Node、CodexHome、角色homeDir、allowedRoots与route config不变，远端thread绑定不重置。

本机 `eng/NuGet.Storage.Local.config` 的storage feed改为相对该文件的 `../../atelia-storage/artifacts/dev-feed`，新包版本与Completion pin保持。模板和日常指南同步更新；已有历史执行记录、备份与业务正文中的旧路径保留其历史含义。

## 持久身份重绑定

目前两类仓身份仍派生自规范绝对 session 路径：delegation使用 `gdsr1-` 前缀，Character Memory使用 `cmsr1-` 前缀。只改路径配置会破坏owner匹配；使用相对配置也不改变此规则，今后再次迁址仍需显式处理身份。

本次对四个SQLite副本精确修改97个字段后再发布：

| 表与字段 | 修改数 |
|---|---:|
| 两库 delegation_meta.session_repository_id | 2 |
| 两库 character_memory_meta.session_repository_id | 2 |
| internal_mail_outbox.target_session_repository_id | 93（cyber42、gpt51） |

93条目标引用均已Delivered，仍与各自目标角色新身份同步。dispatch/message/operation/thread/turn IDs、revision、head、frontier、所有状态不变。dispatch校验会重算ID，但其输入不含session路径；MemoPod身份依赖文档字节，也不含目录位置。当前schema没有需要重写的旧route policy fingerprint。

旧路径若出现在邮件、笔记、摘要或bound_input正文中，仍原样保留；bound_input是历史Observation精确证据，不能文本批量替换。两条Pending note receipt未被推进。原始SessionJournal与MemoPod文件全部逐字节保留。

## 备份、执行和验证

私有完整备份与一次性脚本在兄弟storage仓的ignored目录：

```text
artifacts/galatea-path-relocation-20260929023952/
  backup/                 # 迁移前config、delegates、connections、两sessions及两类sidecar完整树
  candidate/              # 已验证的迁移候选
  migration-plan-and-verification.json
  prepare.py / verify.py / apply.py
  apply-journal.jsonl
  installed-verification-after-host.json
  installed-host-read.jsonl
```

脚本是对这两个停服实例的明确一次性操作，包含原身份/schema/行数和库存断言，不是可用于任意实例的通用工具。发布前核对57个源文件未漂移且无打开句柄；四库和两配置作为同一停服维护集合以逐文件原子替换发布，记录每步，执行失败时恢复已替换成员。数据库与配置的回退必须成套进行。

验证结果：

- 全表逐行逐列比较，只允许97个身份单元变化；所有其他数据库字段和schema完全相同。四库quick_check及foreign_key_check通过。
- 其他51个文件，包括所有sessions、MemoPod、旧数据库备份与connections，SHA256完全相同。原文件权限保留。
- 使用应用内部正式严格打开器验证候选：delegation只读Open+ReadSnapshot，CharacterMemory在副本Open+ReadStatusSnapshot；两个角色全部通过。无生产代码或验证规则修改。
- 新相对配置下，真实GalateaHostService maintenance模式无需别名即可打开两会话；两者Idle、各6条recent turns，Cadence freshness均exact。cyber below-target、gpt awaiting-recent-reserve是当前正常进度。
- Mailbox均返回维护模式的MAINTENANCE_READ_ONLY；provider clients创建数0。Host退出后再次核对97字段以外无变化，两条Pending receipt保持。
- 验证probe Release build 0 warnings/0 errors；相对feed下普通Galatea restore成功，assets仍固定Storage `0.2.0-dev.20260929020652` 与Completion `0.1.0-dev.20260923214739`。

本次为本机配置/数据及文档变更，不改运行时源码，不重复上一轮2803测试，不声称完成真实生成、外部工具调用或Windows运行验证。临时probe利用现有test friend assembly访问internal store API，未放宽生产访问边界。

可以在atelia仓根按原方式启动：

```bash
dotnet run --no-restore -c Release --project prototypes/Galatea/Galatea.Server.csproj
```
