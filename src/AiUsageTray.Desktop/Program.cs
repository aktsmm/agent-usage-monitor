using System;
using System.Linq;
using System.Text;
using AiUsageTray.Core;
using Velopack;

namespace AiUsageTray.Desktop;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(_ => StartupRegistration.BeforeUninstall()).Run();
        if (args.Contains("--validate-resources"))
        {
            Console.OutputEncoding = Encoding.UTF8;
            foreach (var language in new[] { DisplayLanguage.Japanese, DisplayLanguage.English })
            {
                var locale = new UiLocalizer(language);
                foreach (var key in Enum.GetValues<UiText>()) _ = locale.Text(key);
                Console.WriteLine($"resources:{locale.LanguageTag}:{Enum.GetValues<UiText>().Length}:ok");
            }
            return;
        }
        if (args.Contains("--update-check") || args.Contains("--update-check-only") || args.Contains("--update-auto"))
        {
            Console.OutputEncoding = Encoding.UTF8;
            UpdateFlow.RunAsync(args).GetAwaiter().GetResult();
            return;
        }
        if (args.Contains("--panel") || args.Contains("--demo"))
        {
            RunPanel();
            return;
        }
        new TrayHost(args).Run();
    }

    private static void RunPanel()
    {
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
