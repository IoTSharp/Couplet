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

策略分别记录 retired generation、日志和 provider cache 的保留时间。workspace 移除时是否删除本地索引必须显式配置；清理操作不能删除 active generation 或仍有 query lease 的 retired generation。

C0 自动化覆盖本地默认策略、在线 provider 未授权、路径绑定和稳定错误。C1/C3 在真实文件发现、symlink、ignore/deny 和 provider 接线后继续执行内容不泄漏与断网测试。

## C1 已实现边界

- 工作区必须由 CLI `--workspace` 显式给出并 canonicalize；公开 discovery JSON 不包含本机绝对路径，Git remote identity 会移除用户名、密码、query 和 fragment。
- deny 先于 ignore，默认拒绝凭证、`.git`、Couplet 数据库和常见构建产物；Git ignore、binary、generated、symlink escape、unreadable 和大文件 text-only 均以稳定 disposition/reason 报告。
- 被纳入的文件先冻结 content hash，再由 adapter 读取；发现后文件发生变化时以 `file_changed_after_discovery` 失败，不把跨 revision 内容写进同一 snapshot。
- staging collection 以 workspace/index revision 派生的非路径名称隔离，manifest 只在 Document/FullText 计数与索引一致性通过后写入控制 keyspace。
- `CG-005` 关闭前没有 active generation 或 query lease；MCP 不读取 staging，retired generation 也不自动清理，避免把不完整生命周期伪装成安全发布。
- Native AOT staging 因 CG-006 关闭固定包的 background flush/compaction/retention/KV maintenance workers，且必须在报告中暴露 limitation；这只适用于无 TTL、无时序写入的短程 staging 取证，不满足长期保留、磁盘增长或 Production 清理门禁。
