# Capability Gap Catalog

本目录记录阻塞 Couplet journey 的真实能力或性能缺口。它不是普通 feature wishlist；每个 gap 必须有可复现输入、责任边界、阶段影响和关闭证据。

## 状态定义

- `known`：边界已确认，尚未进入实现或证据不足。
- `active`：有稳定复现，正在责任仓库处理。
- `verifying`：责任仓库已有修复，等待 Couplet 回归/容量复测。
- `closed`：Core 回归、Couplet journey 和目标规模证据全部通过。
- `deferred`：对应能力从公开范围移除；不能在仍声明支持时使用此状态。

## 当前目录

| ID | 缺口 | Owner / 路线 | 阻塞 | 状态 | 允许行为 |
|---|---|---|---|---|---|
| CG-001 | `SonnetDB.Core 3.1.0` 已含公开原生图 API，但 Couplet 图 workload 与 `#352` 恢复/容量联合门禁尚未通过 | SonnetDB M40 `#352` + Couplet C2 | Couplet C2 Preview 发布及后续 | known | 允许针对固定 package API 编译联调；图工具对外仍返回 `capability_unavailable`，双方 correctness/recovery 与 performance/capacity 证据共同关闭缺口 |
| CG-002 | FullText + Vector + Native Graph 的 shared typed hybrid plan 和实际访问路径尚未通过 | SonnetDB M40 `#353`-`#359`，关联 M35/M36 | Couplet C3 Beta 发布及后续 | known | 允许对目标 API 联调；不得产品层多路 merge/扩图，双方证据共同关闭 `#359` |
| CG-003 | 生产图快照、维护、7 天长稳、Native AOT 与固定硬件发布证据尚未通过 | SonnetDB M40 `#360`-`#367` | Couplet C4/1.0 发布 | known | 与 `#360~#366` 并行取证；保持 Preview/Beta/未发布，双方证据共同关闭 `#367` |
| CG-004 | C0 曾缺少可执行 typed MCP、fixture/eval runner 和固定 package | Couplet C0 | C0 合同门禁 | closed | 2026-08-25 由固定 package、八工具 schema、stdio smoke、35 个测试和 C0 evidence 关闭；索引能力属于 C1，继续 unavailable |
| CG-005 | 最新 SonnetDB `Tsdb.Generations` 已可联调；Couplet 尚缺完整 MCP active query、真实进程故障和容量联合门禁 | SonnetDB M40 generation contract + Couplet CPL-013/CPL-015 | Couplet C1 发布及后续 | verifying | source lane 可执行 fenced publish/lease/cursor/cutoff cleanup、`workspace_status` 和 exact/fulltext Preview；默认 package 仍仅 staging，symbol/query cursor 继续 unavailable，不引入第二提交日志 |
| CG-006 | `SonnetDB.Core 3.1.0` 默认 background workers 的 Native AOT shutdown 不兼容；最新 source 已修复并通过 Core/Couplet smoke | SonnetDB Core lifecycle + Couplet CPL-007/CPL-015/CPL-043 | 默认 package AOT 与完整 source 长稳证据 | verifying | 默认 package AOT 继续关闭 worker并报告 limitation；source lane 保留默认 worker，待长稳归档 |
| CG-007 | generation public cleanup 曾不能按 durable publish-time cutoff 选择 retired generation | SonnetDB 通用 generation lifecycle API + Couplet CPL-015 | API/接线阻塞已解除；C1 固定硬件增长门禁仍归 CG-005 | closed | source lane 使用 Core cutoff overload；固定 package lane 保持 3.1.0 staging，不直接删资源或建立第二套生命周期 |

上述状态以公共能力边界为主；C1 Medium/Large characterization 已把未达门禁的性能结果附到 CG-005 复现语料，但尚不足以把每个性能失败归因为特定 Core 缺口。进一步最小复现确认责任边界后，通用 Core 问题必须登记独立 gap，Couplet 产品层问题必须直接修复，不得只在日志或 PR 评论中记录。

### CG-005：C1 generation 发布与查询租约

- 状态：verifying
- 首次稳定复现：2026-08-25 / C1 working tree based on `329519d`
- Owner：SonnetDB M40 `#343/#346` public contract + Couplet CPL-013~015 integration
- 阻塞阶段：C1 Correctness/Recovery、C1 Performance/Capacity，以及 C1 后续所有公开查询
- Corpus：Couplet 自身工作区、自动化 C#/TypeScript 小型 fixture，以及 `fixtures/c1/capacity-manifest.v1.json` 冻结的 Medium（1m LOC / 100k symbols）和 Large（10m LOC / 1m symbols）双语言语料；manifest SHA-256 为 `38f906b9b65f88e11bb2953fa2ee45e97105815c13d6b8a364230da1ee9fb1b4`
- 操作：构建 generation 独立 Document collection、path indexes 和 FullText index，批量写入后 checkpoint/reopen；执行 initial、100-file 变化、exact/FullText warm query 与 consistency reopen；随后尝试建立 active generation publication/query lease/cleanup 生命周期
- 预期：跨模型派生状态以单一原子 active revision 发布，查询绑定 lease/cursor，retired generation 仅在租约归零后清理
- 实际：固定 package 只能完成 staging。source lane 已将 planning KV、Document 与 FullText 交给 `Tsdb.Generations`，小型回归覆盖 publish/reopen/no-op、active lease、cursor stale、writer fence、lease/cutoff-aware cleanup、publish 后 cleanup failure 隔离、不扫描 Document 的 `workspace_status`，以及 exact stable-ID/path-index 与 fulltext active-lease MCP 查询；`symbol_get`、查询 cursor、fulltext filter plan、实时 watcher freshness 和 publish 前后真实进程故障尚未接线。既有 Medium/Large 数据来自全量 staging，不能外推 source generation 性能。
- 最小复现：`Stage_WithCSharpAndTypeScript_PersistsVerifiedUnpublishedGenerationAcrossReopen`、`Plan_AfterRealGitBranchSwitch_RebuildsAndKeepsStagingGenerationsIsolatedAcrossReopen`、`InspectStaging_AfterMissingOrCorruptCompletionMarker_RejectsGenerationAndAllowsDeterministicRestage`；CLI `index-stage` 报告 `published=false`、`blocking_gap=CG-005`；`c1-capacity --scale medium|large` 产生 `couplet.c1_capacity_evidence.v1`
- 禁止旁路：不得由 Couplet 增加第二提交日志、将 staging 暴露为 active、用不受租约保护的 collection 指针模拟发布，或在 MCP 中隐藏 revision 不连续
- 关闭证据：已有 SonnetDB public API/Core 回归和 Couplet 小型 cursor/lease/cutoff cleanup、`workspace_status`、exact/fulltext Preview 回归；仍需 kill-before/after-publish、实时 watcher、symbol/query cursor、Codex/Claude Code 发布后查询一致性及 Medium/Large 固定硬件双门禁共同 PASS

### CG-006：Native AOT 后台维护生命周期

- 状态：verifying
- 首次稳定复现：2026-08-25 / C1 working tree based on `329519d`
- Owner：SonnetDB Core background worker lifecycle + Couplet CPL-007/CPL-015/CPL-043 integration
- 阻塞阶段：C1 Native AOT 发布证据、C4 long-running maintenance/Production
- Corpus：单文件 C# 临时工作区；win-x64 Native AOT `Couplet.Cli index-stage`
- 环境：Windows win-x64、.NET SDK `10.0.400`、固定 `SonnetDB.Core 3.1.0`
- 操作：使用默认 `TsdbOptions` open/stage/checkpoint/dispose `Tsdb`
- 预期：staging 完成后数据库正常 dispose，后台 flush/compaction/retention/expirer/cleanup 有可取消的 AOT-safe shutdown
- 实际：固定 3.1.0 package 仍复现并使用禁用 worker 的显式限制路径；最新 SonnetDB source 已修复 worker shutdown 并通过 Core win-x64 Native AOT 三 worker实跑。Couplet source win-x64 Native AOT CLI 首次 generation publish 与 unchanged no-op 均退出 0、无 CG-006 limitation；7 天长稳尚待归档。
- 最小复现：发布 win-x64 Native AOT CLI 后执行 `index-stage --workspace <one-file-workspace> --database <empty-dir>`；默认 package 配置在退出时崩溃
- 禁止旁路：不得吞掉 dispose 异常、跳过 `Tsdb.Dispose()`、把后台维护已关闭伪装为 Production，或以定期重启隐藏增长
- 关闭证据：SonnetDB AOT-safe worker shutdown 回归、Couplet AOT staging/daemon reopen、compaction/retention/KV maintenance correctness 和 7 天增长报告共同 PASS

### CG-007：generation retention 选择性 cleanup

- 状态：closed（2026-08-30，关闭范围为 public API 表达能力与 Couplet source 接线；不代表 C1 容量门禁通过）
- 首次稳定复现：2026-08-30 / Couplet C1 source generation integration
- Owner：SonnetDB 通用 generation lifecycle extend-only public API + Couplet CPL-015 integration
- 原阻塞阶段：C1 retention correctness 与持续发布数据库增长；剩余固定硬件/长稳增长证据继续由 CG-005/C4 门禁跟踪
- Corpus：同一 workspace stream 连续发布 revision 1、2、3；revision 3 active，revision 1 的 durable `PublishedAtUtc + retention` 已到期，revision 2 尚未到期；随后关闭并重开数据库
- 环境：最新 SonnetDB source lane；零 retention 与非零 retention；可控 Couplet `TimeProvider`；有/无 revision 1 query lease
- 操作：Couplet 用可控 `TimeProvider` 计算 inclusive UTC cutoff，并调用 `CleanupRetired(stream, DatabaseGenerationCleanupOptions, token)`；零 retention 继续调用既有立即清理 overload
- 预期：只删除已到期且非 active、无 lease 的 revision 1；保留未到期 revision 2；有 lease 时 revision 1 继续 deferred；零 retention 可立即清理全部 eligible retired generations
- 实际：Core 在 generation 临界区内按 durable `PublishedAtUtc <= cutoff` 重新选择 retired 候选，并分别返回 removed、lease-deferred 和 retention-deferred revisions。Couplet source lane 报告相同三类结果，删除 staging marker 只跟随真正 removed revisions；mixed-age 重开得到 `removed=[1]`、`retention_deferred=[2]`、`active=3`。
- 最小回归：`CleanupRetired_AfterReopenWithMixedAges_RemovesOnlyDueGeneration`；另覆盖非零 offset 的 UTC inclusive cutoff、lease release、预取消、注入失败后 retry、`TimeSpan.MaxValue` cutoff clamp，以及连续六次零 retention publish 后 catalog 只保留 active revision
- 禁止旁路：不得由 Couplet 直接删除 generation catalog/Document/KV/FullText 资源，不得用进程内 timer 或长期 lease 代替 durable policy，不得等“全部 retired 都到期”后整批删除，因为持续发布会造成旧 generation 无界滞留
- 已交付合同：SonnetDB 以 extend-only overload 接受 `published_before_utc` cutoff，在同一 schema/generation 临界区内重新检查候选仍非 active、仍满足 cutoff 且 lease 为零，并返回 removed、lease-deferred 与 retention-deferred revisions
- 关闭证据：SonnetDB Core 的 zero/not-due/due/mixed-age/reopen/lease/concurrent-publish 与 public API 兼容性回归，加上 Couplet source lifecycle/CLI/wire 回归。目标硬件 Medium/Large 和 7 天增长仍是独立 FAIL gate，不以本 gap 关闭替代

## Gap 模板

新增条目至少包含：

```markdown
### CG-NNN：标题

- 状态：known | active | verifying | closed | deferred
- 首次发现：YYYY-MM-DD / Couplet commit
- Owner：Couplet 或 SonnetDB 里程碑/编号
- 阻塞阶段：C0-C4、Preview/Beta/Production
- Corpus：manifest、revision、language、规模
- 环境：CPU/RAM/NVMe/OS/runtime/config
- 操作：typed request、预算、并发、cold/warm
- 预期：正确性不变量与冻结 SLO
- 实际：结果、P50/P95/P99、RSS、分配、I/O、access path/counts
- 最小复现：自动化测试或 benchmark 命令
- 禁止旁路：明确不能采用的替代路径
- 关闭证据：owner 修复 commit + Core 回归 + Couplet journey + 固定硬件报告
```

## 归属规则

| 缺口类型 | 默认 owner |
|---|---|
| GraphStore、邻接、属性索引、路径、图快照/恢复/计划 | SonnetDB M40 |
| KV snapshot lease、cursor、atomic batch、checkpoint/compaction、锁和通用分配 | SonnetDB M40 公共地基或实际 Core 性能里程碑 |
| Document/FullText/Vector/filtered ANN/Hybrid Search/派生索引生命周期 | SonnetDB M32/M35/M36 或实际公共执行里程碑 |
| 共享关系计划、scan/物化、算子内存和访问路径诊断 | SonnetDB M41 或实际 Core 里程碑 |
| Git、语言解析、代码 schema、embedding provider、对 Core 统一候选的 context selection/rerank（不得重新召回）、context pack、MCP/Agent 接入 | Couplet C0-C4 |

归属不确定时先构造最小复现：如果脱离代码领域仍能复现，优先归 Core；如果只涉及语言/产品语义，归 Couplet。责任争议不能解除发布阻塞。
