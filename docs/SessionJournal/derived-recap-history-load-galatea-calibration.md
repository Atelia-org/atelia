# Galatea DerivedRecap HistoryLoad 校准

> 状态：2026-07-31 fresh-import calibration evidence。本文只记录
> content-free aggregates；临时 SessionJournal repo 与完整 JSON report 不进入版本库。

## 输入与流程

输入是当前
`prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded/chat-session-legacy-upgrade-export.json`：

- schema：`atelia.chat-session.legacy-upgrade-export.v1`
- bytes：`1,281,881`
- SHA-256：
  `b71822a27003e8d9f9b9c0ff956ca7c268267aba72221be89df154ed7d4751f3`

在新的 `/tmp` 目录依次执行：

```text
import-legacy-json
validate --branch main
recap history-load inspect --branch main
```

import 生成 148 个 raw events：1 SessionCreated、1 RuntimeConfigSetup、4
SystemPromptSetup、71 Observation、71 imported AgentAction；另有 2 legacy
compaction 与 2 legacy recap 被显式跳过。随后 strict read-only validate 得到：

```text
events                 148
logicalPayloadBytes    474,498
phase                  Idle
```

校准使用：

```text
report schema   atelia.session-journal.recap-history-load-calibration.v1
estimator       atelia.history-load.o200k-base.history-unit-v1
branch          main
branch RefId    000000000400001f
captured head   ej1:00000487000004330000000100000000
baseline        ej1:0000000d2c0000210000000100000000
```

baseline 是 `SessionCreated` exact boundary，因此 calibration window 排除
SessionCreated 以及它之前的两个 setup raw events。后续 3 个 SystemPromptSetup
属于 raw/boundary growth，但不生成 HistoryUnit。

## 结果

总量：

| metric | value |
|---|---:|
| baseline-relative raw events | 145 |
| HistoryUnits | 142 |
| replay-safe boundaries | 145 |
| HistoryLoad | 116,458 |
| canonical rendered UTF-8 bytes | 414,487 |

按 HistoryUnit kind：

| kind | units | HistoryLoad | rendered UTF-8 bytes |
|---|---:|---:|---:|
| Observation | 71 | 20,009 | 74,773 |
| Action | 71 | 96,449 | 339,714 |

单个 HistoryUnit 的 nearest-rank 分布：

| metric | min | p50 | p75 | p90 | p95 | p99 | max |
|---|---:|---:|---:|---:|---:|---:|---:|
| HistoryLoad | 32 | 771 | 1,383 | 1,615 | 1,784 | 2,130 | 2,340 |
| rendered bytes | 119 | 2,814 | 4,790 | 5,914 | 6,237 | 7,788 | 7,843 |

所有连续 fixed-unit window 的 HistoryLoad：

| width | windows | min | p50 | p75 | p90 | p95 | p99 | max |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 20 units | 123 | 12,901 | 17,174 | 17,849 | 18,699 | 18,899 | 19,207 | 19,333 |
| 24 units | 119 | 15,592 | 20,446 | 21,148 | 22,177 | 22,871 | 23,037 | 23,196 |

命令冷进程 wall time 是 2.91 秒，maximum resident set 是 111,532 KiB；这两个
数字包含 `dotnet run --no-build`、repo replay 与 tokenizer 初始化，不能当作纯
estimator benchmark。inspect 前后 repo 文件集合与内容 hash 完全一致，且没有读取
connections/config/Recap Store、创建 Completion client 或调用 LLM。

## R/B 初始候选

这些候选只把 Galatea 当前历史中的 20/24-unit window 分布映射到新量纲，不是
provider token limit，也不是已确定的 production defaults：

| candidate | `MinimumRecentHistoryLoad` R | `RecapBuildIntervalHistoryLoad` B | interpretation |
|---|---:|---:|---|
| median-aligned | 17,000 | 20,000 | 接近两个窗口的 p50，recap 较频繁 |
| balanced | 18,000 | 21,000 | 接近 p75，建议作为下一轮多 fixture 验证起点 |
| continuity-biased | 19,000 | 23,000 | 接近 p95，保留/增长都更大 |

推荐暂以 `R=18,000, B=21,000` 做 H1 focused fixtures 和更多真实 session
对照，但在 H1c production authority cutover 前至少补充短对话、tool-heavy、多语言和异常长
单 unit 数据。HistoryLoad 的意义正是允许相同信息预算对应不同 unit count；这些候选不承诺
“至少保留 20 条”或“恰好每 24 条触发”。

## H2 targeted profile与cache决定

在相同的 142-unit / 116,458-HistoryLoad数据上，直接打开 read-only SessionJournal并把 repo
replay排除在计时区间外。每个 suffix在同一进程中连续执行 30 次 projector measurement：

| baseline-relative suffix | rendered UTF-8 bytes | first/cold ms | warm p50 ms | warm p90 ms | warm allocation p50 |
|---|---:|---:|---:|---:|---:|
| 142 units | 414,487 | 845.08 | 145.04 | 168.95 | 3,909,944 bytes |
| 20 units | 69,641 | 7.09 | 6.48 | 7.85 | 217,976 bytes |
| 40 units | 134,751 | 17.75 | 12.94 | 14.55 | 402,336 bytes |

full first measurement分配 55,771,136 bytes，包含 `o200k_base` tokenizer cold
initialization；同进程后续由现有 process-wide tokenizer复用。production rolling cadence在已有
Published后通常只计量 baseline之后约 20～40 units，其 warm成本约 6～15 ms。

H2据此决定不增加 bounded process cache：当前收益不足以证明 repo identity、stable digest、
容量/逐出、并发与正文驻留风险所带来的复杂性。继续保留 process-wide tokenizer复用和
operation-local prefix；persistent repo-sidecar仍不在当前范围。若将来在线端到端 profile证明
measurement成为瓶颈，再单独设计cache，不从本次测量推导长期兼容承诺。
