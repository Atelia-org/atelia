# atelia-completion 拆仓验收记录

## 当前阶段

2026-09-14 开始实施。用户要求先准备并推送新仓，随后暂停，由用户创建 NuGet Trusted Publishing policy；preview 发布及后续集成在用户交回后继续。

- P0：闭包与 Windows/Linux 基线已记录；Windows 平台失败已在 Linux 同组通过。
- P1：独立源码、冻结开发包、两平台 public package smoke 与包资产审计通过。
- P2：尚未切换 Atelia；原仓活动源码保留。
- P3：用户已创建 public GitHub 仓并推送 `7d19e10`，远端 Windows/Linux CI 全部通过（run `34838908288`）；未发布任何包。
- P4：未开始；DramaBoard 未修改。

## 来源与边界

源 Atelia：`c66d84654408321aab00a66aeb53e1dd19a44679`，branch `main`。
启动时存在 `AGENTS.md` 未提交修改、两份未跟踪的 completion 方案/审查文档，全部保留。
原始完整 diff 和未跟踪文件 SHA256 存于仓外任务目录 `.completion-extraction`。
当前 StoragePackageVersion `0.1.1-preview.2` / revision `976aa345f923da09e2a5cf1dc25ba592b3818b63` 不变。

新工作树：`E:/repos/Atelia-org/atelia-completion`。fresh clone 后使用 git-filter-repo 2.47.0
提取当前六目录及五个独立库历史目录，保留 185 个相关提交、169 个当前文件。
原仓历史没有重写。完整路径和映射存入新仓 `docs/extraction-origin.md` 与 `docs/extraction-commit-map.txt`。
四库 96 个 C# 文件，唯一预期差异为移除跨仓 RecapGrid 测试 IVT；业务实现及原测试 C# 保持来源内容。

## 已执行验证

Windows SDK `10.0.201`，运行时 `Microsoft.NETCore.App 10.0.5`。

- `dotnet build Atelia.sln -c Release`：成功，0 warning / 0 error。
- Completion 最终离线：显式关闭五个 ATELIA_RUN opt-in、清空进程内 OPENROUTER_API_KEY，并使用 `--filter 'Category!=LiveE2E&Category!=LocalE2E'`；Windows 817 passed / 3 platform skipped / 0 failed，Linux 819 passed / 1 platform skipped / 0 failed。原仓和新仓使用同一过滤器，结果一致。
- 首次不带 Category filter 的 Completion 运行：826 passed / 3 skipped；不作为严格离线证据，随后以上述隔离运行替代。

消费者基线采用同一 Release 构建，串行运行：

| 集合 | Windows | Linux 补验 |
| --- | --- | --- |
| WalkingSkeleton | 27 passed | 未另跑 |
| RecapGrid.Runtime | 73 passed | 未另跑 |
| SessionJournal codec/请求恢复 | 123 passed | 未另跑 |
| CLI factory | 6 passed | 未另跑 |
| Galatea recovery/composition/reasoning replay | 12 passed / 9 failed | 21 passed |
| MemoPod architecture/recall | 9 passed / 11 failed | 20 passed |
| SessionJournal.PublicSurface | 4 passed | 未另跑 |
| Hosting.PublicSurface | 7 passed | 未另跑 |
| Runtime.PublicSurface | 4 passed | 未另跑 |

Windows 的 20 项失败发生于原仓：Timeline 的 fsync/flock 和 MemoPodStoreLayout 明确仅支持 Linux；同一来源在 WSL 原生文件系统的相同集合全部通过。未修改产品平台约束。Linux Galatea 耗时 1m36s，运行完成，未因阶段性无输出中止。

新仓本地提交：`7d19e10c3f7b3dec3981ccf528c446cf94efef8f`，工作树干净。
冻结开发版：`0.1.0-dev.20260914.1`，一次 Windows Pack 产生四 nupkg + 四 snupkg，feed 为 `.completion-extraction/feed-1`。
Windows 和 Linux 的 `Test-Package.ps1` 共同消费这一次产包，均通过 public HTTP 状态/取消/EOF/usage 与 Tools schema/绑定/执行验证；独立 Diagnostics-only、Abstractions-only 闭包验证通过，实际缓存 SHA256/SHA512 与冻结 feed 一致。
实际包资产审计通过：四包 ID/version、Repository URL/commit、README、LICENSE、XML、portable PDB 和指向该提交的 Source Link 元数据。尚未验证远端源码/公共符号取得，因为新仓未推送且包未发布。
quick-start 的 XML/C# 示例已从 Markdown 原文物化，引用同一开发包运行，输出 `hello`。
新仓首次开启 XML 文档输出出现 16 条既有 XML 注释警告，构建无错误；未借拆仓修改运行时代码或屏蔽这些警告。

独立 reviewer 未发现阻断实现问题；其提出的实际包资产审计缺口已补验。CI/publish 已准备但未远端执行；公开包发布前还须更新包 README 的未发布状态、固定 release tag，并继续 P2/P3 原消费者验收。
详细日志位于 `E:/repos/Atelia-org/.completion-extraction`；临时日志不是日常构建依赖。

## 当前交接与身份设置

用户已手动创建并推送新仓，远端 HEAD 为 `7d19e10c3f7b3dec3981ccf528c446cf94efef8f`；[CI run 34838908288](https://github.com/Atelia-org/atelia-completion/actions/runs/34838908288) 成功。

本次更新 GITHUB_TOKEN 并重启后的最新实测：`GET /user` 返回 Robird；已成功创建 `nuget` environment，并成功设置和回读 repository Actions variable `NUGET_USER=Robird`。此前 token 有效期限制、401 及设置写入 403 的阻碍均已解除，不再是当前待办。未触发发布。

用户已确认 NuGet policy `atelia-completion-publish` 为 Active：owner `Atelia`，GitHub `Atelia-org/atelia-completion`（owner ID `212307724` / repo ID `1369817269`），workflow `publish.yml`，environment `nuget`，允许新包和新版本，pattern `Atelia.*`。已核对 GitHub 实际 ID 和设置一致，继续 P2/P3；发布入口要求从匹配的 `v{version}` tag 手动触发。

## P2/P3 继续实施

隔离候选工作树 `.completion-extraction/atelia-candidate`，分支 `extract/completion`，base 仍为 `c66d84654408321aab00a66aeb53e1dd19a44679`。23 个活动消费者项目的 33 条 Completion/Diagnostics 引用切换为互斥包/源码配置；四源码目录、Completion.Tests、docs/Completion 与五个 solution 项一起迁出。Storage pin 与配置保持不变。

上游发布候选提交 `3ae1ebeccdd94a7bd507444154a283201a68c992`：更新六份发布文档、补自有精确 IVT 测试、保存 CI package smoke 回执；runtime 未变。源码测试 Windows 818 passed / 3 skipped；[CI 34848912437](https://github.com/Atelia-org/atelia-completion/actions/runs/34848912437) Windows/Linux 通过。冻结开发包 `0.1.0-dev.20260914.2` 位于 feed-2，独立包 smoke 通过；旧 dev1 不覆盖。

候选 Release Rebuild 已通过（0 warning/error）；原消费者测试与源码模式/干净物化验证进行中，尚未发布公开包或合入 Atelia main。
