using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AiUsageTray.Core;
using Velopack.Sources;

namespace AiUsageTray.Desktop;

public sealed class UpdateDownloader : IFileDownloader
{
    public async Task<string> DownloadString(string url, IDictionary<string, string>? headers = null,
        double timeout = 30) => Encoding.UTF8.GetString(await DownloadBytes(url, headers, timeout));

    public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null,
        double timeout = 30)
    {
        using var memory = new MemoryStream();
        await DownloadAsync(url, memory, headers, TimeSpan.FromSeconds(25), 1_048_576, default);
        return memory.ToArray();
    }

    public static async Task<byte[]> DownloadBytesAsync(HttpClient client, string url,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !TrustedSource(uri))
            throw new UsageException(FailureKind.Forbidden, "update_source_not_allowed");
        using var memory = new MemoryStream();
        await DownloadAsync(client, uri, memory, null, 1_048_576, cancellationToken);
        return memory.ToArray();
    }

    public async Task DownloadFile(string url, string targetFile, Action<int>? progress = null,
        IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancellationToken = default)
    {
        await using var file = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None);
        await DownloadAsync(url, file, headers, TimeSpan.FromMinutes(10), 314_572_800, cancellationToken);
        progress?.Invoke(100);
    }

    public static bool TrustedSource(Uri uri, bool redirect = false)
    {
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0) return false;
        if (redirect)
            return UpdateFlow.TrustedReleaseRedirect(uri) ||
                uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com";
        return UpdateFlow.TrustedDownload(uri);
    }

    private static async Task DownloadAsync(string url, Stream output, IDictionary<string, string>? headers,
        TimeSpan timeout, long maximumBytes, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !TrustedSource(uri))
            throw new UsageException(FailureKind.Forbidden, "update_source_not_allowed");
        if (headers is not null && System.Linq.Enumerable.Any(headers.Keys,
            key => key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)))
            throw new UsageException(FailureKind.Forbidden, "update_credential_not_allowed");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = timeout };
        await DownloadAsync(client, uri, output, headers, maximumBytes, deadline.Token);
    }

    private static async Task DownloadAsync(HttpClient client, Uri uri, Stream output,
        IDictionary<string, string>? headers, long maximumBytes, CancellationToken cancellationToken)
    {
        for (int redirects = 0; redirects <= 3; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("AiUsageTray/" + UpdateFlow.Version);
            if (headers is not null && redirects == 0)
                foreach (var entry in headers)
                    if (entry.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase))
                        request.Headers.TryAddWithoutValidation(entry.Key, entry.Value);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                if (location is null || !Uri.TryCreate(uri, location, out var redirectUri) ||
                    !TrustedSource(redirectUri, redirect: true))
                    throw new UsageException(FailureKind.Forbidden, "update_redirect_not_allowed");
                uri = redirectUri;
                continue;
            }
            if (UpdateFlow.IsRateLimited(response))
                throw new UsageException(FailureKind.RateLimited, "update_rate_limited",
                    UpdateFlow.RateLimitRetryAfter(response));
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new HttpRequestException("Update resource was not found", null, response.StatusCode);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maximumBytes)
                throw new UsageException(FailureKind.Schema, "update_content_too_large");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[81920];
            long bytes = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                bytes += count;
                if (bytes > maximumBytes) throw new UsageException(FailureKind.Schema, "update_content_too_large");
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            return;
        }
        throw new UsageException(FailureKind.Forbidden, "update_redirect_limit");
    }
}
