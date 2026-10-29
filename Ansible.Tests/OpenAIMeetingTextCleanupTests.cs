using System.Net;
using System.Text;
using System.Text.Json;
using Ansible.Core;

namespace Ansible.Tests;

public sealed class OpenAIMeetingTextCleanupTests
{
    private static MeetingDocument Document(int count = 1) => new()
    {
        Status = MeetingStatus.Completed,
        Turns = Enumerable.Range(0, count).Select(index => new MeetingTurn("Private speaker", index, index + 1, "raw words")).ToArray(),
        SpeakerNames = new Dictionary<string, string> { ["Private speaker"] = "Private participant" }
    };

    private static string Response(params (int id, string text)[] turns)
    {
        var text = JsonSerializer.Serialize(new { turns = turns.Select(turn => new { turn.id, turn.text }) });
        return JsonSerializer.Serialize(new
        {
            status = "completed",
            output = new[] { new { type = "message", role = "assistant", status = "completed",
                content = new[] { new { type = "output_text", text } } } }
        });
    }

    [Fact]
    public async Task SendsOnlyOriginalWordingToFixedEndpointWithPrivateStructuredRequest()
    {
        var document = Document() with { Turns = [new("Private speaker", 0, 1, "earlier cleanup") { OriginalText = "raw words" }] };
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            Assert.Equal("https://api.openai.com/v1/responses", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("synthetic-key", request.Headers.Authorization.Parameter);
            var body = await request.Content!.ReadAsStringAsync(token);
            Assert.DoesNotContain("Private speaker", body);
            Assert.DoesNotContain("Private participant", body);
            Assert.DoesNotContain("earlier cleanup", body);
            Assert.DoesNotContain("synthetic-key", body);
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            Assert.Equal("api-model", root.GetProperty("model").GetString());
            Assert.Equal("low", root.GetProperty("reasoning").GetProperty("effort").GetString());
            Assert.False(root.GetProperty("store").GetBoolean());
            Assert.False(root.TryGetProperty("tools", out _));
            Assert.Equal("json_schema", root.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
            Assert.True(root.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
            using var input = JsonDocument.Parse(root.GetProperty("input").GetString()!);
            Assert.Equal("raw words", input.RootElement.GetProperty("turns")[0].GetProperty("text").GetString());
            return Ok(Response((0, "Raw words.")));
        }));
        var cleaned = await new OpenAIMeetingTextCleanup(http, () => "synthetic-key")
            .CleanAsync(document, new("api-model", "low", MeetingCleanupProvider.OpenAI));
        Assert.Equal("Raw words.", Assert.Single(cleaned));
        Assert.Equal("earlier cleanup", document.Turns[0].Text);
    }

    [Fact]
    public async Task MissingKeyAndUnfinishedMeetingsDoNotSendRequests()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Must not send")));
        var service = new OpenAIMeetingTextCleanup(http, () => null);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CleanAsync(Document(), new()));
        Assert.Contains("API key", error.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CleanAsync(Document() with { Status = MeetingStatus.Recording }, new()));
    }

    [Theory]
    [InlineData(401, "API key")]
    [InlineData(403, "permissions")]
    [InlineData(429, "billing")]
    [InlineData(400, "model")]
    [InlineData(404, "model")]
    [InlineData(503, "503")]
    public async Task ErrorsHaveUsefulGuidanceWithoutLeakingResponseOrSecrets(int status, string guidance)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent("PRIVATE-ERROR-DETAILS") })));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OpenAIMeetingTextCleanup(http, () => "synthetic-secret").CleanAsync(Document(), new()));
        Assert.Contains(guidance, error.Message);
        Assert.DoesNotContain("PRIVATE", error.Message);
        Assert.DoesNotContain("synthetic-secret", error.Message);
    }

    [Fact]
    public async Task LaterBatchFailureReturnsNoPartialChangesAndPreservesGlobalIds()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            using var input = JsonDocument.Parse(json.RootElement.GetProperty("input").GetString()!);
            var ids = input.RootElement.GetProperty("turns").EnumerateArray().Select(turn => turn.GetProperty("id").GetInt32()).ToArray();
            if (++calls == 1)
            {
                Assert.Equal(Enumerable.Range(0, 64), ids);
                return Ok(Response(ids.Select(id => (id, "Cleaned.")).ToArray()));
            }
            Assert.Equal(new[] { 64 }, ids);
            return new(HttpStatusCode.ServiceUnavailable);
        }));
        var document = Document(65);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OpenAIMeetingTextCleanup(http, () => "synthetic").CleanAsync(document, new()));
        Assert.Equal(2, calls);
        Assert.All(document.Turns, turn => Assert.Equal("raw words", turn.Text));
    }

    [Fact]
    public async Task CancellationIsNotReportedAsTimeoutAndNetworkFailureDoesNotLeakDetails()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return Ok("");
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OpenAIMeetingTextCleanup(http, () => "synthetic").CleanAsync(Document(), new(), cancellation.Token));
        using var disconnected = new HttpClient(new Handler((_, _) => throw new HttpRequestException("PRIVATE")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OpenAIMeetingTextCleanup(disconnected, () => "synthetic").CleanAsync(Document(), new()));
        Assert.Contains("network", error.Message);
        Assert.DoesNotContain("PRIVATE", error.Message);
    }

    [Theory]
    [InlineData("""{"status":"incomplete","output":[]}""")]
    [InlineData("""{"status":"completed","output":[{"type":"function_call"}]}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","role":"assistant","status":"completed","content":[{"type":"refusal","refusal":"No"}]}]}""")]
    [InlineData("""{"status":"completed","output":[]}""")]
    public void IncompleteRefusedAndToolResponsesAreRejected(string output) =>
        Assert.Throws<InvalidDataException>(() => OpenAIMeetingTextCleanup.ParseOutput(output, [new(0, "raw words")]));

    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK)
        { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
