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
| CG-005 | 跨模型 generation filter、active manifest 原子切换、query snapshot/lease、稳定 cursor、旧 generation 清理及 crash/reopen 合同尚未联调证明 | SonnetDB M40 `#343/#346` + Couplet CPL-002/CPL-013/CPL-015 | Couplet C1 发布及后续 | known | 可开发 parser/fixture 和 staging；未关闭前不宣称原子、可恢复的增量索引完成，也不引入第二提交日志 |

上述是已知前置能力，不是实测性能结论。首个 C0/C1 benchmark 产生后，任何未达 [质量与性能门禁](quality-gates.md)的路径必须新增独立 gap，不得只在日志或 PR 评论中记录。

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
