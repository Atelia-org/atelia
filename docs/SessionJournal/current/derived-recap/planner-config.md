# RecapGrid configuration and recipes

状态：WP-08 formal source cutover Complete。旧 `recap-planner-config` v3 owner已退休；此路径保留为
current documentation redirect，不表示存在compat reader。

Formal configuration分为三类且不互相授权：

1. Control canonical Family/Definition/Recipe values，由operator显式register/activate；
2. Control admission document，独立声明permissions、allowed families/capabilities/carriers/prefixes与预算；
3. Hosting strict connection/route manifests，route以 exact
   `(FamilyDigest, RuntimeProtocolId, SemanticModelId?)` 唯一匹配，禁止fallback；current route language是
   [canonical V2](../contracts/recap-grid-route-manifest-v2.md)，只拥有connection selection与Recap调度。

`recap-grid control compose-full-recipe` 是 provider-free helper：它从fresh exact Control/Timeline authority
与ordered definition digests生成 canonical full recipe create-only output；不会注册、激活、打开provider或
修改Store。`put-family`、`put-definition`、`put-recipe` 与 `activate/promote` 仍是分离的显式mutation。

`recap-grid scaffold`只对code-owned built-in asset生成三份create-only canonical bootstrap files：
Control admission、AgentControl profile、Hosting route manifest。operator必须显式给出permissions、
logical-column prefixes、admission budgets与route dispatch limits；family/capability/carrier来自同一code-owned
registration bundle，不能从payload自授权。三个output先整体验证absent/distinct，再分别在写前/后用正式
decoder exact self-check；该命令不打开provider、Timeline、Control或Store。

Output-token setting不属于route或Recap build budget。Recap request始终省略`MaxTokens`；provider wire需要的
值由selected connection/client负责。需要不同provider policy时使用不同connection id，不在route覆盖。

Build budgets约束 selected rows、recipe-row steps、new calls与elapsed time。Control admission ceilings不是
runtime spent-state。HistoryLoad/partition policy由HistoryTimeline policy拥有，不能从recipe或provider
配置推导。
