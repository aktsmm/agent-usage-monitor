using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiUsageTray.Core;
using Velopack;
using Velopack.Sources;

namespace AiUsageTray.Desktop;

public static class UpdateFlow
{
    public const string Repository = "https://github.com/aktsmm/ai-usage-monitor";
    public const string ReleasePage = Repository + "/releases";
    public static string Version => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
        .InformationalVersion.Split('+')[0];

    public static bool TrustedDownload(Uri uri) =>
        uri.Scheme == "https" && uri.Host == "github.com" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.AbsolutePath.StartsWith("/aktsmm/ai-usage-monitor/releases/download/", StringComparison.Ordinal) &&
        uri.Query.Length == 0 && uri.Fragment.Length == 0 && !uri.AbsolutePath.Contains("%", StringComparison.Ordinal);

    public static async Task<SemanticVersion?> LatestVersionAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/aktsmm/ai-usage-monitor/releases/latest");
        request.Headers.UserAgent.ParseAdd("AiUsageTray/" + Version);
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tag) ||
            tag.ValueKind != JsonValueKind.String)
            throw new UsageException(FailureKind.Schema, "更新情報の形式が変わっています。");
        var candidate = (tag.GetString() ?? "").TrimStart('v');
        if (!SemanticVersion.TryParse(candidate, out var newer) || newer is null)
            throw new UsageException(FailureKind.Schema, "更新のバージョン情報が不正です。");
        return newer;
    }

    public static async Task<string> PortableCheckAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var newer = await LatestVersionAsync(client, cancellationToken);
        if (newer is null)
            return $"現在のバージョン {Version}\n公開済みの安定リリースは確認できませんでした。";
        if (!SemanticVersion.TryParse(Version, out var current) || current is null)
            throw new UsageException(FailureKind.Schema, "アプリのバージョン情報が不正です。");
        return newer > current
            ? $"バージョン {newer} を確認しました。\nポータブル版は自動置換しません。公式ReleasesからインストーラーまたはZIPを取得してください。"
            : $"現在のバージョン {Version}。新しい安定版はありません。\nポータブル版は自動置換しません。";
    }

    public static async Task RunAsync(string[] args)
    {
        bool checkOnly = args.Contains("--update-check-only");
        try
        {
            var manager = new UpdateManager(new TrustedGithubSource());
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(20) };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            if (!manager.IsInstalled || manager.IsPortable)
            {
                string message = await PortableCheckAsync(client, timeout.Token);
                if (checkOnly) { Console.WriteLine(message); return; }
                NativeTray.ShowInformation(message + "\n\n確認先: " + ReleasePage);
                if (NativeTray.Confirm("公式Releasesページを開きますか？"))
                    Process.Start(new ProcessStartInfo(ReleasePage) { UseShellExecute = true })?.Dispose();
                return;
            }
            if (await LatestVersionAsync(client, timeout.Token) is null)
            {
                string message = $"現在のバージョン {Version}\n公開済みの安定リリースは確認できませんでした。\n" + ReleasePage;
                if (checkOnly) Console.WriteLine(message);
                else NativeTray.ShowInformation(message);
                return;
            }
            var update = await manager.CheckForUpdatesAsync().WaitAsync(TimeSpan.FromSeconds(25));
            var target = update?.TargetFullRelease ?? manager.UpdatePendingRestart;
            if (checkOnly)
            {
                Console.WriteLine(target is null ? $"installed:{Version}:no_update" : $"installed:{Version}:available:{target.Version}");
                return;
            }
            if (target is null)
            {
                NativeTray.ShowInformation($"現在のバージョン {Version}。新しい安定版はありません。");
                return;
            }
            if (!NativeTray.Confirm($"バージョン {target.Version} をダウンロードして更新しますか？\n利用状況画面を閉じ、常駐本体を安全に終了してから再起動します。"))
                return;
            using var downloadTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            if (update is not null)
                await manager.DownloadUpdatesAsync(update, cancelToken: downloadTimeout.Token);
            int pipeIndex = Array.IndexOf(args, "--pipe");
            int hostIndex = Array.IndexOf(args, "--host");
            if (pipeIndex < 0 || hostIndex < 0 || pipeIndex + 1 >= args.Length || hostIndex + 1 >= args.Length ||
                !int.TryParse(args[hostIndex + 1], out var pid))
                throw new UsageException(FailureKind.Forbidden, "更新はトレイメニューから実行してください。");
            await PipeProtocol.SendAsync(args[pipeIndex + 1], new("update-exit"), downloadTimeout.Token);
            try
            {
                using var host = Process.GetProcessById(pid);
                await host.WaitForExitAsync(downloadTimeout.Token).WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (ArgumentException) { /* The owned host already finished its shutdown. */ }
            manager.ApplyUpdatesAndRestart(target, ["--background"]);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or UsageException or IOException or
            TimeoutException or OperationCanceledException or Velopack.Exceptions.ChecksumFailedException or
            Velopack.Exceptions.AcquireLockFailedException or Velopack.Exceptions.NotInstalledException)
        {
            var message = $"更新確認・適用に失敗しました（{ex.GetType().Name}）。\nUsage取得や保存済み設定は変更しません。\n" + ReleasePage;
            if (checkOnly) { Console.WriteLine(message); Environment.ExitCode = 1; }
            else NativeTray.ShowError(message);
        }

    }

    private sealed class TrustedGithubSource() : GithubSource(Repository, accessToken: null, prerelease: false)
    {
        protected override string GetAssetUrlFromName(GithubRelease release, string assetName)
        {
            string url = base.GetAssetUrlFromName(release, assetName);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !TrustedDownload(uri))
                throw new UsageException(FailureKind.Forbidden, "公式リポジトリ以外の更新資産は使用しません。");
            return url;
        }
    }
}
