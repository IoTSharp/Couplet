# C1 增量索引实现与证据

## 当前结论

C1 处于实现中，不是完成状态。CPL-010~012 已有可运行实现；CPL-013~015 在默认 `SonnetDB.Core 3.1.0` package lane 保留安全 staging，在显式 source lane 已接通最新 SonnetDB `Tsdb.Generations` 的 planning KV + Document + FullText 原子发布、active lease/cursor、重开、no-op、writer fence、lease/cutoff-aware cleanup、确定性提交边界故障重开和 `workspace_status` per-request active lease 小型回归。`CG-007` 的选择性 cleanup API/接线缺口已关闭，`CG-005` 仍为 verifying；`code_search`/`symbol_get`、实时 watcher freshness、查询 cursor、真实子进程 kill-before/after-publish 与容量门禁仍未完成。固定 package 的 Native AOT 继续受 `CG-006` 阻塞；source lane 使用已修复的 worker 生命周期，2026-08-29 win-x64 CLI publish/no-op smoke 已通过，但本轮 CG-007 变更尚未重新执行 Native AOT publish，7 天长稳仍待归档。

2026-08-25 的真实 Medium/Large characterization 已完成，但 Correctness/Recovery 与 Performance/Capacity 两个独立门禁均为 **FAIL**。这些数据是缺口和优化输入，不是 C1 容量声明或完成证据。

冻结的机器可读 v1 合同位于 [`contracts/indexing/v1/schema.json`](../contracts/indexing/v1/schema.json)，覆盖 workspace discovery、UTF-8 source span、parsed/planning snapshot、incremental plan、generation manifest 和默认 package stage report。该 v1 stage object 使用 `additionalProperties=false`，因此 source lane 为新增 retention-deferred 字段明确升级到 [`couplet.index_stage.v2`](../contracts/indexing/v2/stage-report.schema.json)，而不是把字段塞进 v1。两条 lane 的生产 JSON 均通过 source-generated `CoupletJsonContext`；没有反射 serializer fallback。

## 已实现路径

| 交付 | 当前证据 | 公开能力 |
|---|---|---|
| CPL-010 | canonical path；Git HEAD/branch/remote/worktree；tracked/untracked/ignored；deny > ignore；symlink/binary/generated/large/unreadable 分类；bounded watcher overflow 转 full rescan | `workspace-scan` 可用 |
| CPL-011 | replaceable `ILanguageAdapter`；C#、TypeScript/JavaScript lexical adapter `1.1.0` 固定为 `Partial` 与 `Inferred/0.9` 声明 confidence；unsupported/large file 为 `TextOnly`；版本化 fixture 覆盖同名、重载和 generic method 不支持边界 | adapter 能力可诊断，不宣称完整语义 |
| CPL-012 | deterministic file/symbol/chunk IDs；qualified identity/signature；UTF-8 byte/line/column span；content hash、provenance、adapter version、confidence；三语言完整 golden snapshot | staging records 可追溯 |
| CPL-013 | frozen snapshot；added/modified/deleted/content-hash rename；producer/branch rebuild；source runtime 从 active generation 读取轻量 planning snapshot 和真实 previous revision；无变化 no-op | 默认 package 仅 staging；source publish 可用；实时 watcher freshness 未接线 |
| CPL-014 | generation collection；stable ID/path indexes；FullText；512-record batch；source lane 原子发布 planning KV + Document + FullText；`workspace_status` per-request active lease | source `index-stage` 和显式数据库 `workspace_status` 可用；exact/fulltext 查询仍不可用 |
| CPL-015 | parse/cancellation/marker/checkpoint/reopen；source publish/acquire、cursor revision、writer fence、lease/cutoff-aware cleanup；提交前/后故障与重开原子性；status 重开/selector fail closed | CG-007 API/接线已关闭；真实子进程 kill、查询 cursor continuity 未完成 |

## SonnetDB 接线

每个 staging generation 写入独立 Document collection。文件、符号和 chunk 使用同一 source-generated JSON record contract；`$.stable_id` 为唯一 path index，`$.record_type`、`$.path`、`$.qualified_identity` 为附加 path indexes，`$.search_text` 写入 `code_search` FullText index。

写入前先删除并持久化旧 completion marker，再替换 generation collection；Document checkpoint 完成后才写入 `GenerationState.Staging` manifest。`InspectStaging` 同时校验 manifest identity/state、Document count、FullText document count、四个 path/FullText index 和 Document index consistency，missing/corrupt marker 均被拒绝。内部探针冻结实际访问路径 `document_path_index:by_stable_id` 和 `document_fulltext:code_search`。

默认 package lane 不建立 active pointer，不发 query lease，也不删除旧 generation；CLI 固定返回 `published=false` / `CG-005` 和 `couplet.index_stage.v1`。source lane 使用独立 `cplk_*` planning keyspace 保存不含源码正文的文件级 hash/adapter/branch 元数据，与 `cplg_*` Document collection 和 `code_search` FullText index 一同交给 `Tsdb.Generations.Publish`。writer fence 在锁内重新 discovery 并覆盖 active read、build、plan 和 publish，workspace identity 漂移时拒绝继续。下一次运行通过 active lease 读取 planning snapshot；全部文件 unchanged 时复用 active，发生变化时发布连续 database revision。source CLI 的 `--retired-generation-retention <c>` 使用 invariant `TimeSpan`；零值沿用立即 cleanup，非零值以 `TimeProvider.GetUtcNow()` 计算带下溢保护的 inclusive UTC cutoff，再调用 Core options overload。Core 在 generation 临界区内重新检查 active、cutoff 和 lease；Couplet 只为 `RemovedRevisions` 删除 staging marker，并在 `couplet.index_stage.v2` 中分别返回 `deferred_generation_revisions` 与 `retention_deferred_generation_revisions`。发布提交后的 cleanup 失败仍返回 `retired_generation_cleanup_failed` 与 retry limitation，不把已提交 generation 误报为发布失败。确定性 publish 故障回归仍不等同于真实子进程 kill。

source lane MCP 在显式传入 `--workspace` 与 `--database` 后使用与 `index-stage` 相同的 workspace discovery identity，并由 `CoupletRuntime` 在完整 stdio host 生命周期内拥有 `SonnetDbIndexGenerationStore`。`workspace_status` 的每次调用只获取一个 active generation lease，从该 lease 的 planning KV、Document/FullText resource roles 读取同一 published planning/manifest snapshot；它校验 deterministic resource name、parent binding、Document schema、manifest identity/state/schema、checksum 形状和 planning identity，然后在响应完成后释放 lease。状态路径不打开 Document row scan、不重算 checksum、不执行 consistency repair 或 rebuild；source-generated `McpToolResponse<WorkspaceStatusItem>` 是唯一成功 JSON 路径。空 stream 返回 `index_not_ready`，旧 revision selector 返回当前 active 的 `stale_revision`，metadata/schema 异常统一返回不含绝对路径的 `index_corrupt`。

MCP initialize 时发现的 source revision 与 database bytes 各采样一次；`workspace_status.freshness.reason=source_revision_sampled_at_mcp_startup`，diagnostics 的 fallback reason 同时标明 source/database snapshot 边界，`RebuildRequired` 只表示 active manifest 是否匹配 initialize 时的 source revision，不宣称调用时工作树实时 current。status 仍报告 CG-005，不再报告已关闭的 CG-007；exact/fulltext capability 保持 `unavailable/active_query_tool_not_connected`，`code_search`、`symbol_get` 与 C2/C3 工具继续走原 capability gate。默认 package lane 和 source lane 缺少 `--database` 时的既有 unavailable 行为不变。固定 package Native AOT 继续禁用不兼容 worker并报告 CG-006；source lane 保留最新 SonnetDB 默认 worker，使用 Core 已验证的 AOT-safe shutdown。

## 验证命令

```powershell
dotnet restore Couplet.slnx --locked-mode
dotnet build Couplet.slnx --configuration Release --no-restore
dotnet test tests/Couplet.Tests/Couplet.Tests.csproj --configuration Release --no-build --no-restore
dotnet restore Couplet.slnx -p:UseSonnetDbSource=true
dotnet build tests/Couplet.Tests/Couplet.Tests.csproj --configuration Release --no-restore -p:UseSonnetDbSource=true
dotnet test tests/Couplet.Tests/Couplet.Tests.csproj --configuration Release --no-build --no-restore -p:UseSonnetDbSource=true
./eng/verify-roadmap.ps1
dotnet run --project src/Couplet.Cli --configuration Release --no-build -- workspace-scan --workspace .
dotnet run --project src/Couplet.Cli --configuration Release --no-build -- index-stage --workspace . --database artifacts/c1-smoke-db
dotnet publish src/Couplet.Cli/Couplet.Cli.csproj -c Release -r win-x64 -p:CoupletPublishAot=true
dotnet publish src/Couplet.Daemon/Couplet.Daemon.csproj -c Release -r win-x64 -p:CoupletPublishAot=true
dotnet publish src/Couplet.McpServer/Couplet.McpServer.csproj -c Release -r win-x64 -p:CoupletPublishAot=true
```

自动化覆盖 clean/dirty Git revision、ignore/deny、path/symlink escape、Git 子进程取消、文件突变、三语言 stable symbol/chunk 与 UTF-8 span、rename/modify/delete、producer upgrade、watch、branch switch，以及 SonnetDB staging/retry/marker/reopen/access path。source lane 另覆盖 CLI 首次 publish、跨重开 active planning snapshot、unchanged no-op、连续 revision、可取消 writer fence、预取消 no-op 不清理、publish 后 cleanup fault 隔离、提交前/后确定性进程内故障重开、旧 lease 延迟 cleanup、租约释放后资源删除、旧 cursor stale，以及 zero/nonzero/UTC offset inclusive/mixed-age reopen/lease/cancellation/failure/最大 retention/连续六次 publish 的 cleanup 矩阵。`workspace_status` 回归覆盖空库、typed host success、调用前后 SonnetDB Document full-scan counter 不变、active revision 切换、旧 selector fail closed、重开、损坏 manifest、不泄露数据库路径、serialization-inclusive deadline 以及 stdio host 退出后数据库重开。producer `1.0.0 -> 1.1.0` 当前仍只有 planner contract 回归，尚未执行真实旧 producer 数据库升级。Codex/Claude Code 对尚未接线查询工具的 unavailable 回归保持不变。

2026-08-25 在 `329519d` C0 基线、`d0531e0` C1 staging 提交上的首次仓库 smoke：对 Couplet 自身执行 `workspace-scan` 得到 115 个候选、113 个 included files，随后 `index-stage` 成功写入 113 files、508 logical symbols、584 chunks 和 1205 FullText documents，`problems=0`。本次收口后 Release build 为 0 warning/0 error，56/56 tests PASS；CLI、daemon 和 MCP Server 的 win-x64 Native AOT publish 均为 0 个未处置 IL/AOT warning，三个发布后的 executable 均能启动并报告固定 `SonnetDB.Core 3.1.0`。当前 working tree 的 AOT CLI staging 写入 117 files、540 logical symbols、620 chunks 和 1277 FullText documents，`problems=0`，同时按合同保持 `published=false`、`blocking_gap=CG-005` 并显式报告 CG-006 后台维护 limitation。烟测数据库在验证后删除，未作为仓库 artifact 保留。

2026-08-28 增加 C1 三语言 fixture/golden、lexical confidence/version 修正和 producer-version planner contract 回归后，Release build 继续为 0 warning/0 error，59/59 tests PASS，路线校验通过；这只补齐 CPL-011/CPL-012 correctness evidence 与 CPL-013 planner contract，不代表 runtime 增量接线完成，也不改变 CG-005/CG-006 或 C1 gate 状态。

2026-08-29 增加 source ProjectReference lane、generation 产品回归、writer fence、cleanup fault 隔离及 source/default 构建隔离。当前工作树已完成 source/default/source 往返：source Release build 0 warning/0 error、64/64 tests PASS，默认 package Release build 0 warning/0 error、59/59 tests PASS，默认 locked restore、source lock 隔离与路线校验均通过。win-x64 Native AOT CLI 发布为 0 个未处置 IL/AOT warning；对三语言 fixture 的首次运行退出 0、发布 revision 1，unchanged 重跑退出 0、复用同一 revision，两次均 `problems=[]` 且只报告 CPL-015 retention policy limitation。本阶段只证明小型 runtime publish/lease/cursor/cleanup 与 AOT smoke；默认 package、MCP、retention policy、真实子进程 fault/capacity、7 天长稳与双客户端门禁状态不变。

2026-08-30 增加 `Tsdb.Generations.Publish` 正前方/返回后的 internal、默认无行为故障点和两条 reopen 回归。提交前注入验证重开仍绑定 revision 1、未发布 staging 完整且原目标可重试为 revision 2；提交后注入验证重开绑定 revision 2 的 planning KV、Document 与 FullText，exact 新符号可见、旧符号不可见、FullText 命中新 revision，随后重试复用 revision 2。source generation 定向测试 7/7 PASS，source lane 全量 66/66 PASS，默认 package lane 59/59 PASS，路线校验和 `git diff --check` 通过。该结果是确定性进程内异常边界证据，不替代真实子进程 kill-before/after-publish。

2026-08-30 `workspace_status` active-lease 切片当时增加 8 条回归。状态调用不扫描 Document、不重算 checksum；source/database 值明确为 MCP 启动快照。空库、typed JSON、active 切换、旧 selector、重开、真实 branch switch stale、请求内 publish/cleanup lease、损坏 metadata、deadline 和完整 stdio store 生命周期均已覆盖；exact/fulltext 及其余查询工具仍 unavailable。该切片当时 CG-005/CG-007 均未关闭，验证结果为 source lane 全量 74/74 PASS、默认 package lane 59/59 PASS；这是历史阶段结果，不是当前工作树计数。

2026-08-30 增加 CG-007 cutoff cleanup 接线与 `couplet.index_stage.v2`。最终复核结果为 source Release build 0 warning/0 error、source lane 全量 89/89 PASS、默认 package lane 全量 62/62 PASS；source retention/wire 定向测试 12/12 PASS，default v1 wire 测试 1/1 PASS。v1 schema/payload 保持冻结。该结果关闭 API/接线缺口，但尚未重跑固定硬件 Medium/Large 或 7 天增长，不改变 C1 双门禁 FAIL。

Medium/Large 容量运行使用固定 manifest `38f906b9b65f88e11bb2953fa2ee45e97105815c13d6b8a364230da1ee9fb1b4`。Medium（1m LOC / 100k symbols）initial 74.814 s，100-file 73.086 s，peak RSS 4.648 GiB；Large（10m LOC / 1m symbols）initial 3,261.419 s，100-file 6,472.504 s，peak RSS 28.329 GiB。Large initial、两档增量和两档内存均未达目标；详细语料 hash、P50/P95/P99、allocation、reopen 与数据库放大见 [C1 Medium/Large 容量证据](c1-capacity-evidence.md)。

## 未通过门禁

- Correctness/Recovery：**FAIL**。source runtime 已从 active planning snapshot 构造 plan，并实现 publish/no-op/reopen、writer fence、lease/cursor/cutoff cleanup、确定性提交边界故障重开和 `workspace_status` active-lease 回归；仍缺真实子进程 kill-before/after-publish、`code_search`/`symbol_get` cursor continuity、实时 watcher freshness 和 branch-switch 查询语义。
- Performance/Capacity：**FAIL**。Medium/Large 已有固定语料实测；Large initial、两档 100-file 路径和两档 peak RSS 未达门禁，initial/incremental/reopen 昂贵路径只有单样本，I/O 与 FullText candidates/examined 诊断也不完整。
- Native AOT lifecycle：默认 3.1.0 package 仍关闭不兼容 worker并报告 CG-006；2026-08-29 source win-x64 CLI publish/no-op smoke 已通过，本轮 CG-007 变更尚未重新执行 Native AOT publish，7 天长稳仍未归档。
- 双客户端合同：Codex/Claude Code 的能力门控已验证；source lane `workspace_status` 已有 stdio typed 成功回归，但尚无真实双客户端发布后会话证据，`code_search`、`symbol_get` 仍 unavailable。

关闭 C1 还需完成 Couplet 真实进程 crash/recovery、并发协调、MCP cursor/lease、删除/重命名/branch switch、固定硬件容量和 7 天增长联合回归。不得以应用层第二提交日志、直接查询 package staging、吞 dispose 异常或隐藏 maintenance 状态绕过。
