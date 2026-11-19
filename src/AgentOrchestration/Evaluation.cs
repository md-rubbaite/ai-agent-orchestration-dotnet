namespace AgentOrchestration;

/// <summary>
/// An offline evaluation case: a scripted model plus the behaviour the agent must show.
/// Scenarios run in CI without a real model, so regressions in orchestration logic are caught on every change.
/// </summary>
public sealed record EvalScenario(
    string Name,
    string UserMessage,
    Func<ScriptedChatModel> Model,
    RunStatus ExpectedStatus,
    IReadOnlyList<string>? MustCall = null,
    IReadOnlyList<string>? MustNotCall = null,
    string? AnswerMustContain = null,
    string? AnswerMustNotContain = null,
    IReadOnlyList<StepKind>? MustTrace = null);

public sealed record EvalOutcome(string Scenario, bool Passed, IReadOnlyList<string> Failures);

public sealed record EvalReport(IReadOnlyList<EvalOutcome> Outcomes)
{
    public double PassRate => Outcomes.Count == 0 ? 0 : (double)Outcomes.Count(o => o.Passed) / Outcomes.Count;
}

public static class EvalRunner
{
    public static async Task<EvalReport> RunAsync(
        IEnumerable<EvalScenario> scenarios,
        Func<ToolRegistry> tools,
        string systemPrompt,
        IApprovalGate? approvals = null,
        AgentPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        ArgumentNullException.ThrowIfNull(tools);
        var outcomes = new List<EvalOutcome>();
        foreach (var s in scenarios)
        {
            var result = await new AgentRunner(s.Model(), tools(), approvals, policy).RunAsync(systemPrompt, s.UserMessage).ConfigureAwait(false);
            var failures = new List<string>();
            if (result.Status != s.ExpectedStatus)
            {
                failures.Add($"status {result.Status}, expected {s.ExpectedStatus}");
            }

            failures.AddRange((s.MustCall ?? []).Where(t => !result.ToolsExecuted.Contains(t)).Select(t => $"did not call {t}"));
            failures.AddRange((s.MustNotCall ?? []).Where(result.ToolsExecuted.Contains).Select(t => $"called forbidden {t}"));
            if (s.AnswerMustContain is { } must && !(result.FinalAnswer ?? string.Empty).Contains(must, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"answer missing '{must}'");
            }

            if (s.AnswerMustNotContain is { } mustNot && (result.FinalAnswer ?? string.Empty).Contains(mustNot, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"answer contains '{mustNot}'");
            }

            failures.AddRange((s.MustTrace ?? []).Where(k => result.Trace.All(t => t.Kind != k)).Select(k => $"trace missing {k}"));
            outcomes.Add(new(s.Name, failures.Count == 0, failures));
        }

        return new EvalReport(outcomes);
    }
}
