using Couplet.Core.Evaluation;

namespace Couplet.Application.Evaluation;

/// <summary>
/// 校验 Codex 与 Claude Code baseline/enabled paired eval 的预注册配对。
/// </summary>
public static class PairedAgentEvalRunner
{
    /// <summary>
    /// 校验 eval 结果是否覆盖每个 client/task/repetition/condition。
    /// </summary>
    /// <param name="manifest">预注册 eval manifest。</param>
    /// <param name="results">观测结果。</param>
    /// <returns>runner 就绪和完整性结果。</returns>
    public static AgentEvalValidationResult Validate(AgentEvalManifest manifest, AgentEvalResultSet results)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(results);
        var problems = new SortedSet<string>(StringComparer.Ordinal);
        int expected = checked(manifest.Clients.Count * manifest.Tasks.Count * manifest.Repetitions * 2);

        if (results.State == "not_run")
        {
            if (results.Observations.Count != 0)
            {
                problems.Add("not_run_contains_observations");
            }

            return new AgentEvalValidationResult
            {
                RunnerReady = problems.Count == 0,
                Complete = false,
                ExpectedObservations = expected,
                ActualObservations = results.Observations.Count,
                Problems = problems.ToArray(),
            };
        }

        if (results.State != "completed")
        {
            problems.Add("agent_eval_state_invalid");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (AgentEvalObservation observation in results.Observations)
        {
            if (!manifest.Clients.Contains(observation.Client, StringComparer.Ordinal)
                || !manifest.Tasks.Any(task => task.Id == observation.TaskId)
                || observation.Repetition < 0
                || observation.Repetition >= manifest.Repetitions
                || observation.Condition is not ("baseline" or "enabled"))
            {
                problems.Add("agent_eval_observation_out_of_manifest");
                continue;
            }

            string key = $"{observation.Client}\n{observation.TaskId}\n{observation.Repetition}\n{observation.Condition}";
            if (!keys.Add(key))
            {
                problems.Add("agent_eval_observation_duplicate");
            }
        }

        if (keys.Count != expected)
        {
            problems.Add("agent_eval_pair_incomplete");
        }

        return new AgentEvalValidationResult
        {
            RunnerReady = true,
            Complete = problems.Count == 0,
            ExpectedObservations = expected,
            ActualObservations = results.Observations.Count,
            Problems = problems.ToArray(),
        };
    }
}
