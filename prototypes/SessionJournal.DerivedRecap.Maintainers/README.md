# SessionJournal.DerivedRecap.Maintainers

本项目拥有 concrete recap family/member definitions、shared prompt builder 与 output interpreter。
neutral execution contract 位于 `SessionJournal.DerivedRecap.Abstractions`；raw `SessionJournal` 不认识
Maintainer runtime，Store 也不认识 Completion client/model。

## 当前 contract

- `RecapMaintenanceEpochInput` 是 immutable shared input：完整 prior context、同一 history slab、source id
  与可选 token estimate；没有 per-member previous-block payload。
- 成功结果只有 `RecapMaintenanceSuccess.Updated(content)` 与
  `RecapMaintenanceSuccess.KeepUnchanged`。transport、termination 与 output validation failure 走异常及
  Planner typed failure。
- `RecapMaintainerFamilyDefinition` 唯一拥有 shared system prompt 与
  `RecapMaintainerOutputProtocol`；后者唯一拥有 ordered `CompletionOutputContract`、严格 parser 与
  semantic fingerprint。
- `RecapMaintainerDefinition` 只拥有 member id、target、family reference、tail task instruction 与从这些
  immutable values 计算出的 capability fingerprint。member 没有 system/tools/parser override。
- built-in 简体中文 world/autobiography definitions `ReferenceEquals(Family)`，英文 definitions共享另一
  family；两组复用同一 structured output protocol instance。
- catalog 按 frozen member identity索引，并拒绝 semantic fingerprint相同但 object reference不同的
  family copies，防止 runtime family分组静默漂移。

`RewriteRecapBlockMaintainer`把 family system/output contract/prior/history放进 typed prompt prefix，只把
target和member task放进 tail。模型必须恰好调用一次 `recap.submit`：`updated`携带完整替换正文，
`keep-unchanged`要求`content: null`；plain-text response不再是合法结果。

## Fingerprint

`CompletionOutputContract.SemanticFingerprint`由`Completion.Abstractions`统一 canonicalize ordered tools、
完整 tool schemas、tool choice 与 parallel constraint。output protocol fingerprint再绑定 parser schema；
family fingerprint绑定 system prompt、context projection schema与output protocol；member capability v2绑定
implementation、maintainer id、target、family fingerprint和tail task。connection、model、secret、logging与
cache hint都不进入 durable capability。

当前仍是 R2A 过渡切口：`RewriteRecapBlockMaintainer`暂时持有 client/model并使用
`PromptCacheReuseHint.NoReuseExpected`，以保持 production可执行。R2B 将把 dispatch唯一收口到 shared
ExecutionLane/RuntimeGroup；在此之前不要把该过渡构造方式误写成最终 runtime ownership。

Host composition继续通过`RecapMaintainerProfileCatalog`解析 profile metadata并注入 runtime client；Planner
只依赖 neutral Abstractions与 opaque capability，不引用本 concrete assembly。
