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
        TrayHost? host = null;
        void Unhandled(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception exception)
            {
                host?.RecordFatal(exception);
                host?.ShowFatal(exception);
            }
        }
        AppDomain.CurrentDomain.UnhandledException += Unhandled;
        try
        {
            host = new TrayHost(args);
            host.Run();
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            host?.RecordFatal(ex);
            if (host is not null) host.ShowFatal(ex);
            else NativeTray.ShowError(new UiLocalizer().Format(UiText.HostStopped, ex.GetType().Name));
        }
        finally
        {
            host?.ReleaseLease();
            AppDomain.CurrentDomain.UnhandledException -= Unhandled;
        }
    }

    private static void RunPanel()
    {
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
