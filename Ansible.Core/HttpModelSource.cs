using System.Net;
using System.Net.Http.Headers;

namespace Ansible.Core;

internal interface IModelSource
{
    Task<ModelSourceRead> OpenAsync(NativeModelEntry entry, long offset, string? etag, CancellationToken token);
}

internal sealed class ModelSourceRead(Stream content, string? etag, int? httpStatus = null, IDisposable? owner = null)
    : IAsyncDisposable
{
    public Stream Content { get; } = content;
    public string? ETag { get; } = etag;
    public int? HttpStatus { get; } = httpStatus;

    public async ValueTask DisposeAsync()
    {
        try { await Content.DisposeAsync(); }
        finally { owner?.Dispose(); }
    }
}

internal sealed class HttpModelSource(HttpClient http) : IModelSource
{
    internal static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(30),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<ModelSourceRead> OpenAsync(
        NativeModelEntry entry, long offset, string? etag, CancellationToken token)
    {
        var response = await GetResponseAsync(entry.DownloadUri, offset, etag, token);
        try
        {
            ValidateResponse(response, entry, offset, etag);
            var tag = response.Headers.ETag is { IsWeak: false } value ? value.ToString() : null;
            var content = await response.Content.ReadAsStreamAsync(token);
            return new ModelSourceRead(content, tag, (int)response.StatusCode, response);
        }
        catch { response.Dispose(); throw; }
    }

    private async Task<HttpResponseMessage> GetResponseAsync(Uri uri, long offset, string? etag, CancellationToken token)
    {
        for (var redirects = 0; redirects <= 8; redirects++)
        {
            ValidateDownloadUri(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
                if (etag is not null) { request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(etag)); }
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new TimeoutException("The model server did not respond within 30 seconds. Retry to resume."); }
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) { throw new InvalidDataException("The download redirect has no destination."); }
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
        throw new InvalidDataException("The model download exceeded eight redirects.");
    }

    private static void ValidateDownloadUri(Uri uri)
    {
        var host = uri.IdnHost;
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0 ||
            !(host == "huggingface.co" || host.EndsWith(".huggingface.co", StringComparison.OrdinalIgnoreCase) ||
              host == "hf.co" || host.EndsWith(".hf.co", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Model downloads require HTTPS on an approved Hugging Face host.");
    }

    private static void ValidateResponse(HttpResponseMessage response, NativeModelEntry entry, long offset, string? etag)
    {
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentEncoding.Any(value => value != "identity"))
            throw InvalidResponse("Encoded model responses cannot be verified or resumed.");
        if (offset > 0)
        {
            var range = response.Content.Headers.ContentRange;
            if (response.StatusCode != HttpStatusCode.PartialContent || range is null || range.Unit != "bytes" ||
                range.From != offset || range.To != entry.Bytes - 1 || range.Length != entry.Bytes)
                throw InvalidResponse("The server did not honor the exact resume range. Keep the partial file or confirm cleanup.");
            if (etag is not null && response.Headers.ETag?.ToString() != etag)
                throw InvalidResponse("The server validator changed. Confirm partial cleanup before restarting.");
        }
        else if (response.StatusCode != HttpStatusCode.OK)
            throw InvalidResponse("The server returned an unexpected response for a new download.");
        if (response.Content.Headers.ContentLength is { } length && length != entry.Bytes - offset)
            throw InvalidResponse("The server response length does not match the pinned model.");

        HttpRequestException InvalidResponse(string message) => new(message, null, response.StatusCode);
    }
}
