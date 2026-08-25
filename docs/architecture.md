# Couplet 架构

## 1. 目标与边界

Couplet 负责把一个本地代码工作区转换为可供编码 Agent 查询的、带 revision 和源证据的代码知识。它拥有代码领域逻辑，不拥有通用数据库实现：

- **Couplet 拥有**：工作区/Git 识别、语言适配、代码 schema、增量调度、embedding 执行、检索意图、对 Core 已返回的有界统一候选做 context selection、MCP、CLI/daemon 和 Agent 集成。
- **SonnetDB 拥有**：KV、Document、FullText、Vector、原生 Graph、Hybrid Search 的持久化与查询，候选访问/去重/融合/原生图扩展、事务、快照、WAL、checkpoint、backup/recovery、执行计划、资源治理和通用性能。
- **依赖方向**：`Couplet -> SonnetDB.Core`。Couplet 只使用版本化公开 API，不读取内部 key layout，不复制 Core 代码；SonnetDB 不包含代码语言、MCP 或 Agent 产品逻辑。两个仓库保持同级独立，不互相作为 Git submodule。默认构建固定 `SonnetDB.Core 3.1.0` package 和 content hash，不依赖相邻源码 checkout。

## 2. 逻辑组件

```text
MCP clients / CLI
        |
  Protocol Host
        |
 Query & Context Service --------------------+
        |                                    |
 Workspace Coordinator                  Capability Health
        |                                    |
 Git Snapshot -> Language Adapters -> Incremental Indexer
                                         |
                                  SonnetDB Adapter
                                         |
    +-------------+-----------+-----------+----------+----------+
    |             |           |           |          |          |
    KV         Document    FullText     Vector    Native Graph  Metrics
```

| 组件 | 职责 | 不得承担的职责 |
|---|---|---|
| Protocol Host | MCP 版本协商、typed validation、取消、deadline、分页与错误映射 | 数据库查询逻辑、静默重试不同语义路径 |
| Workspace Coordinator | canonical workspace、Git/worktree、ignore、安全策略和 index freshness | 解析语言语义或遍历关系 |
| Language Adapter | 从文件快照产生 symbols、spans 和带 evidence/confidence 的 relations | 直接写数据库、跨 revision 缓存不可验证状态 |
| Incremental Indexer | 计算变更集、分批构建 generation、原子发布和垃圾回收 | 自建持久存储或内存图真相源 |
| Query & Context Service | 提交 exact 或 shared hybrid typed plan 的意图/过滤/预算，并从 Core 返回的有界统一候选选择 context pack | 在产品层合并多路召回、重新召回、逐跳扩图或加载全库候选 |
| SonnetDB Adapter | 把代码 schema 映射到公开多模型 API，暴露实际计划/计数 | 访问 SonnetDB 内部 key、WAL 或文件格式 |
| Capability Health | 报告版本、可用能力、阻塞 gap、重建原因和降级级别 | 把未知或未验证能力报告为 ready |

## 3. SonnetDB 模型映射

| 数据 | 权威模型 | 示例 | 说明 |
|---|---|---|---|
| workspace/revision/generation/config | KV | active generation、parser/model version、checkpoint | 只保存小型控制面状态 |
| 文件、chunk、符号详情和证据 | Document | path、hash、span、signature、language | 大正文按预算取片段，不复制无关构建产物 |
| 标识符、注释和源码词项 | FullText | exact identifier、token、phrase | 实际 access path 必须可见 |
| 语义 chunk embedding | Vector | content hash + model/version -> vector | 本地默认，生命周期跟随 generation |
| 代码实体和关系 | Native Graph | Symbol、File、Calls、References、Covers | 唯一关系真相源；必须使用原生邻接 |
| eval/运行摘要 | Table 或 Document | corpus/run/metric manifest | 原始大 artifact 放发布证据，不塞入图属性 |

Table、Document 或 KV 中可以保存关系对象的引用或展示摘要，但不能保存一套可独立遍历的边集合。

## 4. Workspace 与 revision

`workspace_id` 由规范化仓库身份和 worktree 身份生成，不能只依赖可变化的绝对路径。每次可查询快照包含：

- `source_revision`：Git commit；工作区有未提交变更时还包含确定性的 dirty content digest。
- `index_revision`：Couplet 单调生成的已发布 generation。
- `schema_version`、各 language adapter/version、embedding model/version。
- 本次包含、排除、失败和待处理的文件计数。

默认数据库位于平台应用数据目录，并以 `workspace_id` 隔离；除非用户显式配置，不向代码仓库写索引目录。

## 5. 增量发布协议

Couplet 不假设 SonnetDB 当前提供跨所有模型的单事务。索引采用 generation 发布协议：

1. 固定输入文件清单、Git/dirty revision 和配置，生成新的 `index_revision`。
2. 所有 Document、FullText、Vector 和 Graph 记录携带该 generation 或指向其稳定实体 ID。
3. 在未发布 generation 中执行解析、写入、索引校验和图不变量检查；查询仍租用旧 active generation。
4. 所有需要的模型 durable 后，以 KV 原子更新 active generation 与 manifest checksum。
5. 新查询只读取新 generation；旧 generation 在无 query lease 后有界清理。
6. 进程在第 4 步前崩溃时，新 generation 不可见；在第 4 步后崩溃时，恢复必须得到完整新 generation。

如果公开 API 无法高效表达 generation 过滤、原子发布、snapshot lease 或派生索引清理，应登记 SonnetDB capability gap；不得用另一个数据库实现提交日志。

## 6. 代码图 schema

首版候选节点：`Workspace`、`Repository`、`Project`、`BuildTarget`、`Module`、`Namespace`、`File`、`Type`、`Member`、`Symbol`、`Test`。

首版候选边：`Contains`、`Defines`、`References`、`Calls`、`Imports`、`Inherits`、`Implements`、`Overrides`、`DependsOn`、`Builds`、`Covers`。

每个节点/边至少包含 stable ID、language、source revision、source span/evidence、adapter version 和 confidence。静态可证明关系使用 `exact`；动态语言启发式关系必须明确标为 `inferred`，并保留推导证据。C0 已冻结 `couplet.code_graph.v1` 和 `couplet.generation.v1`，详见 [Code Graph v1 合同](code-graph-v1-contract.md)。

## 7. 查询路径

```text
typed request
  -> capability/freshness check
  -> SonnetDB single typed plan
       (exact or FullText/Vector candidate access
        + dedup/fusion + Native Graph expansion)
  -> bounded unified candidates + actual plan/counts
  -> Couplet context selection + source evidence hydration
       (不能重新召回、合并第二路结果或逐跳扩图)
  -> token/item/byte budget
  -> typed response + diagnostics + cursor
```

- exact symbol 与声明的原生邻接路径不得全库扫描。
- 阻塞算子必须申明内存上限；所有路径接受 cancellation/deadline。
- `EXPLAIN`/diagnostics 必须报告候选、检查、返回、expanded edges、frontier peak、fallback 和预算消耗。
- 返回预算只限制一次响应，不限制索引库规模；达到预算时返回 `truncated=true` 和 revision-bound `next_cursor`。

## 8. 进程与接入

首版以一个本地 Couplet host 托管 workspace coordinator、嵌入式 SonnetDB 和 MCP Server。C0 已把共享 `Couplet.Core` / `Couplet.Application`、SonnetDB adapter、CLI、daemon 与 MCP Server executable 分开，并实现只读 stdio initialize、schema discovery 和 capability-gated tool call；索引查询仍 unavailable。若后续成熟 parser 不能进入 Native AOT host，可使用受控的本地 parser worker，但它不能拥有数据库或第二份索引，发布矩阵必须如实标注各 executable/worker 的 AOT 状态。Codex 与 Claude Code 使用同一 schema，不维护客户端专属语义。

远程多租户、协作写入、IDE 编辑器插件和自动改码不属于首版。它们只有在本地单机合同、权限和生产门禁通过后才能单独进入路线。

## 9. 性能缺口反馈

每个查询报告实际 access path 和资源计数。若基准显示通用 SonnetDB 路径存在正确性、恢复、全扫/物化、锁、分配、I/O 或容量缺口：

1. 在 [能力缺口目录](capability-gaps.md)记录最小复现、规模、SLO 和阻塞阶段。
2. 将缺口归入 SonnetDB M40/M32/M35/M36/M41 或其他真实责任里程碑。
3. 在 SonnetDB 修复并取得回归和固定硬件证据前，Couplet 能力保持 unavailable/Preview/Beta。
4. 关闭后把 SonnetDB commit、Couplet 回归和容量报告链接回 gap。

产品侧调高内存、超时或结果上限不能作为关闭证据。
