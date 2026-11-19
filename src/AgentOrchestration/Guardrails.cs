using System.Text.RegularExpressions;

namespace AgentOrchestration;

/// <summary>Decides whether a side-effecting tool call may run. Production implementations ask a person.</summary>
public interface IApprovalGate
{
    Task<bool> ApproveAsync(ToolCall toolCall, ToolDefinition definition, CancellationToken cancellationToken);
}

/// <summary>Safe default: every write is denied unless an approval gate is configured.</summary>
public sealed class DenyAllApprovals : IApprovalGate
{
    public Task<bool> ApproveAsync(ToolCall toolCall, ToolDefinition definition, CancellationToken cancellationToken) => Task.FromResult(false);
}

public sealed class DelegateApprovalGate(Func<ToolCall, ToolDefinition, bool> decide) : IApprovalGate
{
    public Task<bool> ApproveAsync(ToolCall toolCall, ToolDefinition definition, CancellationToken cancellationToken) =>
        Task.FromResult(decide(toolCall, definition));
}

/// <summary>
/// Heuristic screen for instructions hidden in tool output (indirect prompt injection). It flags; it does not
/// make content safe. Tool output is always passed to the model as delimited, untrusted data.
/// </summary>
public static partial class PromptInjectionDetector
{
    [GeneratedRegex(@"ignore (all |any )?(previous|prior|above) (instructions|messages)|disregard (the )?(system|previous) prompt|you are now|new instructions:|system prompt|reveal (your|the) (prompt|instructions)|call the \w+ tool|transfer (all|the) (funds|money)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex Suspicious();

    public static bool LooksLikeInjection(string text) => Suspicious().IsMatch(text);
}

/// <summary>Redacts e-mail addresses and payment card numbers (Luhn-checked) from text leaving the agent.</summary>
public static partial class PiiRedactor
{
    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, 200)]
    private static partial Regex Email();

    [GeneratedRegex(@"\b(?:\d[ -]?){12,18}\d\b", RegexOptions.CultureInvariant, 200)]
    private static partial Regex CardCandidate();

    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = Email().Replace(text, "[email redacted]");
        return CardCandidate().Replace(result, m =>
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            return digits.Length is >= 13 and <= 19 && PassesLuhn(digits) ? $"[card ending {digits[^4..]}]" : m.Value;
        });
    }

    internal static bool PassesLuhn(string digits)
    {
        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (doubleIt)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
            doubleIt = !doubleIt;
        }

        return sum % 10 == 0;
    }
}
