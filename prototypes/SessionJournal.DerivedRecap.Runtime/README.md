# SessionJournal.DerivedRecap.Runtime

Completion-backed DerivedRecap runtime binding layer。

- `RecapExecutionLane`唯一拥有一个runtime route的raw client、model、max tokens、invocation options与send入口；
  recap调用使用`ReuseExpectedSoon`，每个call通过`RecapCallContext`记录member/target/source/family、
  leader/follower以及dispatch/lane wait attribution。
- `RecapExecutionLaneInterner`是lane的public受控创建入口，按opaque route object reference复用lane，不使用
  value equality，并拒绝同一route reference漂移到不同raw client/model/max/logging policy。
- `RecapRuntimeGroupInterner`按exact `(lane reference, family reference)`复用sealed group。
- `RecapRuntimeGroup.Bind`是`BoundRecapBlockMaintainer`的public受控创建入口，验证member与group引用同一个
  family；binding只能使用family prefix/parser、member tail与lane dispatch，并把group作为neutral opaque
  `RuntimeGroupAffinity`暴露给Planner scheduler。每个operation-local group execution只构造一个共享的
  `CompletionPromptPrefix`，member只能追加自己的tail。

Planner和Store都不引用本assembly。Galatea/CLI只在deferred registry第一次真实lookup后创建lane/group/binding；
Planner并行启动各group leader，leader settle后释放同组followers；lane以共享cap和leader优先级做admission。
provider cache wire mapping与usage telemetry由Completion adapters拥有，不进入durable recap identity。
