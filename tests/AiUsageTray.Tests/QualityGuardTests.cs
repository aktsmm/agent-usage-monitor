using System.Text.RegularExpressions;
using System.Xml.Linq;
using AiUsageTray.Core;

/// <summary>Guards that the UI stays readable and that the release workflow keeps its safety rules.</summary>
static class QualityGuardTests
{
    public static void Register(Action<string, Action> test)
    {
        void Check(bool condition, string what) { if (!condition) throw new InvalidOperationException("Quality guard: " + what); }

        string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AiUsageTray.slnx")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
        }

        test("theme colors meet WCAG AA contrast in light and dark", () =>
        {
            foreach (var dark in new[] { false, true })
            {
                foreach (var text in new[] { "TextBrush", "MutedBrush", "AccentBrush", "WarningBrush" })
                    foreach (var background in new[] { "BackgroundBrush", "SoftBrush" })
                    {
                        var ratio = ThemePalette.Contrast(ThemePalette.Color(text, dark), ThemePalette.Color(background, dark));
                        Check(ratio >= 4.5, $"{(dark ? "dark" : "light")} {text} on {background} is {ratio:0.00}:1");
                    }
            }
            Check(Math.Abs(ThemePalette.Contrast("#000000", "#FFFFFF") - 21) < 0.01, "contrast formula sanity");
        });

        test("release workflow keeps least privilege, the approval gate and no untrusted inline input", () =>
        {
            var path = Path.Combine(RepositoryRoot(), ".github", "workflows", "windows-packages.yml");
            var text = File.ReadAllText(path);
            Check(Regex.IsMatch(text, @"(?m)^permissions:\s*\r?\n\s+contents:\s*read"), "top-level permissions must be contents: read");
            Check(!text.Contains("write-all", StringComparison.Ordinal), "write-all is not allowed");
            Check(!text.Contains("pull_request_target", StringComparison.Ordinal), "pull_request_target is not allowed");
            Check(text.Contains("environment: release-approval", StringComparison.Ordinal), "the draft job must stay behind release-approval");
            Check(Regex.IsMatch(text, @"gh release create[^\r\n]*--draft"), "releases must be created as drafts only");
            Check(!Regex.IsMatch(text, @"gh release (edit|create)[^\r\n]*--draft=false"), "the workflow must never publish");
            foreach (Match use in Regex.Matches(text, @"uses:\s*([^\s@]+)@(\S+)"))
                Check(Regex.IsMatch(use.Groups[2].Value, "^[0-9a-f]{40}$"), $"action {use.Groups[1].Value} must be pinned to a commit SHA");
            foreach (Match run in Regex.Matches(text, @"run:\s*\|?[\s\S]*?(?=\r?\n\s{6}- |\r?\n\s{2}\w|\z)"))
                Check(!Regex.IsMatch(run.Value, @"\$\{\{\s*(github\.event\.(pull_request|head_commit|issue|comment)|github\.head_ref|inputs\.)"),
                    "untrusted input must reach scripts through env, not inline");
        });

        test("release version, readme and packaged release notes agree", () =>
        {
            var root = RepositoryRoot();
            var props = XDocument.Load(Path.Combine(root, "Directory.Build.props"));
            var version = props.Descendants("VersionPrefix").Single().Value;
            var file = $"release-notes-{version}.md";
            var notesPath = Path.Combine(root, "docs", file);
            Check(File.Exists(notesPath), "release notes for the package version are missing");
            Check(File.ReadLines(notesPath).First() == $"# Agent Usage Monitor {version}",
                "release notes title does not match the package version");
            var readme = File.ReadAllText(Path.Combine(root, "README.md"));
            Check(readme.Contains($"docs/{file}", StringComparison.Ordinal),
                "readme does not link to the current release notes");
        });

        test("overview error wraps long localized messages", () =>
        {
            var xaml = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "AiUsageTray.Desktop", "MainWindow.xaml"));
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var error = xaml.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "Error");
            Check((string?)error.Attribute("TextWrapping") == "Wrap", "overview error must wrap");
        });
    }
}
