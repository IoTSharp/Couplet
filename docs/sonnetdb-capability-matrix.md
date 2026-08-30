# SonnetDB 能力矩阵

本文是 Couplet 对 SonnetDB public capability 的唯一跨仓索引。详细数据库交付和真实状态以 SonnetDB [原生属性图路线](https://github.com/IoTSharp/SonnetDB/blob/main/docs/native-graph-database-roadmap.md)及主 [ROADMAP](https://github.com/IoTSharp/SonnetDB/blob/main/ROADMAP.md) 为准；Couplet 不复制其内部实现待办。

## 联合交付矩阵

| Couplet 需求 | Couplet 交付 | SonnetDB public capability / 编号 | 联调开始 | Couplet 发布 gate |
|---|---|---|---|---|
| workload、语料、schema、SLO | CPL-001~007 | M40 `#341` | 立即同步设计 | 双方冻结相同 manifest/合同后 C0 PASS |
| generation/cursor/recovery | CPL-002、CPL-013~015 | 最新源码 `Tsdb.Generations` 的 atomic publish、query lease、cursor 与 cutoff cleanup | source ProjectReference 及 exact/fulltext/`symbol_get` 查询已可调用；fulltext `code_search` 同进程 active/retired cursor/no-scan 与真实子进程 commit 边界已接线，CG-007 API/接线已关闭 | 进程重启 cursor、实时 watcher、双客户端与固定硬件 capacity 关闭 CG-005 后 C1 PASS |
| 原生节点/边/邻接/属性索引 | CPL-020~021 | M40 `#347/#348` | 对应 API 可调用 | 纳入 `#352` correctness/recovery gate |
| 流式 BFS/DFS/path/预算 | CPL-022~024 | M40 `#349/#350` | 对应 API/diagnostics 可调用 | `#352` + Couplet C2 全 PASS 后 Preview |
| Server/SDK/import parity | Couplet embedded adapter/MCP | M40 `#351` | typed SDK/embedded contract 可调用 | `#352` 联合报告 |
| Graph logical/physical plan | CPL-032 | M40 `#353~#358` | typed plan 可表达目标 intent/filter/budget | 纳入 `#359` |
| FullText/Vector/Graph hybrid | CPL-030~033 | M40 `#359`，关联 M35/M36 | 共享 candidate/plan API 可调用 | `#359` + Couplet C3 全 PASS 后 Beta |
| 生产快照、维护、知识组合 | CPL-040~043 | M40 `#360~#366` | 分项能力可调用 | 纳入 `#367` mixed workload |
| 生产容量与发布 | CPL-043~044 | M40 `#367` | 双方并行跑相同 manifest | `#367` + Couplet C4 全 PASS 后 1.0 |

## Capability handshake

CPL-003/CPL-007 实现启动 handshake，至少交换：

- `sonnetdb_core_version`、文件/schema compatibility 和 Native AOT/trim capability。
- KV/Document/FullText/Vector/Graph/Hybrid 各 capability ID、level（unavailable/preview/beta/production）和 contract version。
- snapshot lease、forward cursor、atomic batch、generation filter、path budgets、shared hybrid plan 和 diagnostics 是否可用。
- 当前已知 blocking gap 与可安全执行的能力集合。

Couplet 只按 handshake 开放工具。版本号较新不等于 capability 自动可用；能力未达到路线 gate 时返回 `capability_unavailable`。

### 当前实现状态

- 默认 lane 固定 `SonnetDB.Core 3.1.0` 官方 package 和 content hash；显式 `UseSonnetDbSource=true` lane 直接 ProjectReference 最新源码，restore lock 隔离在 `obj/`。
- `couplet.sonnetdb_handshake.v1` 分开报告 `integration_state` 与 `release_level`；public API 存在时前者可为 available，后者在联合门禁通过前仍为 unavailable。
- source lane 的 `generation.atomic_publish`、cutoff cleanup 与 exact/fulltext/`symbol_get` query 已有 Couplet runtime 小型接线；fulltext `code_search` 已支持同进程 active/retired generation cursor、path/language/entity-kind 过滤并证明不走 Document scan，真实 CLI 子进程 commit 前后强杀/重开只暴露完整 revision。release level 仍被进程重启 cursor、实时 watcher、双客户端和固定硬件 capacity 的 CG-005 联合门禁阻塞。`hybrid.shared_plan` 继续由 CG-002 阻塞。
- 八个 MCP schema 和 stdio 协议已就绪；source lane 在显式绑定数据库时可通过 `workspace_status` 读取 active generation 状态，并以 Preview 等级执行 `code_search` exact/fulltext 与 `symbol_get`；C2/C3 工具继续返回稳定 `capability_unavailable`。联合版本记录见 [`contracts/c0-handshake.v1.json`](../contracts/c0-handshake.v1.json)。
- C1 已通过固定 package 建立 generation 独立 Document collection、stable ID/path/qualified identity indexes 与 FullText index，批量写入、索引一致性、计数、checkpoint 和 reopen 均有自动化证据。
- `code_search` Preview 实际走 `generation_active_lease:*` 或续页时的 `generation_retained_cursor_lease:*` access path；fulltext cursor 使用受预算约束的 Top-K + offset、默认两分钟绝对 TTL 和 128 个进程内 slot，超预算/容量 fail fast，不使用 Document scan。`symbol_get` 走 generation-bound `by_stable_id` 或最多读取两条的 `by_qualified_identity`，并通过公开 MCP typed 响应暴露 source evidence 与诊断。它们不替代尚缺的进程重启 cursor、实时 watcher、固定硬件 capacity 或双客户端发布门禁。
- Codex 与 Claude Code 双客户端回归已验证三个 C1 MCP 工具在只有 staging 时稳定返回 `CG-005/generation_publish_blocked`，响应不含 staging items。
- 默认 package public API 仍不能组合 generation 不变量；source lane 使用 Core 单一 `Tsdb.Generations` catalog，未建立应用层第二提交日志。CG-005 状态为 verifying。
- Medium/Large 已完成一次真实 characterization；Large initial、两档 100-file 变化和两档 peak RSS 均未达目标，Correctness/Recovery 与 Performance/Capacity 均保持 FAIL。详细数据见 [`c1-capacity-evidence.md`](c1-capacity-evidence.md)。
- 默认 package 的 win-x64 Native AOT 继续关闭不兼容 worker并报告 CG-006；最新 source 已修复 worker shutdown。普通 source/JIT handshake 只报告 `source_workers_enabled`，不再从 `UseSonnetDbSource` 外推 AOT 已验证；Native AOT 进程报告 `source_aot_workers_enabled_pending_soak`。2026-08-29 的 source CLI publish/no-op runtime smoke 已通过，本轮 CLI、Daemon、MCP Server source publish 均为 0 个未处置 IL/AOT warning，7 天长稳仍需归档。

## 变更规则

- SonnetDB public contract 只能按兼容策略扩展；Couplet 默认 package version 固定，跨仓开发显式选择最新 source lane。
- opt-in `ProjectReference` 是仓库内受测配置，但不改变默认 package restore/build；两条 lane 分别验证且不得共用 lock file。
- 新增 Core 缺口先登记 [Capability Gap Catalog](capability-gaps.md)，再在本表加入 owner/编号；关闭需要 Core 回归、Couplet journey 和固定硬件证据。
- `#352/#359/#367` 是双方联合退出门禁，不是禁止 Couplet 针对前序 public API 开发/联调的条件。
