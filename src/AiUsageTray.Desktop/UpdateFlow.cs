using System;
using System.Diagnostics;
using System.Globalization;
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
    public const string ReleaseDownloadBase = Repository + "/releases/latest/download";
    private const string LatestManifestUrl = ReleaseDownloadBase + "/releases.win.json";
    private const string PackageId = "AiUsageTrayDesktop";
    public static string Version => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
        .InformationalVersion.Split('+')[0];

    public static bool TrustedDownload(Uri uri) =>
        uri.Scheme == "https" && uri.Host == "github.com" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        (uri.AbsolutePath.StartsWith("/aktsmm/agent-usage-monitor/releases/download/", StringComparison.Ordinal) ||
         uri.AbsolutePath.StartsWith("/aktsmm/agent-usage-monitor/releases/latest/download/", StringComparison.Ordinal)) &&
        TrustedReleaseQuery(uri) && uri.Fragment.Length == 0 && !uri.AbsolutePath.Contains("%", StringComparison.Ordinal);

    public static bool TrustedReleaseRedirect(Uri uri) =>
        uri.Scheme == "https" && uri.Host == "github.com" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.AbsolutePath.StartsWith("/aktsmm/agent-usage-monitor/releases/download/", StringComparison.Ordinal) &&
        TrustedReleaseQuery(uri) && uri.Fragment.Length == 0 && !uri.AbsolutePath.Contains("%", StringComparison.Ordinal);

    private static bool TrustedReleaseQuery(Uri uri) => uri.Query.Length == 0 ||
        uri.AbsolutePath.EndsWith("/RELEASES", StringComparison.Ordinal) &&
        uri.Query == "?packageId=AiUsageTrayDesktop";

    public static bool IsRateLimited(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.TooManyRequests ||
        response.StatusCode == HttpStatusCode.Forbidden &&
        response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) &&
        remaining.Contains("0", StringComparer.Ordinal);

    public static TimeSpan? RateLimitRetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter?.Delta;
        if (retry is null && response.Headers.RetryAfter?.Date is { } retryDate)
            retry = retryDate - DateTimeOffset.UtcNow;
        if (retry is null && response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues) &&
            long.TryParse(resetValues.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var resetSeconds) &&
            resetSeconds >= 0 && resetSeconds <= 253_402_300_799)
        {
            retry = DateTimeOffset.FromUnixTimeSeconds(resetSeconds) - DateTimeOffset.UtcNow;
        }
        return retry is { } delay && delay > TimeSpan.Zero ? delay : null;
    }

    public static async Task<SemanticVersion?> LatestVersionAsync(HttpClient client, CancellationToken cancellationToken) =>
        (await LatestAssetAsync(client, cancellationToken))?.Version;

    private static async Task<VelopackAsset?> LatestAssetAsync(HttpClient client, CancellationToken cancellationToken)
    {
        byte[] content;
        try
        {
            content = await UpdateDownloader.DownloadBytesAsync(client, LatestManifestUrl, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array)
            throw new UsageException(FailureKind.Schema, "update_release_manifest_invalid");

        VelopackAsset? fullPackage = null;
        foreach (var item in assets.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("PackageId", out var packageId) || packageId.ValueKind != JsonValueKind.String ||
                packageId.GetString() != PackageId ||
                !item.TryGetProperty("Type", out var type) || type.ValueKind != JsonValueKind.String ||
                type.GetString() != "Full")
                continue;
            if (fullPackage is not null)
                throw new UsageException(FailureKind.Schema, "update_release_manifest_ambiguous");

            if (!item.TryGetProperty("Version", out var versionElement) || versionElement.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("FileName", out var fileName) || fileName.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("SHA1", out var sha1) || sha1.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("SHA256", out var sha256) || sha256.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("Size", out var size) || !size.TryGetInt64(out var sizeBytes))
                throw new UsageException(FailureKind.Schema, "update_release_manifest_invalid");

            var versionText = versionElement.GetString() ?? "";
            if (!Regex.IsMatch(versionText, @"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant) ||
                !SemanticVersion.TryParse(versionText, out var version) || version is null)
                throw new UsageException(FailureKind.Schema, "update_stable_version_invalid");

            fullPackage = new VelopackAsset
            {
                PackageId = PackageId,
                Version = version,
                Type = VelopackAssetType.Full,
                FileName = fileName.GetString() ?? "",
                SHA1 = sha1.GetString(),
                SHA256 = sha256.GetString(),
                Size = sizeBytes
            };
            ValidateManifestAssetMetadata(fullPackage, version);
        }
        return fullPackage ?? throw new UsageException(FailureKind.Schema, "update_release_feed_missing");
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
            var manager = new UpdateManager(new SimpleWebSource(ReleaseDownloadBase, new UpdateDownloader()));
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
            var latestAsset = await LatestAssetAsync(client, timeout.Token);
            var latest = latestAsset?.Version;
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
            if (target is not null) ValidateAsset(target, latestAsset!, current);
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
            await VerifyPackageAsync(latestAsset!, downloadTimeout.Token);
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
        ValidateAssetMetadata(asset, latest);
        if (asset.Version <= current)
            throw new UsageException(FailureKind.Forbidden, "update_asset_manifest_mismatch");
    }

    public static void ValidateAsset(VelopackAsset asset, VelopackAsset manifestAsset, SemanticVersion current)
    {
        ValidateAsset(asset, manifestAsset.Version, current);
        ValidateManifestAssetMetadata(manifestAsset, manifestAsset.Version);
        if (asset.Size != manifestAsset.Size ||
            !string.Equals(asset.SHA1, manifestAsset.SHA1, StringComparison.OrdinalIgnoreCase))
            throw new UsageException(FailureKind.Forbidden, "update_asset_manifest_mismatch");
    }

    private static void ValidateAssetMetadata(VelopackAsset asset, SemanticVersion version)
    {
        if (asset.Version != version ||
            asset.PackageId != PackageId ||
            asset.Type != VelopackAssetType.Full ||
            asset.FileName != $"AiUsageTrayDesktop-{version}-full.nupkg" ||
            asset.Size is <= 0 or > 314_572_800 || asset.SHA1 is null ||
            !Regex.IsMatch(asset.SHA1, "^[a-fA-F0-9]{40}$", RegexOptions.CultureInvariant))
            throw new UsageException(FailureKind.Forbidden, "update_asset_manifest_mismatch");
    }

    private static void ValidateManifestAssetMetadata(VelopackAsset asset, SemanticVersion version)
    {
        ValidateAssetMetadata(asset, version);
        if (asset.SHA256 is null ||
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

}
