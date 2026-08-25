# C0 合同与 Evidence Runner

## 冻结输入

[`contracts/c0-handshake.v1.json`](../contracts/c0-handshake.v1.json) 是 Couplet C0 与 SonnetDB M40 `#341` 的联合版本记录。它固定：

- 代码图、generation、capability handshake、安全、MCP、fixture、golden answer、Agent eval 和 evidence schema 版本；
- 官方 `SonnetDB.Core 3.1.0` package content hash 与 informational commit；
- Small/Medium/Large fixture、golden answers 和 Agent eval manifest 的 SHA-256；
- C0 完成但 C1/C2/C3 产品能力仍 unavailable 的发布边界。

fixture 是确定性生成语料，不提交 100k/1m/10m LOC 输出。`fixture-generate` 流式生成 C# 与 TypeScript/JavaScript 两个 adapter family；档位是验收规模，不是产品上限。

## 命令

```powershell
dotnet run --project src/Couplet.Cli -- c0-evidence --repository . --commit <commit>
dotnet run --project src/Couplet.Cli -- fixture-generate --repository . --scale small --output <empty-directory>
```

`c0-evidence` 校验全部冻结输入并执行 stable ID 合同 microbenchmark，输出硬件/runtime、manifest hash、P50/P95/P99、样本数和 actual access path。无法自动发现存储型号时，固定硬件取证必须显式设置 `COUPLET_STORAGE_MODEL`；`explicit_unknown` 不能作为容量发布报告。

paired Agent eval runner 校验每个 client/task/repetition 的 baseline/enabled 成对观测，预注册 Codex 与 Claude Code、30 个任务、5 次重复和五类任务。C0 只证明 runner/manifest 就绪，结果状态固定为 `not_run`；实际质量、token、时间和测试选择门禁属于 C3/C4。

## 当前证据边界

2026-08-25 本地验证：Release 0 warning/0 error，35 个 C0 测试通过，CLI evidence `contracts_passed=true`、`agent_eval_runner_ready=true`。MCP 真实 stdio smoke 覆盖 `initialize`、`tools/list` 和 unavailable `tools/call`。这些证据不替代 C1-C4 的真实索引、恢复、容量或 Agent 效果报告。
