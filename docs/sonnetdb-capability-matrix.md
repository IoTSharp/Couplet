# SonnetDB 能力矩阵

本文是 Couplet 对 SonnetDB public capability 的唯一跨仓索引。详细数据库交付和真实状态以 SonnetDB [原生属性图路线](https://github.com/IoTSharp/SonnetDB/blob/main/docs/native-graph-database-roadmap.md)及主 [ROADMAP](https://github.com/IoTSharp/SonnetDB/blob/main/ROADMAP.md) 为准；Couplet 不复制其内部实现待办。

## 联合交付矩阵

| Couplet 需求 | Couplet 交付 | SonnetDB public capability / 编号 | 联调开始 | Couplet 发布 gate |
|---|---|---|---|---|
| workload、语料、schema、SLO | CPL-001~007 | M40 `#341` | 立即同步设计 | 双方冻结相同 manifest/合同后 C0 PASS |
| generation/cursor/recovery | CPL-002、CPL-013~015 | M40 `#343/#346` 的 snapshot lease、cursor、atomic/recovery/invariant 公共地基 | 目标 API 可调用 | CG-005 关闭后 C1 PASS |
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

### C0 当前实现状态

- 固定 `SonnetDB.Core 3.1.0` 官方 package 和 content hash，公开 `GraphStore`、KV snapshot、Document、FullText、Vector、Graph path budget 与 diagnostics API 可用于联调。
- `couplet.sonnetdb_handshake.v1` 分开报告 `integration_state` 与 `release_level`；public API 存在时前者可为 available，后者在联合门禁通过前仍为 unavailable。
- `generation.atomic_publish` 和 `hybrid.shared_plan` 尚无已验证的 Couplet public 接线，分别由 CG-005、CG-002 阻塞。
- 八个 MCP schema 和 stdio 协议已就绪，但索引/图/混合工具继续返回稳定 `capability_unavailable`。联合版本记录见 [`contracts/c0-handshake.v1.json`](../contracts/c0-handshake.v1.json)。

## 变更规则

- SonnetDB public contract 只能按兼容策略扩展；Couplet 固定 package version，不跟随浮动 main。
- 本地 opt-in `ProjectReference` 不进入默认仓库配置，且必须通过与固定 package 相同的 compatibility tests。
- 新增 Core 缺口先登记 [Capability Gap Catalog](capability-gaps.md)，再在本表加入 owner/编号；关闭需要 Core 回归、Couplet journey 和固定硬件证据。
- `#352/#359/#367` 是双方联合退出门禁，不是禁止 Couplet 针对前序 public API 开发/联调的条件。
