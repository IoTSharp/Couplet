# MCP v1 合同

## 1. 范围

MCP v1 面向本地编码 Agent，首版只读、typed、版本化。它查询 Couplet 已发布的 index revision，不提供任意 SQL、任意图查询、文件写入、shell 执行或数据库维护入口。

本文与 [`contracts/mcp/v1/schema-catalog.json`](../contracts/mcp/v1/schema-catalog.json) 共同冻结语义和 JSON Schema；source-generated C# DTO、stdio `initialize` / `tools/list` / `tools/call` 以及 snapshot/兼容性测试已在 C0 实现。C1-C3 未就绪工具仍返回结构化 `capability_unavailable`。

## 2. 通用请求

MCP Server 启动时必须用 `--workspace <path-or-id>` 或等价显式配置绑定一个 workspace；不得从进程当前目录猜测。MCP initialize/handshake 返回 resolved `workspace_id`、canonical repository identity、source/index revision 和 capability summary。v1 一个连接只绑定一个 workspace，多工作区 daemon 的 list/resolve 合同留待兼容扩展。

每个请求包含以下字段；`workspace_id` 可省略但必须由连接绑定唯一解析，若显式提供则必须与连接一致：

| 字段 | 类型 | 语义 |
|---|---|---|
| `protocol_version` | string | 首版为 `1`；不支持的 major 必须拒绝。 |
| `workspace_id` | string? | 显式选择或校验 initialize 已绑定的工作区。 |
| `revision_selector` | object? | tagged selector：`kind` 为 `source` 或 `index`，`value` 为 revision；缺省解析到当前 active index generation。 |
| `budget.max_items` | int | 最大结构化结果项；正数且受服务端上限保护。 |
| `budget.max_tokens` | int | 最大上下文 token 预算；tokenizer/model identity 必须返回。 |
| `budget.max_bytes` | int | 序列化与 source hydration 上限。 |
| `budget.deadline_ms` | int | 调用 deadline；服务端上限优先。 |
| `cursor` | string? | opaque、带签名/校验、绑定 workspace/query/revision 的续页游标。 |

服务端不对索引库设置固定词项上限。预算只控制单次计算和响应；结果未完时必须返回可解释截断与 `next_cursor`。

## 3. 通用响应

| 字段 | 语义 |
|---|---|
| `schema_version` | 响应 schema 版本。 |
| `workspace_id` / `source_revision` / `index_revision` | 结果绑定的确切快照。 |
| `freshness` | 结构化状态：`source_state`（clean/dirty/unknown）、`index_state`（empty/indexing/current/stale/corrupt）、coverage、pending/failed file counts 和原因。 |
| `capabilities` | 本次用到的 exact/fulltext/vector/graph/hybrid 能力和等级。 |
| `items` | typed 结果；顺序规则由具体工具定义。 |
| `evidence` | 去重后的文件、span、symbol、relation 和 revision 证据。 |
| `diagnostics` | access path、候选/检查/返回、expanded edges、frontier、fallback、耗时和预算消耗。 |
| `truncated` / `truncation_reason` | 是否因 items/tokens/bytes/deadline/frontier 等停止。 |
| `next_cursor` | 仍有稳定续页时返回；revision 改变后不得悄悄续到新快照。 |

每个 item 通过 `evidence_ids` 引用证据。路径和 span 使用 workspace-relative path、UTF-8 line/column 与 byte offset 的明确组合；不能只返回不可验证的自然语言结论。

## 4. 工具

### `workspace_status`

返回 initialize 所绑定的 workspace/source/index revision、文件/符号/关系/chunk 计数、索引队列、parser/embedding/schema version、数据库大小、能力等级、阻塞 gap 和是否需要 rebuild。首次连接不需要预先知道 `workspace_id`；handshake 已提供该值。

不得触发隐式全量重建或 Document 全表扫描。客户端可据此决定等待、提示用户或只使用已验证能力。

当前 source lane 在 MCP 启动时显式绑定 `--workspace` 与 `--database` 后开放该工具：每次调用持有一个 active generation lease，只读取同一 planning/manifest snapshot，并以 source-generated typed DTO 返回。`source_revision` 与 `database_bytes` 是 initialize/startup 时的采样值，不是调用时实时工作树扫描；`freshness.reason` 与 diagnostics 必须显式说明该边界，`rebuild_required` 只相对 initialize snapshot 判断。source lane 的 exact/fulltext capability 已随 `code_search` 首切片升为 Preview；`symbol_get` 仍 unavailable。成功 status 继续报告尚未关闭的 CG-005，不再报告已关闭的 CG-007。

### `code_search`

输入 query、scope（path/language/kind）、mode（`exact`、`fulltext`、`vector`、`hybrid`）和预算。输出稳定排序的 file/symbol/chunk 命中、score decomposition 和 source evidence。

- C1 开放 exact/fulltext；C3 才开放 vector/hybrid。
- 当前 source lane exact 只按 stable ID 命中 `by_stable_id` path index，fulltext 使用 active generation 的 `code_search` FullText index；请求全程持有同一 query lease，并返回实际 access path、候选/检查/返回计数和预算消耗。
- 当前不签发 `next_cursor`；携带 cursor 或 fulltext path/language/kind filter 时稳定返回 capability limitation。默认 package lane 继续返回 `generation_publish_blocked`。
- 请求未就绪 mode 时返回 `capability_unavailable`，不把 fulltext 冒充 vector/hybrid。
- 相同 score 使用 stable ID 作为最终 tie-breaker，保证分页确定性。

### `symbol_get`

按 stable symbol ID 或 unambiguous qualified identity 返回 kind、signature、container、definition、declarations、documentation、language、source span 和 confidence。名称歧义返回候选，不自行猜选。

### `symbol_relations`

按 symbol、relation kinds、direction、depth 和预算读取原生图邻接，返回逐边 evidence、confidence 和目标符号摘要。

该工具从 C2 开始可用。必须由 SonnetDB GraphStore 执行；不可用时返回结构化错误，不在 Couplet 中遍历关系集合。

### `dependency_path`

输入 from/to symbol 或 build target、允许的 relation kinds、方向、max depth/path/frontier。输出零条或多条有序路径，每一步包含节点、边、source evidence 和累计代价。

路径爆炸、frontier 或 deadline 到达时返回 partial/truncated；不能先枚举所有路径再裁剪。

### `impact_analyze`

输入 files/symbols 或 change set、relation allowlist、max depth/frontier、是否包含 tests/build targets。输出按直接/传递、置信度和原因分组的受影响符号、项目、构建目标和候选测试。

“未发现影响”只有在索引 current、相关语言能力 exact 且预算未截断时才能表述为完整结论；其他情况必须返回 coverage caveat。

### `change_context`

输入 Git base/head、working tree 或显式 diff hunks。输出变更文件、映射符号、调用/依赖邻域、公共合同变化、候选测试和未知区域；不修改 Git 或工作区。

### `context_pack`

输入 task、可选入口 symbol/change set、retrieval modes、evidence policy 和 token/item/byte 预算。输出按角色分区的最小上下文：definitions、implementation、constraints、callers/dependencies、tests 和 diagnostics。

每个片段带 path/span/revision/selection reason；相同源码只出现一次。context pack 不输出未经证据支持的总结，不用超出预算的巨大图 dump 替代选择。

## 5. 错误合同

首版稳定错误码：

| 错误码 | 含义 |
|---|---|
| `invalid_request` | schema、范围、预算或参数不合法。 |
| `workspace_not_found` | workspace 未注册或无权访问。 |
| `index_not_ready` | 尚无可查询 generation。 |
| `stale_revision` | 指定 revision/cursor 已不可用或与当前请求不匹配。 |
| `capability_unavailable` | 所需 parser/model/SonnetDB 能力或发布 gate 未通过。 |
| `budget_exhausted` | 在产生任何可靠 item 前预算耗尽；已有结果时使用 truncated response。 |
| `provider_unavailable` | 显式选择的 embedding/provider 不可用。 |
| `cancelled` | 客户端取消。 |
| `deadline_exceeded` | deadline 到达。 |
| `index_corrupt` | 校验失败，需要 repair/rebuild；不得返回可疑结果。 |
| `internal_error` | 未分类错误；返回 correlation ID，不泄露源码或凭证。 |

错误对象包括 `retryable`、`capability`、`gap_id`（如有）、`current_revision` 和安全诊断。不得通过复用错误码改变既有含义。

## 6. 兼容与安全

- v1 只允许新增 optional 字段、枚举的协商式扩展和新工具；删除字段、改变默认值/排序/错误语义需要新 major。
- 未识别请求字段按 schema 策略明确拒绝或忽略，不能因客户端不同产生不同语义。
- cursor 不含源码正文或凭证，并有完整性校验，绑定 resolved `index_revision` 而不是模糊 source revision；日志只记录工具名、版本、计数、耗时和 correlation ID。
- workspace allowlist、path normalization 和 symlink resolution 在查询前执行，证据 hydration 不能越过仓库边界。
- Codex 与 Claude Code 使用完全相同的工具 schema、能力等级和错误合同；客户端适配层只处理安装和传输。
