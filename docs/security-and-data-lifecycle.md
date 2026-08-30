# 安全、隐私与数据生命周期合同

## 默认边界

安全合同版本为 `couplet.security.v1`，机器可读 schema 位于 [`contracts/security/v1/policy.schema.json`](../contracts/security/v1/policy.schema.json)。默认模式为 `LocalOnly`：仓库正文、prompt、凭证和完整绝对路径不发送到外部服务，也不进入日志、trace 或 eval artifact。

- MCP Server 必须用 `--workspace` 显式绑定存在的目录，不从当前目录猜测工作区。
- workspace allowlist 在读取文件前校验；所有公开路径为 workspace-relative path 或 stable ID。
- deny 规则优先于 include/ignore；symlink 解析后的目标仍必须位于允许的 workspace 边界内。
- 密钥、`.env`、凭证、构建产物、索引数据库和用户排除内容不得进入任何派生模型。
- 日志默认只记录工具、版本、correlation ID、有界计数、耗时和原因码。

## Provider

在线 provider 只能使用 `ExplicitOnline`，并同时满足：`user_opt_in=true`、固定 provider/model/version，以及非空发送字段 allowlist。缺少任一项即拒绝策略；请求显式选择未配置 provider 时返回 `provider_unavailable`，不会回退到另一个 provider。

provider cache identity 必须包含 provider、model、version、content hash 和允许字段集合。deny/ignore 内容不能因为已有 cache 而重新可见。

## 生命周期

策略分别记录 retired generation、日志和 provider cache 的 retention duration。`RetiredGenerationRetention` 表示 retired generation 从 durable publish time 起达到该时长后具备清理资格，不承诺“最长保留时间”；active generation 或仍有 query lease 的 retired generation 会继续保留。workspace 移除时是否删除本地索引必须显式配置。

C0 自动化覆盖本地默认策略、在线 provider 未授权、路径绑定和稳定错误。C1/C3 在真实文件发现、symlink、ignore/deny 和 provider 接线后继续执行内容不泄漏与断网测试。

## C1 已实现边界

- 工作区必须由 CLI `--workspace` 显式给出并 canonicalize；公开 discovery JSON 不包含本机绝对路径，Git remote identity 会移除用户名、密码、query 和 fragment。
- deny 先于 ignore，默认拒绝凭证、`.git`、Couplet 数据库和常见构建产物；Git ignore、binary、generated、symlink escape、unreadable 和大文件 text-only 均以稳定 disposition/reason 报告。
- 被纳入的文件先冻结 content hash，再由 adapter 读取；发现后文件发生变化时以 `file_changed_after_discovery` 失败，不把跨 revision 内容写进同一 snapshot。
- staging collection 以 workspace/index revision 派生的非路径名称隔离，manifest 只在 Document/FullText 计数与索引一致性通过后写入控制 keyspace。
- 默认 3.1.0 package lane 没有 active generation 或 query lease，MCP 不读取 staging。source lane 由 `Tsdb.Generations` 原子发布 generation 独占 planning KV、Document 和 FullText 资源，并用 query lease 阻止 retired cleanup；`workspace_status` 与 `code_search` exact/fulltext Preview 均使用 per-request active lease，查询只访问该 lease 绑定的 path/FullText index。`symbol_get` 和查询 cursor 仍 unavailable。
- source `index-stage` 通过 `--retired-generation-retention <c>` 读取 invariant `TimeSpan` 并传入 lifecycle store；默认零值保持立即 cleanup。非零值转换为 inclusive UTC publish-time cutoff，未到期 revision 与仍有 lease 的到期 revision 分别报告，cleanup 失败可重试。该接线关闭 CG-007 的 API/集成缺口；固定 package lane 不具备 generation cleanup，保持 3.1.0 staging 行为。
- 默认 package Native AOT 因 CG-006 关闭 background workers 并暴露 limitation；最新 source lane 使用已修复的默认 worker。普通 source/JIT handshake 不声明 AOT 已验证；2026-08-29 source Native AOT publish/no-op smoke 已通过，本轮 CG-007 变更尚未重新执行 Native AOT publish，且 7 天长稳未归档前仍不能外推为 Production 维护证据。
