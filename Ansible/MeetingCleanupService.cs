using Ansible.Core;
using System.Diagnostics;
using Windows.Security.Credentials;

namespace Ansible;

internal sealed class MeetingCleanupService(LocalDiagnosticLog? diagnostics = null) : IMeetingTextCleanup
{
    private const string CredentialResource = "Ansible.OpenAI.TranscriptCleanup";
    private const string CredentialUser = "api-key";
    private const int ElementNotFound = unchecked((int)0x80070490);
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };
    private readonly CopilotMeetingTextCleanup _copilot = new();
    private readonly OpenAIMeetingTextCleanup _openAI = new(Http, ReadKey);

    public async Task<IReadOnlyList<string>> CleanAsync(MeetingDocument document, MeetingCleanupOptions options,
        CancellationToken cancellationToken = default)
    {
        var generation = diagnostics?.CurrentGeneration;
        var started = generation is null ? 0 : Stopwatch.GetTimestamp();
        Exception? failure = null;
        try
        {
            return await (options.Provider switch
            {
                MeetingCleanupProvider.CopilotCli => _copilot.CleanAsync(document, options, cancellationToken),
                MeetingCleanupProvider.OpenAI => _openAI.CleanAsync(document, options, cancellationToken),
                _ => throw new ArgumentException("Choose a transcript cleanup provider.")
            });
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            if (generation is { } enabled)
                diagnostics!.Write(new ApplicationDiagnostic(DateTimeOffset.UtcNow,
                    options.Provider == MeetingCleanupProvider.CopilotCli
                        ? ApplicationDiagnosticStage.CleanupCopilot : ApplicationDiagnosticStage.CleanupOpenAI,
                    failure is OperationCanceledException && cancellationToken.IsCancellationRequested
                        ? DiagnosticOutcome.Cancelled : failure is null ? DiagnosticOutcome.Completed : DiagnosticOutcome.Failed,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds, failure?.HResult), enabled);
        }
    }

    private static PasswordCredential? FindKey()
    {
        try { return new PasswordVault().Retrieve(CredentialResource, CredentialUser); }
        catch (Exception error) when (error.HResult == ElementNotFound) { return null; }
    }

    internal static bool HasKey => FindKey() is not null;

    private static string? ReadKey()
    {
        var credential = FindKey();
        if (credential is null) return null;
        credential.RetrievePassword();
        return credential.Password;
    }

    internal static void SaveKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsWhiteSpace))
            throw new ArgumentException("Enter an OpenAI API key without spaces.");
        new PasswordVault().Add(new PasswordCredential(CredentialResource, CredentialUser, key));
    }

    internal static void RemoveKey()
    {
        if (FindKey() is { } credential) new PasswordVault().Remove(credential);
    }
}
