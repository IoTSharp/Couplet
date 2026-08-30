# C1 增量索引实现与证据

## 当前结论

C1 处于实现中，不是完成状态。CPL-010~012 已有可运行实现；CPL-013~015 在默认 `SonnetDB.Core 3.1.0` package lane 保留安全 staging，在显式 source lane 已接通 filter-aware revision provenance、daemon watcher、planning KV + Document + FullText 原子发布、active/exact-revision lease、持久 cursor registry、no-op、writer fence、lease/cutoff-aware cleanup、真实子进程 commit 前后 kill/reopen、`workspace_status`、`code_search` exact/fulltext Preview、path/language/entity-kind 过滤与 `symbol_get`。fulltext cursor 可在 orderly store reopen 后继续同一 retired revision。`CG-007` 已关闭，`CG-005` 仍为 verifying；真实进程重启/跨进程 cursor、hard-kill CAS、双客户端、跨平台、随机故障与固定硬件容量门禁仍未完成。固定 package Native AOT 继续受 `CG-006` 阻塞；source CLI、Daemon 与 MCP Server 的 win-x64 Native AOT publish/原生 `version` smoke 均通过且为 0 个未处置 IL/AOT warning，但生产 journey 与 7 天长稳仍待归档。

2026-08-25 的真实 Medium/Large characterization 已完成，但 Correctness/Recovery 与 Performance/Capacity 两个独立门禁均为 **FAIL**。这些数据是缺口和优化输入，不是 C1 容量声明或完成证据。

冻结的机器可读 v1 合同位于 [`contracts/indexing/v1/schema.json`](../contracts/indexing/v1/schema.json)，覆盖 workspace discovery、UTF-8 source span、parsed/planning snapshot、incremental plan、generation manifest 和默认 package stage report。该 v1 stage object 使用 `additionalProperties=false`，因此 source lane 为新增 retention-deferred 字段明确升级到 [`couplet.index_stage.v2`](../contracts/indexing/v2/stage-report.schema.json)，而不是把字段塞进 v1。两条 lane 的生产 JSON 均通过 source-generated `CoupletJsonContext`；没有反射 serializer fallback。

## 已实现路径

| 交付 | 当前证据 | 公开能力 |
|---|---|---|
| CPL-010 | canonical path；Git HEAD/branch/remote/worktree；filter-aware HEAD 对拍；逐组件 symlink containment；binary/generated/large/unreadable 分类；daemon watcher overflow 转 full rescan并默认 30 秒 reconciliation | `workspace-scan` 与 source daemon watcher 可用；一般 filesystem TOCTOU/跨平台长稳未证明 |
| CPL-011 | replaceable `ILanguageAdapter`；C#、TypeScript/JavaScript lexical adapter `1.1.0` 固定为 `Partial` 与 `Inferred/0.9` 声明 confidence；unsupported/large file 为 `TextOnly`；版本化 fixture 覆盖同名、重载和 generic method 不支持边界 | adapter 能力可诊断，不宣称完整语义 |
| CPL-012 | deterministic file/symbol/chunk IDs；qualified identity/signature；UTF-8 byte/line/column span；content hash、provenance、adapter version、confidence；三语言完整 golden snapshot | staging records 可追溯 |
| CPL-013 | frozen snapshot；included-input digest 绑定 index revision；added/modified/deleted/content-hash rename；producer/branch rebuild；source daemon 事件 + 周期 reconciliation；三次 fresh snapshot retry；无变化 no-op | 默认 package 仅 staging；source publish/watcher 可用；固定硬件、跨平台与长稳未验证 |
| CPL-014 | generation collection；stable ID/path/language/entity-kind indexes；FullText；source 原子发布 planning KV + Document + FullText；status/search/symbol query lease；持久 HMAC/CAS cursor registry + exact-revision lease | source 查询与 orderly store reopen cursor 可用；真实进程重启/跨进程 cursor 仍不可用 |
| CPL-015 | parse/cancellation/failure-before-stage/marker/checkpoint/reopen；writer fence、lease/cutoff cleanup；真实子进程提交前/后 kill/reopen；cursor transition/release/timer fail-closed | CG-007 已关闭；hard-kill CAS、双客户端与容量发布证据未完成 |

## SonnetDB 接线

每个 staging generation 写入独立 Document collection。文件、符号和 chunk 使用同一 source-generated JSON record contract；`$.stable_id` 为唯一 path index，`$.record_type`、`$.path`、`$.language`、稀疏 `$.entity_kind` 与 `$.qualified_identity` 为附加 path indexes，`$.search_text` 写入 `code_search` FullText index。active/no-op 路径会校验完整查询 schema；旧 generation 缺少新增索引时不会被继续复用，而是发布新的 ordinal generation。

写入前先删除并持久化旧 completion marker，再替换 generation collection；Document checkpoint 完成后才写入 `GenerationState.Staging` manifest。`InspectStaging` 同时校验 manifest identity/state、Document count、FullText document count、四个 path/FullText index 和 Document index consistency，missing/corrupt marker 均被拒绝。内部探针冻结实际访问路径 `document_path_index:by_stable_id` 和 `document_fulltext:code_search`。

默认 package lane 不建立 active pointer，不发 query lease，也不删除旧 generation；CLI 固定返回 `published=false` / `CG-005` 和 `couplet.index_stage.v1`。source lane 使用独立 `cplk_*` planning keyspace 保存不含源码正文的文件级 hash/adapter/branch 元数据，与 `cplg_*` Document collection 和 `code_search` FullText index 一同交给 `Tsdb.Generations.Publish`。writer fence 在锁内重新 discovery 并覆盖 active read、build、plan 和 publish，workspace identity 漂移时拒绝继续。下一次运行通过 active lease 读取 planning snapshot；全部文件 unchanged 时复用 active，发生变化时发布连续 database revision。source CLI 的 `--retired-generation-retention <c>` 使用 invariant `TimeSpan`；零值沿用立即 cleanup，非零值以 `TimeProvider.GetUtcNow()` 计算带下溢保护的 inclusive UTC cutoff，再调用 Core options overload。Core 在 generation 临界区内重新检查 active、cutoff 和 lease；Couplet 只为 `RemovedRevisions` 删除 staging marker，并在 `couplet.index_stage.v2` 中分别返回 `deferred_generation_revisions` 与 `retention_deferred_generation_revisions`。发布提交后的 cleanup 失败仍返回 `retired_generation_cleanup_failed` 与 retry limitation，不把已提交 generation 误报为发布失败。真实 crash-process 回归启动 `Couplet.Cli` 子进程，在 commit 前/后握手后调用 `Process.Kill(entireProcessTree: true)`；新 store 只见完整 revision 1 或 2，`InspectStaging` 复核实际 Document/FullText 总量与索引一致性，后续真实 CLI 分别发布或复用 revision 2 并清理 revision 1。该确定性本机回归不等同于固定硬件、随机故障或长稳证据。

source lane MCP 在显式传入 `--workspace` 与 `--database` 后使用与 `index-stage` 相同的 workspace discovery identity，并由 `CoupletRuntime` 在完整 stdio host 生命周期内拥有 `SonnetDbIndexGenerationStore`。`workspace_status` 的每次调用只获取一个 active generation lease，从该 lease 的 planning KV、Document/FullText resource roles 读取同一 published planning/manifest snapshot；它校验 deterministic resource name、parent binding、Document schema、manifest identity/state/schema、checksum 形状和 planning identity，然后在响应完成后释放 lease。状态路径不打开 Document row scan、不重算 checksum、不执行 consistency repair 或 rebuild；source-generated `McpToolResponse<WorkspaceStatusItem>` 是唯一成功 JSON 路径。空 stream 返回 `index_not_ready`，旧 revision selector 返回当前 active 的 `stale_revision`，metadata/schema 异常统一返回不含绝对路径的 `index_corrupt`。

`code_search` 每次调用持有单一 active、retained 或 exact-revision generation lease，并从该 lease 冻结的 collection/index 名称执行查询。exact 只按 stable ID 命中 `by_stable_id`；带过滤 fulltext 组合有界 planning snapshot、`by_path` / `by_language` / `by_entity_kind` 和 SonnetDB `SearchFullTextFiltered`，共享访问预算且不返回部分预算结果。opaque cursor 绑定完整 query shape、generation/revision、little-endian offset、nonce 与绝对到期时间。持久 HMAC key 和 generation-independent `Available -> Claimed` CAS record 使 orderly store dispose/reopen 后可重新获取同一 retired revision；续页仍是一次性所有权转移，不延长默认两分钟 TTL，最多 128 slot。未知 CAS 结果、坏记录、无效 generation metadata、timer、最终 CAS/delete/snapshot release 故障会 fault registry 并返回 `query_cursor_registry_unavailable`；已有 query error/cancellation 保持主错误优先，但 lease/slot 仍释放。该路径没有真实进程重启、跨进程竞争或 hard-kill CAS 证据。当前 FullText 没有原生 search-after，续页仍为预算约束的 Top-K + offset；深页不降级为 Document scan。`symbol_get` cursor 与默认 package 查询继续 unavailable。

MCP initialize 时发现的 source revision 与 database bytes 各采样一次；`workspace_status.freshness.reason=source_revision_sampled_at_mcp_startup`，diagnostics 的 fallback reason 同时标明 source/database snapshot 边界，`RebuildRequired` 只表示 active manifest 是否匹配 initialize 时的 source revision，不宣称调用时工作树实时 current。status 仍报告 CG-005，不再报告已关闭的 CG-007；source exact/fulltext capability 为 `preview/active_generation_query_connected`，并覆盖 `symbol_get`；C2/C3 工具继续走原 capability gate。默认 package lane 和 source lane 缺少 `--database` 时的既有 unavailable 行为不变。固定 package Native AOT 继续禁用不兼容 worker并报告 CG-006；source lane 保留最新 SonnetDB 默认 worker，使用 Core 已验证的 AOT-safe shutdown。

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
dotnet publish src/Couplet.Cli/Couplet.Cli.csproj -c Release -r win-x64 -p:CoupletPublishAot=true -p:UseSonnetDbSource=true
dotnet publish src/Couplet.Daemon/Couplet.Daemon.csproj -c Release -r win-x64 -p:CoupletPublishAot=true -p:UseSonnetDbSource=true
dotnet publish src/Couplet.McpServer/Couplet.McpServer.csproj -c Release -r win-x64 -p:CoupletPublishAot=true -p:UseSonnetDbSource=true
```

自动化覆盖 clean/dirty Git revision、filter-aware HEAD、CR/LF/leading-quote path、LFS-like smudge、UTF-8 probe boundary、父目录/目录 symlink、unreadable input、Git 子进程取消/故障回收、三语言 provenance、rename/modify/delete、producer upgrade 和 branch switch。watcher 覆盖 rename/delete、overflow full rescan、linked worktree HEAD、assume-unchanged、周期 no-op、三次 snapshot retry、物理数据库 containment、writer fence 与提交后输出失败。source generation/MCP 另覆盖真实 CLI commit 前后 kill/reopen、cutoff cleanup、durable cursor orderly reopen/绝对 TTL/128 slot、claim/retain/release/timer fault、坏 registry metadata、query/cancellation 主错误优先，以及不扫描 Document 的 status/search/symbol 路径。producer `1.0.0 -> 1.1.0` 当前仍只有 planner contract 回归，尚未执行真实旧 producer 数据库升级。

2026-08-25 在 `329519d` C0 基线、`d0531e0` C1 staging 提交上的首次仓库 smoke：对 Couplet 自身执行 `workspace-scan` 得到 115 个候选、113 个 included files，随后 `index-stage` 成功写入 113 files、508 logical symbols、584 chunks 和 1205 FullText documents，`problems=0`。本次收口后 Release build 为 0 warning/0 error，56/56 tests PASS；CLI、daemon 和 MCP Server 的 win-x64 Native AOT publish 均为 0 个未处置 IL/AOT warning，三个发布后的 executable 均能启动并报告固定 `SonnetDB.Core 3.1.0`。当前 working tree 的 AOT CLI staging 写入 117 files、540 logical symbols、620 chunks 和 1277 FullText documents，`problems=0`，同时按合同保持 `published=false`、`blocking_gap=CG-005` 并显式报告 CG-006 后台维护 limitation。烟测数据库在验证后删除，未作为仓库 artifact 保留。

2026-08-28 增加 C1 三语言 fixture/golden、lexical confidence/version 修正和 producer-version planner contract 回归后，Release build 继续为 0 warning/0 error，59/59 tests PASS，路线校验通过；这只补齐 CPL-011/CPL-012 correctness evidence 与 CPL-013 planner contract，不代表 runtime 增量接线完成，也不改变 CG-005/CG-006 或 C1 gate 状态。

2026-08-29 增加 source ProjectReference lane、generation 产品回归、writer fence、cleanup fault 隔离及 source/default 构建隔离。当前工作树已完成 source/default/source 往返：source Release build 0 warning/0 error、64/64 tests PASS，默认 package Release build 0 warning/0 error、59/59 tests PASS，默认 locked restore、source lock 隔离与路线校验均通过。win-x64 Native AOT CLI 发布为 0 个未处置 IL/AOT warning；对三语言 fixture 的首次运行退出 0、发布 revision 1，unchanged 重跑退出 0、复用同一 revision，两次均 `problems=[]` 且只报告 CPL-015 retention policy limitation。本阶段只证明小型 runtime publish/lease/cursor/cleanup 与 AOT smoke；默认 package、MCP、retention policy、真实子进程 fault/capacity、7 天长稳与双客户端门禁状态不变。

2026-08-30 增加 `Tsdb.Generations.Publish` 正前方/返回后的 internal、默认无行为故障点和两条 reopen 回归。提交前注入验证重开仍绑定 revision 1、未发布 staging 完整且原目标可重试为 revision 2；提交后注入验证重开绑定 revision 2 的 planning KV、Document 与 FullText，exact 新符号可见、旧符号不可见、FullText 命中新 revision，随后重试复用 revision 2。source generation 定向测试 7/7 PASS，source lane 全量 66/66 PASS，默认 package lane 59/59 PASS，路线校验和 `git diff --check` 通过。该结果是确定性进程内异常边界证据，不替代真实子进程 kill-before/after-publish。

2026-08-30 `workspace_status` active-lease 切片当时增加 8 条回归。状态调用不扫描 Document、不重算 checksum；source/database 值明确为 MCP 启动快照。空库、typed JSON、active 切换、旧 selector、重开、真实 branch switch stale、请求内 publish/cleanup lease、损坏 metadata、deadline 和完整 stdio store 生命周期均已覆盖；exact/fulltext 及其余查询工具仍 unavailable。该切片当时 CG-005/CG-007 均未关闭，验证结果为 source lane 全量 74/74 PASS、默认 package lane 59/59 PASS；这是历史阶段结果，不是当前工作树计数。

2026-08-30 增加 CG-007 cutoff cleanup 接线与 `couplet.index_stage.v2`。最终复核结果为 source Release build 0 warning/0 error、source lane 全量 89/89 PASS、默认 package lane 全量 62/62 PASS；source retention/wire 定向测试 12/12 PASS，default v1 wire 测试 1/1 PASS。v1 schema/payload 保持冻结。该结果关闭 API/接线缺口，但尚未重跑固定硬件 Medium/Large 或 7 天增长，不改变 C1 双门禁 FAIL。

2026-08-30 增加 source `code_search` exact/fulltext active-lease Preview 首切片并随后接入 `symbol_get` stable/qualified identity 有界索引查询。该阶段复核结果为 source Release build 0 warning/0 error、source lane 全量 92/92 PASS，默认 package Release build 0 warning/0 error、默认 lane 62/62 PASS；这是 cursor 接线前的历史计数。

2026-08-30 增加 fulltext `code_search` 同 active generation cursor/no-scan 合同。四条新回归覆盖多页顺序/完整集/无重复遗漏、tamper/query shape、序列化期间 publish 与旧 lease cleanup、有效签名负值/溢出/深页预算/空白 cursor；所有路径断言 `DocumentCollectionStore.FullScanCount` 不变。C1 generation/MCP 定向 24/24 PASS，source lane 全量 96/96 PASS，默认 package lane 全量 62/62 PASS。该阶段的 cursor 尚不跨请求保留 retired generation lease，active 切换后旧 cursor 显式 stale；FullText 深页仍是有预算的 Top-K + offset，不构成原生 search-after 或固定硬件性能证据。fulltext filter plan、真实双客户端与固定硬件/长稳门禁仍未完成，CG-005 和 C1 双门禁保持原状态。

2026-08-30 增加 fulltext path/language/entity-kind 过滤计划，并在 SonnetDB Core 增加 extend-only posting-stage filtered search。path glob 以有预算的 planning snapshot 匹配并通过 `by_path` 取候选，language/kind 走新增 path indexes，三类候选与 FullText posting 共用访问预算；cursor 绑定完整过滤 shape，预算耗尽不返回部分命中。最终复核为 SonnetDB Core `3869/3869 PASS`、Couplet source lane `101/101 PASS`、默认 package lane `62/62 PASS`；source CLI、Daemon、MCP Server 的 win-x64 Native AOT publish 均成功且无未处置 IL/AOT warning。该阶段结果关闭 fulltext filter plan 的本地接线缺口，但尚不提供 retired generation 跨请求 lease、真实子进程 fault、双客户端、Medium/Large 或 7 天长稳证据；后续条目记录前两项的小型本机回归。CG-005 保持 `verifying`，C1 双门禁保持 **FAIL**。

2026-08-30 继续补齐同进程跨请求 retired-generation cursor 与真实子进程 commit 边界恢复。fulltext cursor lease 采用默认两分钟绝对 TTL、128 slot、随机 nonce 和一次性所有权转移；主动 `TimeProvider` timer、取消/错误/最终页/容量/store dispose 均释放 lease，容量在 FullText 前 fail fast，exact 不占 slot，Document full-scan counter 保持不变。对抗回归进一步覆盖响应 IOException 后 cleanup、store dispose/reopen cleanup 与终页释放后的 slot 复用。真实 `Couplet.Cli` 子进程分别在 publish commit 前后握手后被强杀；未显式启用测试 hook 时 CLI fail closed，重开逐项对拍全部 Document exact 记录、FullText 命中 ID 集合和同一 expected generation，后续真实 CLI retry 分别发布或复用 revision 2 并清理 revision 1。协调者复跑 source Release build 为 0 warning/0 error，两个 cursor/crash 类 18/18、source lane 全量 111/111、默认 package lane 62/62 PASS；CLI、Daemon 与 MCP Server 的 win-x64 source Native AOT publish 均无未处置 IL/AOT warning，三个原生可执行文件的 `version` 烟测退出 0。该结果不提供断电/fsync、进程重启 cursor、Medium/Large 性能、双客户端、跨平台或 7 天长稳证据，CG-005 继续 `verifying`，C1 双门禁继续 **FAIL**。

2026-08-31 增加 durable orderly-reopen cursor、daemon watcher 与 revision provenance 加固。cursor registry 持久化 HMAC key、Available/Claimed CAS、绝对 TTL 和 128-slot，并通过 SonnetDB `Acquire(stream, revision)` 在 store dispose/reopen 后恢复 retired lease；38 项 cursor 定向回归覆盖 replay、过期、容量、坏记录、无效 generation metadata 与 transition/release/timer fault。provenance 25/25、workspace/indexing 16/16、generation publishing 8/8 通过；最终协调者复跑 watcher/provenance 37/37、source 全量 171/171、默认 package 90/90。CLI、Daemon、MCP Server 的 win-x64 source Native AOT publish 均无未处置 IL/AOT warning，三个最终原生 executable 的 `version` smoke 退出 0。binary HEAD probe 仍是 O(binary file count) 的 Git 进程数，symlink resolution 到实际 open 仍有一般 TOCTOU；真实进程重启/跨进程 cursor、hard-kill CAS、真实双客户端、跨平台、随机故障、固定硬件与 7 天证据均未运行，因此 `CG-005` 继续 `verifying`，C1 双门禁继续 **FAIL**。

Medium/Large 容量运行使用固定 manifest `38f906b9b65f88e11bb2953fa2ee45e97105815c13d6b8a364230da1ee9fb1b4`。Medium（1m LOC / 100k symbols）initial 74.814 s，100-file 73.086 s，peak RSS 4.648 GiB；Large（10m LOC / 1m symbols）initial 3,261.419 s，100-file 6,472.504 s，peak RSS 28.329 GiB。Large initial、两档增量和两档内存均未达目标；详细语料 hash、P50/P95/P99、allocation、reopen 与数据库放大见 [C1 Medium/Large 容量证据](c1-capacity-evidence.md)。

## 未通过门禁

- Correctness/Recovery：**FAIL**。source runtime 已实现 filter-aware revision provenance、daemon watcher、publish/no-op/reopen、writer fence、active/retained/exact-revision lease、durable orderly-reopen cursor、cutoff cleanup、真实子进程 commit 边界及 status/search/symbol 回归；仍缺真实进程重启/跨进程 cursor、hard-kill CAS、branch-switch 发布后查询语义、随机故障、跨平台和真实双客户端会话。
- Performance/Capacity：**FAIL**。Medium/Large 已有固定语料实测；Large initial、两档 100-file 路径和两档 peak RSS 未达门禁，initial/incremental/reopen 昂贵路径只有单样本，I/O 与 FullText candidates/examined 诊断也不完整。
- Native AOT lifecycle：默认 3.1.0 package 仍关闭不兼容 worker并报告 CG-006；最终 source CLI、Daemon 与 MCP Server 的 win-x64 publish 和原生 `version` smoke 均通过且为 0 个未处置 IL/AOT warning，生产 index/watch/MCP journey 与 7 天长稳仍未归档。
- 双客户端合同：Codex/Claude Code 的能力门控已验证；source status/search/symbol 与 orderly-reopen cursor 已有 typed 小型回归，但尚无真实双客户端发布后会话，真实进程重启/跨进程 cursor 仍 unavailable。

关闭 C1 还需完成真实进程重启/跨进程 cursor 与 hard-kill CAS、删除/重命名/branch switch 发布后查询、真实双客户端、跨平台/随机故障、固定硬件容量和 7 天增长联合回归。不得以应用层第二提交日志、直接查询 package staging、吞 dispose 异常或隐藏 maintenance 状态绕过。
