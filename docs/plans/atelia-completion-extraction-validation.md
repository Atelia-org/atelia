# atelia-completion 拆仓验收记录

## 当前阶段

2026-09-14 开始实施。用户完成 NuGet Trusted Publishing policy 后已交回继续，四包 `0.1.0-preview.1` 已公开发布。

- P0：闭包与 Windows/Linux 基线已记录；Windows 平台失败已在 Linux 同组通过。
- P1：独立源码、冻结开发包、两平台 public package smoke 与包资产审计通过。
- P2：包/源码两模式、依赖边界、原消费者验收通过；迁移与最终公开 pin 已合入 Atelia main，默认路径复验完成。
- P3：`0.1.0-preview.1` 已发布，四包签名、nuget.org-only 消费、公开 PDB 与 96 个 Source Link 文件验证通过。
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

## P0/P1 验证记录

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

P1 验收提交：`7d19e10c3f7b3dec3981ccf528c446cf94efef8f`，当时工作树干净。
冻结开发版：`0.1.0-dev.20260914.1`，一次 Windows Pack 产生四 nupkg + 四 snupkg，feed 为 `.completion-extraction/feed-1`。
Windows 和 Linux 的 `Test-Package.ps1` 共同消费这一次产包，均通过 public HTTP 状态/取消/EOF/usage 与 Tools schema/绑定/执行验证；独立 Diagnostics-only、Abstractions-only 闭包验证通过，实际缓存 SHA256/SHA512 与冻结 feed 一致。
P1 包资产审计通过：四包 ID/version、Repository URL/commit、README、LICENSE、XML、portable PDB 和指向该提交的 Source Link 元数据。该阶段未验证远端源码/公共符号；后续 P3 实证见下文。
quick-start 的 XML/C# 示例已从 Markdown 原文物化，引用同一开发包运行，输出 `hello`。
新仓首次开启 XML 文档输出出现 16 条既有 XML 注释警告，构建无错误；未借拆仓修改运行时代码或屏蔽这些警告。

P1 独立 reviewer 未发现阻断实现问题；其提出的实际包资产审计缺口已补验。该阶段仅准备 CI/publish；后续 README、tag、发布与消费者验收结果见 P2/P3。
详细日志位于 `E:/repos/Atelia-org/.completion-extraction`；临时日志不是日常构建依赖。

## 发布前身份设置记录

用户首次手动创建并推送新仓时，远端 HEAD 为 `7d19e10c3f7b3dec3981ccf528c446cf94efef8f`；[CI run 34838908288](https://github.com/Atelia-org/atelia-completion/actions/runs/34838908288) 成功。

用户更新 GITHUB_TOKEN 并重启后，`GET /user` 返回 Robird；成功创建 `nuget` environment，并成功设置和回读 repository Actions variable `NUGET_USER=Robird`。此前 token 有效期限制、401 及设置写入 403 的阻碍均已解除；配置完成时尚未触发发布，后续发布结果见下文。

用户已确认 NuGet policy `atelia-completion-publish` 为 Active：owner `Atelia`，GitHub `Atelia-org/atelia-completion`（owner ID `212307724` / repo ID `1369817269`），workflow `publish.yml`，environment `nuget`，允许新包和新版本，pattern `Atelia.*`。已核对 GitHub 实际 ID 和设置一致，继续 P2/P3；发布入口要求从匹配的 `v{version}` tag 手动触发。

## P2/P3 继续实施

隔离候选工作树 `.completion-extraction/atelia-candidate`，分支 `extract/completion`，base 仍为 `c66d84654408321aab00a66aeb53e1dd19a44679`。23 个活动消费者项目的 33 条 Completion/Diagnostics 引用切换为互斥包/源码配置；四源码目录、Completion.Tests、docs/Completion 与五个 solution 项一起迁出。Storage pin 与配置保持不变。

上游发布候选提交 `3ae1ebeccdd94a7bd507444154a283201a68c992`：更新六份发布文档、补自有精确 IVT 测试、保存 CI package smoke 回执；runtime 未变。源码测试 Windows 818 passed / 3 skipped；[CI 34848912437](https://github.com/Atelia-org/atelia-completion/actions/runs/34848912437) Windows/Linux 通过。冻结开发包 `0.1.0-dev.20260914.2` 位于 feed-2，独立包 smoke 通过；旧 dev1 不覆盖。

候选 Release Rebuild 通过（0 warning/error），源码模式 Rebuild 通过（16 条上游既有 XML 注释警告，另 1 条测试可空警告随后修正并重编通过），assets 确认四库均为指向显式绝对根的 project。源码模式 WalkingSkeleton 27、Runtime 73 通过；缺失根、相对根、不完整根、非法开关以及 source pack 均按预期失败。

Windows 同 P0 集合通过，除了相同的 Galatea 9 / MemoPod 11 条 Linux 平台限制。初次多出的 MemoPod 2 条依赖结构断言已改为精确包/源码互斥约束并保留程序集与 IVT 边界；修正后 Windows 架构 8 条、Linux 完整 MemoPod 20 条通过。Linux 的独立物化候选完成 Rebuild（0 warning/error），其余同组 WalkingSkeleton 27、Runtime 73、SessionJournal 123、CLI 6、Galatea 21、三个 public surface 4/7/4 全部通过。

候选本地提交 `b7105dda`；提交 hook 首次自行 restore 找不到本地 dev 包而中止，第二次向进程提供相同 RestoreConfigFile/NUGET_PACKAGES 后完成原有格式化检查，没有绕过 hook。四个改动 C# 文件在 hook 后重新编译，Windows Runtime 73、WalkingSkeleton 27、MemoPod architecture 8 通过。原 main 的计划及 P0/P1 证据单独提交为 `a6cdc271`，未提前合入 dev 包引用。

上游已推送 tag `v0.1.0-preview.1` 指向 `3ae1ebeccdd94a7bd507444154a283201a68c992`，固定文档 HTTP 200。发布 workflow [34850329637](https://github.com/Atelia-org/atelia-completion/actions/runs/34850329637) 成功，OIDC 交换成功，四个 nupkg 与四个 snupkg 均被服务接受。初始索引等待约 6 分钟，未重复发布或更改版本内容。

四个公开 nupkg 均从 V3 实际下载，`dotnet nuget verify --all` 通过；签名包哈希写入仓外 `public-feed/public-receipt.json`，与未签名产包 manifest 分开。`public-only` probe 使用新缓存和仅 nuget.org 的配置运行原 public HTTP/Tools smoke，四个缓存包与下载的签名包哈希一致。

公开 symbol server PDB 与公开 DLL 的 GUID/stamp/checksum 全部匹配：Diagnostics 1、Abstractions 19、Completion 59、Tools 17 个远端源码文件，共 96 个 Source Link 源文件的 checksum 全部通过；另检查 11 个 embedded generated documents。证据在 `.completion-extraction/public-verification/results/0.1.0-preview.1/3ae1ebeccdd94a7bd507444154a283201a68c992/SUCCESS.json`。

实际发布 run 的四份包内 README 均为对应版本，没有未发布占位；7 个固定 tag 链接 HTTP 200，LICENSE/XML/portable PDB/Repository URL+commit 审计通过。文档三个原样 C# 示例此前在冻结候选包中输出 `hello`、`hello|quick-start|1`、`saved:Atelia Tools|1`；公开包另有同一完整 public smoke 证明。

Atelia public pin 提交 `68195d78`，合入 main 的 merge 为 `7918d32b`。候选 public-only restore、Release Rebuild（0 warning/error）及四包 assets/实际签名缓存哈希核对通过。主线普通 `dotnet build Atelia.sln -c Release -t:Rebuild` 通过（0 warning/error），未使用 Prepare 或路径覆盖；九组 Windows 测试共 265 passed / 20 failed，失败用例身份与 P0 逐项完全一致，均为既有 Linux 平台限制。主线默认缓存四包哈希与公开签名包一致。

Linux 最终公开还原首次失败的原因已定位：WSL 直连 api.nuget.org 被 302 到 nuget.azure.cn，镜像尚未同步，返回 404；新 HTTP cache 仍可复现。仅在任务进程使用本机已有 `127.0.0.1:10808` 代理后，原公开 URL 返回 200，公开 restore 和 Rebuild 通过（0 warning/error），四包 assets 和缓存签名包哈希一致。未改仓库 NuGet 配置或系统代理。最终九组 Linux 公开包消费测试共 **285 passed / 0 failed / 0 aborted**，包括 Galatea 21 和 MemoPod 20 全部通过。

最终主线独立 review 通过；活动工程/脚本无遗漏的旧源码引用，四库及迁出测试目录无 Git 跟踪文件；已修正文档的历史状态表述。Storage props、Prepare 脚本、依赖指南相对 c66d8465 无差异，未改 DramaBoard 或 DurableGraph。P0–P3 拆仓交付完成；P4 是尚未实施的独立消费者接入里程碑。
