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
        if (redirect) return uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com";
        return UpdateFlow.TrustedDownload(uri) || uri.Host == "api.github.com" &&
            (uri.AbsolutePath == "/repos/aktsmm/agent-usage-monitor/releases" ||
             uri.AbsolutePath.StartsWith("/repos/aktsmm/agent-usage-monitor/releases/", StringComparison.Ordinal));
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
        for (int redirects = 0; redirects <= 3; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("AiUsageTray/" + UpdateFlow.Version);
            if (headers is not null && redirects == 0)
                foreach (var entry in headers)
                    if (entry.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase))
                        request.Headers.TryAddWithoutValidation(entry.Key, entry.Value);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                if (location is null || !location.IsAbsoluteUri || !TrustedSource(location, redirect: true))
                    throw new UsageException(FailureKind.Forbidden, "update_redirect_not_allowed");
                uri = location;
                continue;
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new UsageException(FailureKind.RateLimited, "update_rate_limited",
                    response.Headers.RetryAfter?.Delta ?? response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maximumBytes)
                throw new UsageException(FailureKind.Schema, "update_content_too_large");
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
            var buffer = new byte[81920];
            long bytes = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, deadline.Token)) > 0)
            {
                bytes += count;
                if (bytes > maximumBytes) throw new UsageException(FailureKind.Schema, "update_content_too_large");
                await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
            }
            return;
        }
        throw new UsageException(FailureKind.Forbidden, "update_redirect_limit");
    }
}
