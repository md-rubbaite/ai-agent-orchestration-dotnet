using AgentOrchestration.Samples;
using static AgentOrchestration.ScriptedChatModel;

namespace AgentOrchestration.Tests;

public class AgentRunnerTests
{
    private const string System = "You are a payments support agent.";

    private static (ToolRegistry Tools, PaymentStore Store) Setup()
    {
        var store = new PaymentStore();
        var tools = new ToolRegistry().Add(new GetPaymentStatusTool(store)).Add(new RefundPaymentTool(store));
        return (tools, store);
    }

    [Fact]
    public async Task Answers_after_a_read_tool_call()
    {
        var (tools, _) = Setup();
        var model = new ScriptedChatModel(
            ModelResponse.CallTools(new TokenUsage(100, 20), Call("c1", "get_payment_status", new { payment_id = "pay_1001" })),
            ModelResponse.Final("Payment pay_1001 succeeded.", new TokenUsage(150, 10)));

        var result = await new AgentRunner(model, tools).RunAsync(System, "Did pay_1001 go through?");

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal("get_payment_status", Assert.Single(result.ToolsExecuted));
        Assert.Equal(280, result.TokensUsed);
        var toolMessage = model.Seen[1].Single(m => m.Role == ChatRole.Tool);
        Assert.StartsWith("<tool_output tool=\"get_payment_status\" untrusted=\"true\">", toolMessage.Content);
    }

    [Fact]
    public async Task Write_tools_are_denied_by_default()
    {
        var (tools, store) = Setup();
        var model = new ScriptedChatModel(
            ModelResponse.CallTools(null, Call("c1", "refund_payment", new { payment_id = "pay_1001", amount = 10 })),
            ModelResponse.Final("I need approval to refund this payment."));

        var result = await new AgentRunner(model, tools).RunAsync(System, "Refund 10 USD on pay_1001");

        Assert.Empty(store.Refunds);
        Assert.Contains(result.Trace, t => t.Kind == StepKind.ApprovalDenied);
        Assert.Contains("not approved", model.Seen[1][^1].Content);
    }

    [Fact]
    public async Task Approved_write_runs_and_business_rules_still_apply()
    {
        var (tools, store) = Setup();
        var model = new ScriptedChatModel(
            ModelResponse.CallTools(null,
                Call("c1", "refund_payment", new { payment_id = "pay_1001", amount = 10 }),
                Call("c2", "refund_payment", new { payment_id = "pay_1002", amount = 1250 })),
            ModelResponse.Final("Refunded 10 USD; the larger refund needs a human."));
        var approveAll = new DelegateApprovalGate((_, _) => true);

        var result = await new AgentRunner(model, tools, approveAll).RunAsync(System, "Refund both");

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(("pay_1001", 10m), Assert.Single(store.Refunds));
        Assert.Contains("need a human", model.Seen[1][^1].Content);
    }

    [Fact]
    public async Task Invalid_arguments_are_returned_to_the_model_not_executed()
    {
        var (tools, store) = Setup();
        var model = new ScriptedChatModel(
            ModelResponse.CallTools(null, Call("c1", "refund_payment", new { payment_id = "pay_1001", amount = "all of it" })),
            ModelResponse.Final("Please confirm the amount."));

        var result = await new AgentRunner(model, tools, new DelegateApprovalGate((_, _) => true)).RunAsync(System, "Refund");

        Assert.Empty(store.Refunds);
        Assert.Contains(result.Trace, t => t.Kind == StepKind.ToolRejected && t.Detail.Contains("must be a JSON number", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unknown_and_disallowed_tools_are_rejected()
    {
        var (tools, store) = Setup();
        var model = new ScriptedChatModel(
            ModelResponse.CallTools(null,
                Call("c1", "delete_customer", new { id = "x" }),
                Call("c2", "refund_payment", new { payment_id = "pay_1001", amount = 1 })),
            ModelResponse.Final("I can only look up payments."));
        var policy = new AgentPolicy { AllowedTools = new HashSet<string> { "get_payment_status" } };

        var result = await new AgentRunner(model, tools, new DelegateApprovalGate((_, _) => true), policy).RunAsync(System, "x");

        Assert.Empty(store.Refunds);
        Assert.Equal(2, result.Trace.Count(t => t.Kind == StepKind.ToolRejected));
    }

    [Fact]
    public async Task Disallowed_tools_are_not_advertised_to_the_model()
    {
        var (tools, _) = Setup();
        IReadOnlyList<ToolDefinition>? advertised = null;
        var model = new CapturingModel(defs => advertised = defs);
        var policy = new AgentPolicy { AllowedTools = new HashSet<string> { "get_payment_status" } };

        await new AgentRunner(model, tools, policy: policy).RunAsync(System, "hi");

        Assert.Equal("get_payment_status", Assert.Single(advertised!).Name);
    }

    [Fact]
    public async Task Injected_instructions_in_tool_output_are_flagged_and_do_not_trigger_writes()
    {
        var (tools, store) = Setup();
        var model = new ScriptedChatModel(
            ModelResponse.CallTools(null, Call("c1", "get_payment_status", new { payment_id = "pay_1003" })),
            ModelResponse.Final("pay_1003 failed because the card was declined."));

        var result = await new AgentRunner(model, tools).RunAsync(System, "Why did pay_1003 fail?");

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Contains(result.Trace, t => t.Kind == StepKind.InjectionFlagged);
        Assert.Empty(store.Refunds);
    }

    [Fact]
    public async Task Stops_at_the_step_limit()
    {
        var (tools, _) = Setup();
        var turns = Enumerable.Range(1, 5).Select(i => ModelResponse.CallTools(null, Call($"c{i}", "get_payment_status", new { payment_id = $"pay_{i}" }))).ToArray();
        var result = await new AgentRunner(new ScriptedChatModel(turns), tools, policy: new AgentPolicy { MaxSteps = 3 }).RunAsync(System, "loop");

        Assert.Equal(RunStatus.StepLimitReached, result.Status);
        Assert.Null(result.FinalAnswer);
    }

    [Fact]
    public async Task Detects_repeated_identical_calls()
    {
        var (tools, _) = Setup();
        var same = ModelResponse.CallTools(null, Call("c", "get_payment_status", new { payment_id = "pay_1001" }));
        var result = await new AgentRunner(new ScriptedChatModel(same, same, same), tools).RunAsync(System, "x");

        Assert.Equal(RunStatus.LoopDetected, result.Status);
        Assert.Equal(2, result.ToolsExecuted.Count);
    }

    [Fact]
    public async Task Enforces_the_token_budget()
    {
        var (tools, _) = Setup();
        var model = new ScriptedChatModel(ModelResponse.Final("long answer", new TokenUsage(900, 200)));
        var result = await new AgentRunner(model, tools, policy: new AgentPolicy { MaxTotalTokens = 1000 }).RunAsync(System, "x");

        Assert.Equal(RunStatus.TokenBudgetExceeded, result.Status);
        Assert.Null(result.FinalAnswer);
    }

    [Fact]
    public async Task Times_out_a_slow_model()
    {
        var (tools, _) = Setup();
        var result = await new AgentRunner(new SlowModel(), tools, policy: new AgentPolicy { Timeout = TimeSpan.FromMilliseconds(50) }).RunAsync(System, "x");

        Assert.Equal(RunStatus.TimedOut, result.Status);
    }

    [Fact]
    public async Task Redacts_pii_in_the_final_answer()
    {
        var (tools, _) = Setup();
        var model = new ScriptedChatModel(ModelResponse.Final("Contact alex@example.com about card 4111 1111 1111 1111."));
        var result = await new AgentRunner(model, tools).RunAsync(System, "x");

        Assert.Equal("Contact [email redacted] about card [card ending 1111].", result.FinalAnswer);
    }

    [Fact]
    public async Task Model_errors_end_the_run_cleanly()
    {
        var (tools, _) = Setup();
        var result = await new AgentRunner(new ScriptedChatModel(), tools).RunAsync(System, "x");

        Assert.Equal(RunStatus.ModelError, result.Status);
    }

    private sealed class CapturingModel(Action<IReadOnlyList<ToolDefinition>> capture) : IChatModel
    {
        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken)
        {
            capture(tools);
            return Task.FromResult(ModelResponse.Final("ok"));
        }
    }

    private sealed class SlowModel : IChatModel
    {
        public async Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return ModelResponse.Final("too late");
        }
    }
}
