using System.Text.Json.Serialization;

namespace Ansible.Core;

public sealed record MeetingCleanupOptions(string Model = "gpt-6-luna", string ReasoningEffort = "high")
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 128 ||
            Model.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.' and not '/'))
            throw new ArgumentException("Enter a valid Copilot model identifier.", nameof(Model));
        if (ReasoningEffort is not ("none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max"))
            throw new ArgumentException("Select a supported reasoning effort.", nameof(ReasoningEffort));
    }
}

public interface IMeetingTextCleanup
{
    Task<IReadOnlyList<string>> CleanAsync(MeetingDocument document, MeetingCleanupOptions options,
        CancellationToken cancellationToken = default);
}

internal sealed record CleanupTurn(int Id, string Text);
internal sealed record CleanupPayload(IReadOnlyList<CleanupTurn> Turns);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CleanupPayload))]
internal partial class CleanupJsonContext : JsonSerializerContext;
