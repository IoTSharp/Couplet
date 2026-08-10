# AGENTS

本文件定义 AI 协作在 Couplet 仓库中的工程约束。所有代码、测试和文档变更都必须遵守本文件。

## 项目目标

Couplet 是面向 Codex、Claude Code 等编码 Agent 的本地优先代码知识与上下文引擎。它以嵌入式 `SonnetDB.Core` 为唯一数据引擎，提供增量代码索引、原生属性图分析、混合检索、预算化上下文和 typed MCP。

当前真实状态以 [ROADMAP.md](ROADMAP.md) 为准。仓库和路线基线完成不代表任何产品能力已经实现。

## 强制架构边界

1. 依赖方向只能是 `Couplet -> SonnetDB.Core`。不得让 SonnetDB 引用 Couplet，不得把代码领域 schema 或 MCP 产品合同下沉到 SonnetDB Core。
2. SonnetDB 是唯一数据引擎。不得增加 SQLite、DuckDB、Neo4j、独立向量库、内存图真相源或另一套持久索引。
3. 定义、引用、调用、继承、包含、依赖和测试关系必须使用 SonnetDB 原生属性图。禁止关系边表、应用层 BFS/DFS、加载全图后遍历或隐藏全量扫描兜底。
4. 如果 SonnetDB 缺少正确性、恢复、访问路径或性能能力，必须登记 capability gap、回收到对应 SonnetDB 里程碑并阻塞 Couplet 阶段。允许 fail fast 或降低公开能力等级，不允许旁路。
5. 修复优先级为：正确性/原子性/恢复；有界执行与消除非预期全扫/物化；容量/延迟/资源放大；最后才是 API、Agent 集成和 UI。
6. 索引入口不得设置固定文件数、词项数、符号数或边数上限。所有查询必须有显式预算、取消、分页、截断状态和 access-path 诊断。

## 数据与隐私

- 默认本地处理，不向外部服务发送仓库内容。
- 在线 embedding 或模型 provider 必须由用户显式启用，并记录 provider、模型版本、发送范围和 cache identity。
- 严格遵守 ignore/deny 配置；密钥、凭证、构建产物和用户排除内容不得进入索引、日志、trace 或 eval artifact。
- 日志和指标不得记录源码正文、prompt、凭证或完整文件路径；需要定位时使用可控的 workspace-relative path 或散列标识。
- 索引必须携带 workspace revision、index revision、parser/model version、source span 和 provenance；不能把陈旧结果伪装为当前结果。

## .NET 与 API 约束

- 目标框架为 .NET 10，启用 Nullable、ImplicitUsings 和 TreatWarningsAsErrors。所有 Couplet 自有生产代码必须通过 trim analyzer；声明支持 Native AOT 的 executable/worker 必须启用 AOT analyzer 并以 0 个未处置 IL/AOT warning 发布。语言 parser 等依赖的 AOT 边界由 CPL-007 spike 和发布能力矩阵决定，不得把未验证的整进程 AOT 写成已完成能力。
- 第一版禁止 `unsafe`。确有必要时必须先有独立 ADR、基准证据和 reviewer 明确批准。
- 生产 JSON 必须使用 source-generated `System.Text.Json` context / `JsonTypeInfo<T>`，不得使用依赖反射元数据的序列化重载或压制 IL2026/IL3050。
- 所有 public API 必须有中文 XML 文档注释。
- 公共 MCP 合同遵循 extend-only 兼容策略；字段删除、重命名、含义改变或错误码复用均视为 breaking change。
- Couplet 可以为语言解析、Git 或 embedding 引入有边界的依赖，但每个运行时依赖必须说明许可证、Native AOT/trim 状态、更新策略和不可用时的明确行为。不得把依赖引入 `SonnetDB.Core`。

## 索引与查询不变量

- 一个 index revision 的文件、符号、chunk、全文、向量和图派生状态必须原子发布；崩溃后只能看到旧 revision 或完整新 revision。
- stable ID 必须由规范化 workspace identity、语言、symbol identity 和 source identity 推导；文件重命名与符号移动语义由测试冻结。
- 删除、重命名、branch/worktree 切换、parser 升级和 embedding 模型升级必须清除或重建所有相关派生索引。
- uncertain/dynamic 语言关系必须带 confidence 与 evidence，不能标为 exact。
- 图遍历和混合查询必须返回实际访问路径、候选/检查/返回计数、frontier peak、fallback reason 和预算消耗。
- 任何 scan fallback 都必须显式、可取消、有界并通过目标规模 SLO；声明的原生图路径不得 fallback 到 scan。

## 测试与证据

- 单元测试命名采用 `方法名_场景_预期结果`。
- 必测空工作区、单文件、大仓、损坏数据库、旧 schema、取消、预算耗尽、Git rename/delete/branch switch、崩溃恢复和 provider 不可用。
- 语言适配器必须有 fixture + golden symbol/edge/source-span 测试；动态或不支持结构必须有显式能力等级。
- MCP 必须有 schema snapshot、兼容性、分页、截断、错误和双客户端合同测试。
- 正确性/恢复与性能/容量是两个独立 PASS/FAIL gate。任一失败都不得合并阶段完成或发布；SonnetDB `#352/#359/#367` 是与对应 Couplet workload 联合关闭的退出门禁，不得错误写成禁止联调开发的前置条件。
- 性能报告必须记录 commit、语料 manifest、硬件、运行时、P50/P95/P99、吞吐、内存、分配、I/O、候选/检查/返回量、实际 access path 和恢复时间。
- Agent eval 必须固定客户端、模型、版本、提示、工具合同和语料，保留 paired baseline，不以主观演示替代证据。

## 文档与状态

- 行为、公共合同、阶段边界或门禁变更必须同步更新 `README.md`、`ROADMAP.md`、相关 ADR/文档和 `CHANGELOG.md`。
- 只有存在可运行实现和验收证据时才能把产品阶段标为完成。文档完成只能标记为“仓库/路线基线完成”。
- capability gap 必须记录复现语料、规模、预期/实际、责任仓库/里程碑、阻塞阶段和关闭证据。
- 未经维护者明确决定，不新增 `LICENSE` 文件或许可证元数据。

## 变更规范

- 一个 PR 只处理一个路线交付或一个独立缺口。
- Commit 使用 Conventional Commits：`<type>(<scope>): <简述>`。
- PR 必须说明变更点、路线对应项、测试/证据、兼容性、SonnetDB gap（如有）和 CHANGELOG 更新。
- 不提交 `bin/`、`obj/`、索引数据库、语料副本、benchmark artifact、密钥或本机配置。
