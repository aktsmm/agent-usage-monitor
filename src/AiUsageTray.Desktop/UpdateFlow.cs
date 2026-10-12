using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiUsageTray.Core;
using Velopack;
using Velopack.Sources;
using Velopack.Locators;

namespace AiUsageTray.Desktop;

public static class UpdateFlow
{
    public const string Repository = "https://github.com/aktsmm/agent-usage-monitor";
    public const string ReleasePage = Repository + "/releases";
    public static string Version => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
        .InformationalVersion.Split('+')[0];

    public static bool TrustedDownload(Uri uri) =>
        uri.Scheme == "https" && uri.Host == "github.com" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.AbsolutePath.StartsWith("/aktsmm/agent-usage-monitor/releases/download/", StringComparison.Ordinal) &&
        uri.Query.Length == 0 && uri.Fragment.Length == 0 && !uri.AbsolutePath.Contains("%", StringComparison.Ordinal);

    public static async Task<SemanticVersion?> LatestVersionAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/aktsmm/agent-usage-monitor/releases/latest");
        request.Headers.UserAgent.ParseAdd("AiUsageTray/" + Version);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new UsageException(FailureKind.RateLimited, "update_rate_limited",
                response.Headers.RetryAfter?.Delta ?? response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (memory.Length + count > 1_048_576)
                throw new UsageException(FailureKind.Schema, "update_metadata_too_large");
            memory.Write(buffer, 0, count);
        }
        using var document = JsonDocument.Parse(memory.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tag) ||
            tag.ValueKind != JsonValueKind.String)
            throw new UsageException(FailureKind.Schema, "update_response_schema_invalid");
        if (!root.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
            !root.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False)
            throw new UsageException(FailureKind.Schema, "update_unstable_release_rejected");
        var candidate = tag.GetString() ?? "";
        if (!Regex.IsMatch(candidate, @"^v?\d+\.\d+\.\d+$", RegexOptions.CultureInvariant))
            throw new UsageException(FailureKind.Schema, "update_stable_version_invalid");
        candidate = candidate.TrimStart('v');
        if (!SemanticVersion.TryParse(candidate, out var newer) || newer is null)
            throw new UsageException(FailureKind.Schema, "update_version_invalid");
        return newer;
    }

    public static async Task<string> PortableCheckAsync(HttpClient client, CancellationToken cancellationToken,
        UiLocalizer? localizer = null)
    {
        var locale = localizer ?? new();
        var newer = await LatestVersionAsync(client, cancellationToken);
        if (newer is null)
            return Version + "\n" + locale.Text(UiText.UpdateUnavailable);
        if (!SemanticVersion.TryParse(Version, out var current) || current is null)
            throw new UsageException(FailureKind.Schema, "installed_version_invalid");
        return newer > current
            ? locale.Format(UiText.UpdateAvailable, newer) + "\n" + locale.Text(UiText.PortableUpdate)
            : locale.Format(UiText.UpdateCurrent, Version) + "\n" + locale.Text(UiText.PortableUpdate);
    }

    public static async Task RunAsync(string[] args)
    {
        bool checkOnly = args.Contains("--update-check-only");
        bool automatic = args.Contains("--update-auto");
        int languageIndex = Array.IndexOf(args, "--ui-language");
        var locale = new UiLocalizer(languageIndex >= 0 && languageIndex + 1 < args.Length
            ? args[languageIndex + 1] switch
            {
                "ja" => DisplayLanguage.Japanese, "en" => DisplayLanguage.English,
                _ => throw new UsageException(FailureKind.Schema, "helper_language_invalid")
            } : DisplayLanguage.System);
        int pipeIndex = Array.IndexOf(args, "--pipe");
        int hostIndex = Array.IndexOf(args, "--host");
        string? pipe = pipeIndex >= 0 && pipeIndex + 1 < args.Length ? args[pipeIndex + 1] : null;
        async Task ReportAsync(UpdateStage stage, string message, string? target = null, TimeSpan? retry = null)
        {
            if (pipe is null) return;
            var view = await PipeProtocol.SendAsync(pipe, new("update-status", Update:
                new(stage, message, target, DateTimeOffset.UtcNow,
                    retry is { } delay && delay > TimeSpan.Zero ? DateTimeOffset.UtcNow + delay : null,
                    stage == UpdateStage.Failed ? message : null)), default);
            locale = new UiLocalizer(view.Settings.Language);
        }
        try
        {
            await ReportAsync(UpdateStage.Checking, "update_checking");
            var manager = new UpdateManager(new TrustedGithubSource());
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(20) };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            if (!manager.IsInstalled || manager.IsPortable)
            {
                string message = await PortableCheckAsync(client, timeout.Token, locale);
                await ReportAsync(UpdateStage.Portable, message);
                if (automatic) return;
                if (checkOnly) { Console.WriteLine(message); return; }
                NativeTray.ShowInformation(message + "\n\n" + ReleasePage, locale);
                if (NativeTray.Confirm(locale.Text(UiText.OpenReleasePageConfirm) + "\n" + ReleasePage, locale))
                    Process.Start(new ProcessStartInfo(ReleasePage) { UseShellExecute = true })?.Dispose();
                return;
            }
            var latest = await LatestVersionAsync(client, timeout.Token);
            if (latest is null)
            {
                string message = Version + "\n" + locale.Text(UiText.UpdateUnavailable) + "\n" + ReleasePage;
                if (checkOnly) Console.WriteLine(message);
                await ReportAsync(UpdateStage.NoStableRelease, "update_no_stable_release");
                if (!checkOnly && !automatic) NativeTray.ShowInformation(message, locale);
                return;
            }
            if (!SemanticVersion.TryParse(Version, out var current) || current is null)
                throw new UsageException(FailureKind.Schema, "installed_version_invalid");
            if (latest <= current)
            {
                await ReportAsync(UpdateStage.Current, "update_current");
                if (!automatic && !checkOnly) NativeTray.ShowInformation(locale.Format(UiText.UpdateCurrent, Version), locale);
                if (checkOnly) Console.WriteLine($"installed:{Version}:no_update");
                return;
            }
            var update = await manager.CheckForUpdatesAsync().WaitAsync(TimeSpan.FromSeconds(25));
            var target = update?.TargetFullRelease ?? manager.UpdatePendingRestart;
            if (target is not null) ValidateAsset(target, latest, current);
            if (target is null)
                throw new UsageException(FailureKind.Schema, "update_release_feed_missing");
            if (checkOnly)
            {
                Console.WriteLine($"installed:{Version}:available:{target.Version}");
                return;
            }
            if (!automatic && !NativeTray.Confirm(locale.Format(UiText.UpdateDownloadConfirm, target.Version), locale))
                return;
            using var downloadTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await ReportAsync(UpdateStage.Downloading, "update_downloading", target.Version.ToString());
            if (update is not null)
                await manager.DownloadUpdatesAsync(update, cancelToken: downloadTimeout.Token);
            await VerifyPackageAsync(target, downloadTimeout.Token);
            if (pipe is null || hostIndex < 0 || hostIndex + 1 >= args.Length ||
                !int.TryParse(args[hostIndex + 1], out var pid))
                throw new UsageException(FailureKind.Forbidden, "update_tray_owner_required");
            await ReportAsync(UpdateStage.Ready, "update_ready", target.Version.ToString());
            var view = await PipeProtocol.SendAsync(pipe, new("update-permission"), downloadTimeout.Token);
            if (view.Update?.Stage == UpdateStage.Deferred) return;
            using var host = Process.GetProcessById(pid);
            _ = host.Handle;
            await PipeProtocol.SendAsync(pipe, new("update-exit"), downloadTimeout.Token);
            await host.WaitForExitAsync(downloadTimeout.Token).WaitAsync(TimeSpan.FromSeconds(30));
            if (host.ExitCode != 0)
                throw new UsageException(FailureKind.Storage, "update_host_shutdown_failed");
            manager.ApplyUpdatesAndRestart(target, ["--background"]);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or UsageException or IOException or ArgumentException or
            TimeoutException or OperationCanceledException or Velopack.Exceptions.ChecksumFailedException or
            Velopack.Exceptions.AcquireLockFailedException or Velopack.Exceptions.NotInstalledException)
        {
            var message = locale.Format(UiText.UpdateFailed, ex.GetType().Name) + "\n" + ReleasePage;
            try
            {
                await ReportAsync(UpdateStage.Failed, ex is UsageException failure ? failure.SafeCode : ex.GetType().Name,
                    retry: ex is UsageException usage ? usage.RetryAfter : null);
            }
            catch (Exception reportError) when (reportError is IOException or OperationCanceledException or UsageException)
            {
                if (!automatic) NativeTray.ShowError(locale.Text(UiText.CommunicationFailed), locale);
            }
            if (checkOnly) { Console.WriteLine(message); Environment.ExitCode = 1; }
            else if (!automatic) NativeTray.ShowError(message, locale);
        }

    }

    public static void ValidateAsset(VelopackAsset asset, SemanticVersion latest, SemanticVersion current)
    {
        if (asset.Version != latest || asset.Version <= current ||
            asset.PackageId != "AiUsageTrayDesktop" ||
            asset.FileName != $"AiUsageTrayDesktop-{latest}-full.nupkg" ||
            asset.Size is <= 0 or > 314_572_800 || asset.SHA256 is null ||
            !Regex.IsMatch(asset.SHA256, "^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant))
            throw new UsageException(FailureKind.Forbidden, "update_asset_manifest_mismatch");
    }

    private static async Task VerifyPackageAsync(VelopackAsset asset, CancellationToken cancellationToken)
    {
        var directory = VelopackLocator.Current.PackagesDir ??
            throw new UsageException(FailureKind.Storage, "update_package_directory_missing");
        await using var file = File.OpenRead(Path.Combine(directory, asset.FileName));
        if (file.Length != asset.Size ||
            !string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)),
                asset.SHA256, StringComparison.OrdinalIgnoreCase))
            throw new UsageException(FailureKind.Forbidden, "update_sha256_mismatch");
    }

    private sealed class TrustedGithubSource() : GithubSource(Repository, accessToken: null, prerelease: false,
        downloader: new UpdateDownloader())
    {
        protected override string GetAssetUrlFromName(GithubRelease release, string assetName)
        {
            string url = base.GetAssetUrlFromName(release, assetName);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !TrustedDownload(uri))
                throw new UsageException(FailureKind.Forbidden, "update_asset_source_rejected");
            return url;
        }
    }
}
