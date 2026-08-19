# CompletionRequestPrepared v6 implementation candidate evidence

状态：**B1 implementation complete；candidate evidence assembled；independent implementation review PASS；independent evidence/docs review PASS；Ready for Gate B / Gate B Pending；promotion Not Started；B2未授权/未开始；非current/approval/deployment/tag authority**  
记录日期：2026-08-20  
implementation source：`83477c06d75d86eaa57940e7a244fbcc7c1e4e8a`  
implementation tree：`571d10b6be9fa1704e3af811204d5d4de23cb954`

本文记录[Prepared v6 Tier-A Candidate](../work/active/completion-request-prepared-v6-tier-a-amendment.md)
获 Gate A 授权后的 B1 product/tests candidate、可执行 fixture 与验证结果。它不是 final Tier-A contract、
Gate B ledger、deployment evidence 或 tag authority；B2、真实 provider、Galatea MemoPod adapter 与 route activation
均不在本记录范围。

## 1. Exact source and commit ledger

old v5-only reader pin 为
`session-journal-contract-r2-approved-surfaces-v6`：annotated tag object
`acc73dab771b05233f2b0e0fe6ed81081d2f960d`，target
`14b570cb125e40d349c9a50fe11bcc27211ba462`，tree
`b48114d7f8bd170701f6932b24021151e0975702`。Gate A docs baseline
`101a3f9161fca74121ccc1110ee96fe42a5d98f3`与该 old source 在
`prototypes/SessionJournal`、`tests/SessionJournal.Tests`和
`tests/SessionJournal.PublicSurface.Tests`上无差异；old-reader evidence没有复制new parser，也没有用new reader的
compatibility switch伪造old behavior。

| Commit | Kind | Exact semantic unit |
|:--|:--|:--|
| `c7126c25f35ea8d4c3f7f121b3edda7217be9804` | product/tests | Prepared v6 recipe、codec和reconstructor foundation |
| `c77fe8092feb59a93b0223f641cc81df1d8919ce` | product/tests | cross-pair、terminal与strict boundary hardening |
| `c6ee8fc006ac482ca28b0b92b4bfd15db702a408` | product/tests | public supplemental source seam、Observation selection与durable recovery integration |
| `740ca8685b58a9a2a80b62e435d2ef5d0f9b6d97` | tests | recovery、caps、null result和dependency-boundary tails |
| `796bb3133a963667a8ecf33ba14f3ac79cb24f55` | product/tests | closed selection hierarchy hardening |
| `b8ba1bae68181c811e90e581041a1b0b8f61fbb7` | tests | real-writer mixed v5-v6-v5 audit/offline/paged-reader proof |
| `83477c06d75d86eaa57940e7a244fbcc7c1e4e8a` | tests | v5 exact-128 pass与v6 129th-nonterminal rejection |

`796bb313..83477c06 -- prototypes/SessionJournal`为空；最终两项提交只增加acceptance tests，未改变
reviewed product bytes。相对Gate A baseline，最终candidate只修改Candidate §7允许的七个production files；
`SessionRequestManifest.cs`无需变化，`SessionExecutionTailResolver.cs`与v1 recipe owner均未修改。SessionJournal
production ProjectReference没有增加MemoPod、Galatea或RecapGrid。

## 2. Implemented shape and post-review change record

final implementation保持Candidate的provider-neutral语义：`SessionRuntime`只有trailing nullable
`SupplementalContextSource`；source request携带durable Observation address和exact content；result只允许NoMatch或一个
validated exact Observation carrier。Observation path在source call后durable reread并recheck head，source exception、
null result、caller cancellation、cap failure与failpoint都在Prepared之前停止。Prepared/Started recovery和tool
continuation从source Prepared pair重建，source访问为0；prefix仍是Recap → supplemental → dependency-closed raw suffix。

原plan snippet中的`abstract record`经独立review收紧为：

```csharp
public abstract class SessionSupplementalContextSelection {
    private SessionSupplementalContextSelection() { }

    public sealed class NoMatch : SessionSupplementalContextSelection { }

    public sealed class Selected : SessionSupplementalContextSelection {
        public Selected(string exactObservationContent) {
            // Exact null/empty/Unicode guards are part of the implementation.
            ExactObservationContent = exactObservationContent;
        }
        public string ExactObservationContent { get; }
    }
}
```

这是post-review hardening，不是surface扩张：private base constructor消除record合成的protected copy constructor与
外部派生入口；nested outcomes仍closed、sealed、get-only并沿用同名engine pattern。它刻意不承诺record value
equality。

实际test scope另有两项acceptance-driven、test-only扩张：

- `SessionContextCandidateContractTests.cs`锁定SessionJournal production ProjectReference exact allow-list，证明没有
  MemoPod/Galatea/RecapGrid依赖；
- `SessionSelectedLineageAuditTests.cs`覆盖mixed v5-v6-v5的paged selected-lineage consumer且证明不rewrite。

`SessionDependencyClosedFoldSeedTests.cs`和optional `SessionTailContextProjectionTests.cs`均未修改；现有behavior由
engine/reconstructor与route tests覆盖。`SessionJournalAuditScanTests.cs`、
`SessionJournalOfflineValidatorTests.cs`已在原Candidate test scope内，并由mixed-reader tail实际修改。

## 3. Literal wire and hash ledger

以下SHA-256直接对`SessionRequestManifestCodecTests`中由writer exact match的UTF-8 literal计算，不含BOM或LF：

| Fixture | Bytes | SHA-256 | Terminal snapshot content SHA-256 |
|:--|--:|:--|:--|
| v5 / recipe v1 | 2,106 | `4c42375c48979faac926944122145233861ad2350427651cd4ca816c2318d139` | n/a |
| v6 / recipe v2 / NoMatch | 2,047 | `753432bf4a104623e61201ea0d06563ce1a2de6800b7f3420de0920670605998` | `cf54cbc00884e96754d6dd2830d68df4af61413a7bc8e4db45f410d82cbea5d6` |
| v6 / recipe v2 / Selected | 2,078 | `dc3cb5daaac5598db7621cc6f2f3d97a66373df9e504ee244512ce35a24d9ce8` | `1e3581cddf25cda4993abf552120605b0ee0b04ce5606c869582c9610ca813a1` |

v5仍写body v5/recipe v1并接受count 0与128；v6只写body v6/recipe v2，接受mandatory terminal下的count
1与129。v5 count 129、v6 count 0、v6 129个全Recap而无terminal、v6 count 130、cross-pair与unknown
version/recipe均fail closed。whole-request commitment仍由unchanged canonical request codec对最终重建request验证；
上述synthetic manifest的`commitment`字段不是real-request commitment替代品。

## 4. Executable compatibility evidence

### 4.1 Single v6 against the pinned old reader

final source通过public `SendAsync`写入一个v6/NoMatch turn：

- Prepared `ej1:00000002a80000fc0000000100000000`，body schema 6，payload SHA-256
  `3b98f54673faced330ddb1c7aab7c96f1078ef0a9b9f978b095c73f0e8b8bdf4`；
- Started/final head `ej1:000000069c00001b0000000100000000`；6 events；new-reader reconstruction 1；
  fake completion calls 1；supplemental source calls 1；
- new reader成功reconstruct后，state-review runner建立sorted-manifest baseline
  `7d261ddf91a16ad57652f572d13573a0859a3968c45b68d034011de0ba57ea49`；传给old reader的两个recursive
  copies与该baseline repo物理diff均为0。old public harness copy和old CLI copy各自的before/after digest均保持该值；
- physical files：event segment 1,804 bytes / SHA-256
  `967563ca9c671e474a5e01db998e2067bfced86ee21d40868157ca79479462ff`；Ref object 872 bytes /
  `88ea743198928e5edc7c790c9f5ea8b51c4d1c26595a2551f35f1d5bc531fc64`；Ref op 260 bytes /
  `71d7bc710de9ee0327e30c55205c4846265ab20d67b8bf5c3992d6a9c2a1ddda`。

从immutable old source构建的public harness把expected rejection判为成功并exit 0，观测exact exception：

```text
System.NotSupportedException
Unsupported body schema version for session event kind 'CompletionRequestPrepared': actual=6, expected=5.
```

同一old source的CLI以
`validate --input <recursive-copy> --report-json <absent-output>`调用，exit 1、stdout empty、report absent，stderr
exact为：

```text
error: Unsupported body schema version for session event kind 'CompletionRequestPrepared': actual=6, expected=5.
```

stderr末尾有一个LF。这个结果只证明old reader显式Unsupported且不改repo；它不支持把含v6的journal回滚给old binary。

### 4.2 Mixed v5-v6-v5 with the new reader

public real `SendAsync`依次写`[v5, v6 NoMatch, v5]`，final head
`ej1:0000001b5c00006c0000000100000000`，15 events，Prepared reconstruction count 3。三个Prepared为：

| Version | Address | Payload SHA-256 |
|:--:|:--|:--|
| 5 | `ej1:00000002a40001dd0000000100000000` | `34f8922447081edf2ed85bb3d253f346069b18e8cc4e9bdb84ebfc6e775cd8fe` |
| 6 | `ej1:0000000cd80000fc0000000100000000` | `93369f2dc330dd091297e1aff414c8382ac47a67d18c93e82b26f0e929e77fb9` |
| 5 | `ej1:00000013740001dd0000000100000000` | `3156abe6edeb8ddd9de926256d166448e8a3d2f905f5dbb5babfcdd304dd5a81` |

external public checked-audit runner逐event读取actual body version；其
`atelia.session-journal.test-tree-digest.v1` before/after exact为
`e16bf0fbeb3d86513104ce86ee628930b59cbe3213aa937a5eeaa7ad99bbc278`，counter labels为
`fakeCompletion=3`、`contextSelection=12`、`lifecycle=6`、`materialization=0`、`supplemental=1`。committed offline
validation与paged selected-lineage tests各自创建独立repo、验证`[5,6,5]`并只断言各自before/after digest相等；它们不共享
上述literal digest。raw-range test还证明把中间v6 entry伪降为v5会改变range hash。这里的test-tree digest只承诺各fixture
定义的sorted final bytes；不承诺transient syscall、filesystem metadata或不同runtime的physical layout。

## 5. Validation ledger

所有命令在`83477c06`、Linux WSL2 x64、Ubuntu 24.04、kernel
`6.18.33.2-microsoft-standard-WSL2`、.NET SDK `10.0.111` / host `10.0.11`执行。compatibility harness未配置、
构造或调用real provider，也未主动读取secret；本轮未采集network-deny evidence，restore-enabled build不能证明zero network。

| Gate | Exact command / result |
|:--|:--|
| Independent implementation review | exact range `101a3f91^..83477c06`；48/48 focused、519/519 full、4/4 PublicSurface、diff clean；findings 0 |
| Broad B1 focused rerun | 下方exact filter command：230/230 |
| SessionJournal full | `dotnet test tests/SessionJournal.Tests/SessionJournal.Tests.csproj --no-restore -m:1 -nr:false`：519/519 |
| Public surface | `dotnet test tests/SessionJournal.PublicSurface.Tests/SessionJournal.PublicSurface.Tests.csproj --no-restore -m:1 -nr:false`：4/4 |
| Release product build | `dotnet build prototypes/SessionJournal/SessionJournal.csproj --no-restore -c Release -m:1 -nr:false`：0 warnings / 0 errors |
| Final test-only tails | `git diff 796bb313..83477c06 -- prototypes/SessionJournal`：empty；`git diff b8ba1bae..83477c06 -- prototypes/SessionJournal`：empty |

Broad focused gate的exact command为：

```bash
dotnet test tests/SessionJournal.Tests/SessionJournal.Tests.csproj --no-restore -m:1 -nr:false \
  --filter "FullyQualifiedName~SessionSupplementalContextIntegrationTests|FullyQualifiedName~SessionEventCodecGoldenTests|FullyQualifiedName~SessionEventCodecStrictnessTests|FullyQualifiedName~SessionRequestManifestCodecTests|FullyQualifiedName~SessionPreparedRequestReconstructorTests|FullyQualifiedName~SessionPreparedCompletionRecoveryEngineTests|FullyQualifiedName~SessionContextCandidateProviderRouteTests|FullyQualifiedName~SessionEventBodySchemaVersionTests|FullyQualifiedName~SessionJournalAuditScanTests|FullyQualifiedName~SessionJournalOfflineValidatorTests|FullyQualifiedName~SessionSelectedLineageAuditTests"
```

## 6. Remaining boundary

- independent implementation review与independent evidence/docs review均已PASS、findings为0；candidate已Ready for Gate B，
  但Gate B尚未授予；
- 没有生成final contract、Gate B ledger或新tag，旧R2 contract/evidence/tag均未改动；
- v6首次写入后的rollback floor是含exact dual reader的build；v5-only reader只用于证明expected Unsupported；
- B2 Galatea adapter/config、MemoPod访问、真实provider、route activation、migration/rewrite和deployment均未实施；
- evidence hashes证明列明的literal、payload与fixture final bytes，不承诺跨runtime physical byte determinism。
