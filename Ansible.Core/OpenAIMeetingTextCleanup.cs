using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansible.Core;

public sealed class OpenAIMeetingTextCleanup(HttpClient httpClient, Func<string?> getApiKey) : IMeetingTextCleanup
{
    public async Task<IReadOnlyList<string>> CleanAsync(MeetingDocument document, MeetingCleanupOptions options,
        CancellationToken cancellationToken = default)
    {
        var batches = MeetingCleanupText.Batches(document, options);
        cancellationToken.ThrowIfCancellationRequested();
        if (batches.Count == 0) return [];
        var key = getApiKey();
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Save an OpenAI API key in Settings before cleaning a transcript with OpenAI.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var result = new List<string>(document.Turns.Count);
        try
        {
            foreach (var batch in batches)
            {
                using var schema = JsonDocument.Parse("""
                    {"type":"object","properties":{"turns":{"type":"array","items":{"type":"object",
                    "properties":{"id":{"type":"integer"},"text":{"type":"string"}},
                    "required":["id","text"],"additionalProperties":false}}},
                    "required":["turns"],"additionalProperties":false}
                    """);
                var payload = new OpenAICleanupRequest(options.Model, MeetingCleanupText.Instructions,
                    MeetingCleanupText.Input(batch), new(options.ReasoningEffort),
                    new(new("json_schema", "transcript_cleanup", true, schema.RootElement)), false);
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Content = new StringContent(JsonSerializer.Serialize(payload,
                    OpenAICleanupJsonContext.Default.OpenAICleanupRequest), Encoding.UTF8, "application/json");
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException(FailureMessage(response.StatusCode));
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token), Encoding.UTF8);
                var content = new StringBuilder();
                var buffer = new char[4096];
                int read;
                while ((read = await reader.ReadAsync(buffer.AsMemory(), timeout.Token)) > 0)
                {
                    if (content.Length + read > MeetingCleanupText.MaximumOutputCharacters) throw MeetingCleanupText.InvalidResponse();
                    content.Append(buffer, 0, read);
                }
                result.AddRange(ParseOutput(content.ToString(), batch));
            }
            timeout.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("OpenAI transcript cleanup timed out. The original transcript is preserved.");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("OpenAI could not be reached. Check the network connection. The original transcript is preserved.");
        }
    }

    internal static string FailureMessage(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "OpenAI rejected the API key. Replace it in Transcript cleanup Settings. The original transcript is preserved.",
        HttpStatusCode.Forbidden => "Your OpenAI API project cannot access this request. Check model permissions and project access. The original transcript is preserved.",
        HttpStatusCode.TooManyRequests => "OpenAI rate limit or API quota reached. Check API billing and limits before retrying. The original transcript is preserved.",
        HttpStatusCode.BadRequest or HttpStatusCode.NotFound =>
            "OpenAI rejected the cleanup model, reasoning effort, or request format. Use an OpenAI API model that supports Responses and structured outputs. Copilot model names are not automatically mapped. The original transcript is preserved.",
        _ => $"OpenAI cleanup failed with HTTP {(int)status}. The original transcript is preserved."
    };

    internal static IReadOnlyList<string> ParseOutput(string output, IReadOnlyList<CleanupTurn> expected)
    {
        try
        {
            using var json = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 32, AllowDuplicateProperties = false });
            var root = json.RootElement;
            if (root.GetProperty("status").GetString() != "completed") throw MeetingCleanupText.InvalidResponse();
            string? text = null;
            foreach (var item in root.GetProperty("output").EnumerateArray())
            {
                var type = item.GetProperty("type").GetString();
                if (type == "reasoning") continue;
                if (type != "message" || item.GetProperty("role").GetString() != "assistant" ||
                    item.GetProperty("status").GetString() != "completed") throw MeetingCleanupText.InvalidResponse();
                foreach (var part in item.GetProperty("content").EnumerateArray())
                {
                    if (part.GetProperty("type").GetString() != "output_text" || text is not null) throw MeetingCleanupText.InvalidResponse();
                    text = part.GetProperty("text").GetString();
                }
            }
            if (string.IsNullOrWhiteSpace(text)) throw MeetingCleanupText.InvalidResponse();
            return MeetingCleanupText.ParseResponse(text, expected);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw MeetingCleanupText.InvalidResponse();
        }
    }
}

internal sealed record OpenAICleanupRequest(string Model, string Instructions, string Input,
    OpenAICleanupReasoning Reasoning, OpenAICleanupText Text, bool Store);
internal sealed record OpenAICleanupReasoning(string Effort);
internal sealed record OpenAICleanupText(OpenAICleanupFormat Format);
internal sealed record OpenAICleanupFormat(string Type, string Name, bool Strict, JsonElement Schema);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(OpenAICleanupRequest))]
internal partial class OpenAICleanupJsonContext : JsonSerializerContext;
