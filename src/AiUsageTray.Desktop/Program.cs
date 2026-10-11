using System;
using System.Linq;
using System.Text;
using Velopack;

namespace AiUsageTray.Desktop;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(_ => StartupRegistration.BeforeUninstall()).Run();
        if (args.Contains("--update-check") || args.Contains("--update-check-only"))
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
