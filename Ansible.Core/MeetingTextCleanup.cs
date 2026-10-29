using System.Text.Json.Serialization;

namespace Ansible.Core;

public enum MeetingCleanupProvider { CopilotCli, OpenAI }

public interface IMeetingTextCleanup
{
    Task<IReadOnlyList<string>> CleanAsync(MeetingDocument document, MeetingCleanupOptions options,
        CancellationToken cancellationToken = default);
}

public sealed record MeetingCleanupSettings
{
    public bool Automatic { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<MeetingCleanupProvider>))]
    public MeetingCleanupProvider Provider { get; init; }
    public string CopilotModel { get; init; } = "gpt-6-luna";
    public string CopilotReasoningEffort { get; init; } = "high";
    public string OpenAIModel { get; init; } = "";
    public string OpenAIReasoningEffort { get; init; } = "high";

    [JsonIgnore]
    public MeetingCleanupOptions Options => Provider switch
    {
        MeetingCleanupProvider.CopilotCli => new(CopilotModel, CopilotReasoningEffort, Provider),
        MeetingCleanupProvider.OpenAI => new(OpenAIModel, OpenAIReasoningEffort, Provider),
        _ => throw new InvalidDataException("Choose a transcript cleanup provider.")
    };

    public void Validate()
    {
        if (!Enum.IsDefined(Provider)) throw new InvalidDataException("Choose a transcript cleanup provider.");
        if (OpenAIModel is null) throw new InvalidDataException("The OpenAI cleanup model setting is invalid.");
        new MeetingCleanupOptions(CopilotModel, CopilotReasoningEffort).Validate();
        new MeetingCleanupOptions(string.IsNullOrEmpty(OpenAIModel) ? "unconfigured" : OpenAIModel,
            OpenAIReasoningEffort, MeetingCleanupProvider.OpenAI).Validate();
        if (Automatic) Options.Validate();
    }
}

public sealed record MeetingCleanupOptions(string Model = "gpt-6-luna", string ReasoningEffort = "high",
    MeetingCleanupProvider Provider = MeetingCleanupProvider.CopilotCli)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 128 ||
            Model.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.' and not '/'))
            throw new ArgumentException("Enter a valid cleanup model identifier for the selected provider.", nameof(Model));
        if (!Enum.IsDefined(Provider)) throw new ArgumentException("Choose a transcript cleanup provider.", nameof(Provider));
        if (ReasoningEffort is not ("none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max"))
            throw new ArgumentException("Select a supported reasoning effort.", nameof(ReasoningEffort));
    }
}

internal sealed record CleanupTurn(int Id, string Text);
internal sealed record CleanupPayload(IReadOnlyList<CleanupTurn> Turns);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CleanupPayload))]
internal partial class CleanupJsonContext : JsonSerializerContext;
