using Couplet.Core.Contracts;
using Couplet.Core.Evaluation;
using Couplet.Core.Mcp;

namespace Couplet.Application.Evaluation;

/// <summary>
/// 校验 C0 fixture、golden answer 和 paired eval manifest。
/// </summary>
public static class FixtureContractValidator
{
    private static readonly string[] _requiredScales = ["large", "medium", "small"];
    private static readonly string[] _requiredClients = ["Claude Code", "Codex"];
    private static readonly string[] _requiredCategories = ["impact", "large_context", "locate", "modify", "test_selection"];

    /// <summary>
    /// 校验三份互相绑定的 C0 manifest。
    /// </summary>
    /// <param name="manifest">语料 manifest。</param>
    /// <param name="goldenAnswers">golden answer 集。</param>
    /// <param name="agentEval">paired Agent eval manifest。</param>
    /// <returns>稳定排序的问题码。</returns>
    public static IReadOnlyList<string> Validate(
        FixtureManifest manifest,
        GoldenAnswerSet goldenAnswers,
        AgentEvalManifest agentEval)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(goldenAnswers);
        ArgumentNullException.ThrowIfNull(agentEval);
        var problems = new SortedSet<string>(StringComparer.Ordinal);

        if (manifest.SchemaVersion != ContractVersions.FixtureManifest)
        {
            problems.Add("fixture_schema_unsupported");
        }

        string[] scales = manifest.Scales.Select(scale => scale.Id).Order(StringComparer.Ordinal).ToArray();
        if (!scales.SequenceEqual(_requiredScales, StringComparer.Ordinal))
        {
            problems.Add("fixture_scales_incomplete");
        }

        foreach (CorpusScaleDefinition scale in manifest.Scales)
        {
            string[] families = scale.Languages.Select(language => language.Family).Distinct(StringComparer.Ordinal).ToArray();
            if (!families.Contains("csharp", StringComparer.Ordinal)
                || !families.Contains("typescript_javascript", StringComparer.Ordinal))
            {
                problems.Add($"fixture_languages_incomplete:{scale.Id}");
            }

            double totalShare = scale.Languages.Sum(language => language.Share);
            if (Math.Abs(totalShare - 1d) > 0.000_001d)
            {
                problems.Add($"fixture_language_share_invalid:{scale.Id}");
            }

            if (scale.TargetLinesOfCode <= 0 || scale.MinimumSymbols <= 0 || scale.MinimumRelations <= 0)
            {
                problems.Add($"fixture_scale_size_invalid:{scale.Id}");
            }
        }

        if (goldenAnswers.SchemaVersion != ContractVersions.GoldenAnswers
            || goldenAnswers.FixtureManifestId != manifest.Id)
        {
            problems.Add("golden_manifest_binding_invalid");
        }

        if (goldenAnswers.Answers.Count == 0
            || goldenAnswers.Answers.Any(answer => !McpToolNames.All.Contains(answer.Tool, StringComparer.Ordinal)))
        {
            problems.Add("golden_answers_invalid");
        }

        if (agentEval.SchemaVersion != ContractVersions.AgentEval
            || agentEval.FixtureManifestId != manifest.Id
            || agentEval.ToolSchemaVersion != ContractVersions.Mcp)
        {
            problems.Add("agent_eval_manifest_binding_invalid");
        }

        string[] clients = agentEval.Clients.Order(StringComparer.Ordinal).ToArray();
        if (!clients.SequenceEqual(_requiredClients, StringComparer.Ordinal))
        {
            problems.Add("agent_eval_clients_invalid");
        }

        if (agentEval.Repetitions < 5 || agentEval.Tasks.Count < 30)
        {
            problems.Add("agent_eval_sample_size_too_small");
        }

        string[] categories = agentEval.Tasks.Select(task => task.Category).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (!categories.SequenceEqual(_requiredCategories, StringComparer.Ordinal))
        {
            problems.Add("agent_eval_categories_incomplete");
        }

        if (agentEval.Tasks.Select(task => task.Id).Distinct(StringComparer.Ordinal).Count() != agentEval.Tasks.Count)
        {
            problems.Add("agent_eval_task_id_duplicate");
        }

        return problems.ToArray();
    }
}
