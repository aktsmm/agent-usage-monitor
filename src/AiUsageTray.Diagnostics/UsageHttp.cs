using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AiUsageTray.Core;

namespace AiUsageTray.Diagnostics;

public sealed class UsageHttp : IDisposable
{
    private readonly HttpClient client;

    public UsageHttp(HttpMessageHandler? handler = null)
    {
        client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false });
        client.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<JsonDocument> GetAsync(Uri uri, string scheme, string token,
        CancellationToken cancellationToken, string? accountId = null)
    {
        if (uri.Scheme != "https" ||
            (uri.Host != "api.github.com" && uri.Host != "chatgpt.com"))
            throw new UsageException(FailureKind.Forbidden, "endpoint_not_allowed");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue(scheme, token);
        request.Headers.UserAgent.ParseAdd("AiUsageTray/0.1");
        request.Headers.Accept.ParseAdd("application/json");
        if (accountId is not null)
            request.Headers.Add("ChatGPT-Account-Id", accountId);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var kind = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => FailureKind.Unauthorized,
                HttpStatusCode.Forbidden => FailureKind.Forbidden,
                HttpStatusCode.TooManyRequests => FailureKind.RateLimited,
                >= HttpStatusCode.InternalServerError => FailureKind.Server,
                _ => FailureKind.Schema
            };
            var retry = response.Headers.RetryAfter;
            var delay = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow);
            throw new UsageException(kind, $"http_{(int)response.StatusCode}",
                delay > TimeSpan.Zero ? delay : null);
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (memory.Length + count > 1_048_576)
                throw new UsageException(FailureKind.Schema, "http_body_too_large");
            memory.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(memory.ToArray());
    }

    public void Dispose() => client.Dispose();
}
