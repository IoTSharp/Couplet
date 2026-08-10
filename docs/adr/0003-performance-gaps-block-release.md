# ADR 0003：性能缺口阻塞发布

- 状态：Accepted
- 日期：2026-08-10

## 背景

代码索引和 Agent 查询会遇到大仓库、高扇出公共符号、深依赖链、频繁编辑和有限 token 预算。只有功能结果正确但依赖全库扫描、无界物化、长锁或过量 I/O 的实现，在真实工作流中仍不可用。把性能问题留作发布后的可选优化，会迫使产品层引入缓存、旁路和错误的容量承诺。

## 决策

1. correctness/recovery 与 performance/capacity 是两个独立 PASS/FAIL gate；任一失败都阻塞对应阶段。
2. C0 在实现前冻结 corpus、硬件、指标、SLO 和 Agent paired-eval 口径。目标不能根据候选发布结果事后降低；变更需要新 ADR、证据和旧目标对比。
3. 修复顺序固定为：
   - 正确性、原子性、快照与恢复；
   - 有界内存/锁/取消，消除非预期全扫、全量物化和重复遍历；
   - 固定硬件容量、P95/P99、分配和 I/O 放大；
   - 新 API、客户端集成、UI 和产品文案。
4. 通用 SonnetDB 缺口回收到实际 Core 里程碑，并阻塞 Couplet；代码/语言/MCP 特定缺口由 Couplet 优先处理。
5. 增加超时、内存、frontier、批大小或结果上限不是性能缺口的关闭证据。

## 必需证据

- commit、corpus manifest、硬件/runtime/config 和 cold/warm 口径。
- throughput、P50/P95/P99、RSS/working set、managed allocation、WAL、逻辑/物理 I/O 和恢复时间。
- candidates/examined/returned、expanded edges、frontier peak、actual access path 和 fallback reason。
- 同规模、多规模复杂度对比，证明 exact/adjacency 路径随命中集合而不是全库增长。
- SonnetDB 修复回归、Couplet golden journey 和固定硬件复测三者全部通过。

## 结果

- 路线日期服从证据门禁，不以赶版本为由绕过。
- 性能 gap 是正常的一等工程交付，优先级高于扩充语言、工具数量或产品包装。
- 对尚未出现的问题仍坚持证据驱动，不预建复杂缓存或分布式机制；一旦受支持 workload 稳定复现缺口，就不能以“未来优化”延期。
