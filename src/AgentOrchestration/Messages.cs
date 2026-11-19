using System.Text.Json;

namespace AgentOrchestration;

public enum ChatRole { System, User, Assistant, Tool }

/// <summary>A tool invocation requested by the model.</summary>
public sealed record ToolCall(string Id, string Name, JsonElement Arguments)
{
    /// <summary>Stable key used for loop detection: same tool with the same arguments.</summary>
    public string Fingerprint => $"{Name}:{Arguments.GetRawText()}";
}

public sealed record ChatMessage(ChatRole Role, string Content, string? ToolCallId = null, IReadOnlyList<ToolCall>? ToolCalls = null);

public sealed record TokenUsage(int Input, int Output)
{
    public int Total => Input + Output;
}

/// <summary>One model turn: either a final answer or one or more tool calls.</summary>
public sealed record ModelResponse(string? Text, IReadOnlyList<ToolCall> ToolCalls, TokenUsage Usage)
{
    public bool IsFinal => ToolCalls.Count == 0;

    public static ModelResponse Final(string text, TokenUsage? usage = null) =>
        new(text, Array.Empty<ToolCall>(), usage ?? new TokenUsage(0, 0));

    public static ModelResponse CallTools(TokenUsage? usage = null, params ToolCall[] calls) =>
        new(null, calls, usage ?? new TokenUsage(0, 0));
}

/// <summary>Provider-agnostic chat model. Adapters (Azure OpenAI, OpenAI, local models) implement this.</summary>
public interface IChatModel
{
    Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken);
}

/// <summary>
/// Deterministic model that replays a script. Used for tests and offline evaluation so agent behaviour
/// can be checked in CI without network access, cost or non-determinism.
/// </summary>
public sealed class ScriptedChatModel : IChatModel
{
    private readonly Queue<Func<IReadOnlyList<ChatMessage>, ModelResponse>> _turns;

    public ScriptedChatModel(IEnumerable<Func<IReadOnlyList<ChatMessage>, ModelResponse>> turns) => _turns = new(turns);

    public ScriptedChatModel(params ModelResponse[] turns) : this(turns.Select(t => (Func<IReadOnlyList<ChatMessage>, ModelResponse>)(_ => t))) { }

    /// <summary>Messages the model saw on each call, for assertions.</summary>
    public List<IReadOnlyList<ChatMessage>> Seen { get; } = [];

    public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Seen.Add(messages.ToList());
        if (_turns.Count == 0)
        {
            throw new InvalidOperationException("Scripted model has no more turns.");
        }

        return Task.FromResult(_turns.Dequeue()(messages));
    }

    public static ToolCall Call(string id, string name, object arguments) =>
        new(id, name, JsonSerializer.SerializeToElement(arguments));
}
