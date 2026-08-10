# ADR 0002：原生属性图无旁路

- 状态：Accepted
- 日期：2026-08-10

## 背景

代码知识的核心关系包括定义、引用、调用、继承、包含、依赖和测试覆盖。若在原生属性图尚未可用时先用关系边表、内存邻接表或应用层多跳遍历交付，会形成第二套 schema、执行器、恢复语义和性能基线；后续通常无法无成本替换，也无法证明用户实际使用的是 SonnetDB 原生图。

## 决策

- 代码关系的唯一持久化与查询实现是 SonnetDB Native Graph public API。
- Couplet 不创建可遍历的关系边表，不保存 shadow adjacency，不加载全图执行 BFS/DFS/shortest path，也不接入第二个图数据库。
- 关系工具必须通过 capability handshake 确认 GraphStore、目标算子、快照/恢复和诊断门禁已通过；否则返回 `capability_unavailable`。
- 单跳和多跳查询必须由 SonnetDB 原生邻接与路径算子执行，并返回 actual access path、expanded edges、frontier peak、fallback 和预算计数。
- Graph capability 缺失或不达 SLO 时，在 SonnetDB M40 登记并优先补全。Couplet 可以继续开发 Git、parser、合同和非图检索，但不能对外宣称关系/影响能力可用。

## 允许与禁止

允许在 Document/Table 中保存用于显示、审计或 eval 的 relation ID/摘要；它们不能成为关系查询或恢复的真相源。允许语言适配器在单文件解析过程中用临时 AST/semantic model 产生关系事件；事件提交后不得保留为可跨请求遍历的图。

禁止：

- `Edges(from, kind, to)` 一类关系表替代 GraphStore。
- 在 Couplet 中逐跳调用单邻接接口拼出 BFS/DFS，而 Core 已缺少受预算路径算子。
- 为“兼容”隐藏 full scan、全量物化或提高默认 frontier/timeout。
- 双写 SonnetDB Graph 与另一图存储作为 fallback。

## 结果

Couplet C2 可在 SonnetDB `#347~#351` API 可用后联调，由双方工作负载共同关闭 `#352` Preview gate；C3 与 `#353~#358` 联调后共同关闭 `#359` Beta gate；C4 与 `#360~#366` 并行取证后共同关闭 `#367`/1.0 gate。gate 未通过只允许内部联调或较低能力等级，不能用临时方案发布。初期功能会少于采用旁路的版本，但只保留一套长期正确的存储、恢复、诊断和性能合同。
