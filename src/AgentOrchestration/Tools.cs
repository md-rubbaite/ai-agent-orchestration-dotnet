using System.Text.Json;

namespace AgentOrchestration;

/// <summary>Read tools only observe state. Write tools change it and always pass the approval gate.</summary>
public enum ToolEffect { Read, Write }

/// <summary>JSON value kinds a tool parameter accepts.</summary>
public enum ParameterType { Text, Number, Flag }

public sealed record ToolParameter(string Name, ParameterType Type, string Description, bool Required = true);

public sealed record ToolDefinition(string Name, string Description, ToolEffect Effect, IReadOnlyList<ToolParameter> Parameters);

public sealed record ToolResult(bool Success, string Content)
{
    public static ToolResult Ok(string content) => new(true, content);

    public static ToolResult Error(string message) => new(false, message);
}

public interface ITool
{
    ToolDefinition Definition { get; }

    Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken);
}

/// <summary>Holds the tools an agent may use and validates model-supplied arguments before any tool runs.</summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.Ordinal);

    public ToolRegistry Add(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!_tools.TryAdd(tool.Definition.Name, tool))
        {
            throw new ArgumentException($"Tool '{tool.Definition.Name}' is already registered.", nameof(tool));
        }

        return this;
    }

    public IReadOnlyList<ToolDefinition> Definitions => _tools.Values.Select(t => t.Definition).ToList();

    public bool TryGet(string name, out ITool tool) => _tools.TryGetValue(name, out tool!);

    /// <summary>Returns null when valid, otherwise a message that is sent back to the model.</summary>
    public static string? Validate(ToolDefinition definition, JsonElement arguments)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return "Arguments must be a JSON object.";
        }

        var known = definition.Parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!known.Contains(property.Name))
            {
                return $"Unknown argument '{property.Name}'.";
            }
        }

        foreach (var p in definition.Parameters)
        {
            if (!arguments.TryGetProperty(p.Name, out var value))
            {
                if (p.Required)
                {
                    return $"Missing required argument '{p.Name}'.";
                }

                continue;
            }

            var ok = p.Type switch
            {
                ParameterType.Text => value.ValueKind == JsonValueKind.String,
                ParameterType.Number => value.ValueKind == JsonValueKind.Number,
                ParameterType.Flag => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                _ => false,
            };
            if (!ok)
            {
                return $"Argument '{p.Name}' must be a JSON {JsonKind(p.Type)}.";
            }
        }

        return null;
    }

    private static string JsonKind(ParameterType type) => type switch
    {
        ParameterType.Text => "string",
        ParameterType.Number => "number",
        _ => "boolean",
    };
}
