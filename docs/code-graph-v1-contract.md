# Code Graph v1 合同

## Schema

代码图合同版本为 `couplet.code_graph.v1`，机器可读快照位于 [`contracts/code-graph/v1/schema.json`](../contracts/code-graph/v1/schema.json)。生产 DTO 位于 `Couplet.Core.Graph`，JSON 只通过 source-generated context 读写。

v1 节点为 `Workspace`、`Repository`、`Project`、`BuildTarget`、`Module`、`Namespace`、`File`、`Type`、`Member`、`Symbol`、`Test` 和 `Chunk`；关系为 `Contains`、`Defines`、`References`、`Calls`、`Imports`、`Inherits`、`Implements`、`Overrides`、`DependsOn`、`Builds` 和 `Covers`。

每个节点和边必须带 stable ID、workspace/source/index revision、adapter/version、content hash、workspace-relative UTF-8 span 和 confidence。`Exact` 只能用于语言语义可证明的结论；`Inferred` 必须给出稳定 rule；未知动态关系使用 `Unknown`，不得补造 exact edge。

## Stable ID

stable ID 使用 `couplet.stable_id.v1` 域、实体类型和逐字段 UTF-8 长度前缀作为 SHA-256 输入，输出前 160 bit 的小写十六进制值。字符串先执行 Unicode NFC；路径统一为正斜杠并拒绝 `.`/`..` 段。

| 实体 | 身份输入 | 变更语义 |
|---|---|---|
| Workspace | canonical repository identity + worktree identity | clone 路径变化不应改变 repository identity；不同 worktree 必须隔离。 |
| File | workspace ID + workspace-relative path | rename 产生新 file ID，旧 ID 在新 generation 删除。 |
| Symbol | workspace ID + language + qualified semantic identity | 仅源码位置移动不改变 ID；签名、容器或重载身份变化产生新 ID。 |
| Chunk | file ID + content hash + ordinal | 正文或稳定分块边界变化产生新 ID。 |
| Relation | source/target ID + relation kind + evidence identity | 平行边由 evidence identity 区分。 |

冻结输入和输出由 `StableIdAndGenerationContractTests` 锁定，不能在同一 major 内静默改变。

## Generation 发布与删除

generation 合同版本为 `couplet.generation.v1`。一个 manifest 同时记录 Document、FullText、Vector 和 Native Graph 计数、producer 版本和 checksum。

1. `Staging` generation 对查询不可见。
2. 所有模型 durable 且不变量校验通过后，SonnetDB 的公共原子能力切换 active manifest；Couplet 不建立第二提交日志。
3. 新查询只租用 `Published` generation；旧 generation 进入 `Retired`。
4. 仅当旧 generation 不是 active、`SupersededBy` 等于 active revision 且 query lease 为 0 时才能进入 `Deleted`。
5. rename/delete/parser 或 model 升级必须在同一新 generation 中清除所有相关派生状态。

source lane 已把跨模型原子发布、snapshot/exact-revision lease、generation-bound cursor 和 lease/cutoff-aware cleanup 接到 SonnetDB 公共合同；默认 package lane 仍只提供 generation-independent staging。真实跨进程 cursor/root 竞争、cursor hard-kill CAS、双客户端、固定硬件容量和长稳门禁尚未完成，因此 CG-005 继续 `verifying`，C1 不得据此宣称通过。
