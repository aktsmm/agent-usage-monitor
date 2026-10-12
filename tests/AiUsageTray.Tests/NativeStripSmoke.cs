using System.Diagnostics;
using System.Runtime.InteropServices;
using AiUsageTray.Core;
using AiUsageTray.Desktop;

static class NativeStripSmoke
{
    public static void Run()
    {
        using var process = Process.GetCurrentProcess();
        uint before = GetGuiResources(process.Handle, 0);
        var errors = new List<string>();
        for (int batch = 0; batch < 3; batch++)
        {
        using (var owner = new NativeTray(() => { }, () => { }, () => { }, () => { }, errors.Add,
            () => { }, () => { }, () => false))
        using (var strip = new NativeUsageStrip(owner.Handle, () => { }, (_, _) => { }, _ => { }, errors.Add))
        {
            var now = DateTimeOffset.UtcNow;
            var snapshot = new QuotaSnapshot(new(AccountKey.FromIdentity(Provider.Copilot, "synthetic-native"), "monthly"),
                "Synthetic", UsageSource.CopilotHttp, now, UsageUnit.PercentagePoints, 36, 100, 36, false,
                new(WindowKind.Monthly, now.AddDays(-10), now.AddDays(21)));
            for (int i = 0; i < 40; i++)
            {
                strip.Update(new DesktopSettings { UsageStripEnabled = i % 2 == 0,
                    Language = i % 4 < 2 ? DisplayLanguage.English : DisplayLanguage.Japanese,
                    Theme = i % 4 == 0 ? "Light" : "Dark", UsageStripPosition = new(-9999, -9999) },
                    [snapshot], TimeSpan.FromMinutes(10));
                Pump();
                if (GetForegroundWindow() == strip.Handle)
                    throw new InvalidOperationException("Native strip activated itself");
            }
            strip.Update(new DesktopSettings { UsageStripEnabled = true, Language = DisplayLanguage.English },
                [snapshot], TimeSpan.FromMinutes(10));
            Pump();
            if (!IsWindowVisible(strip.Handle))
                throw new InvalidOperationException("Native strip did not show");
            if (errors.Count > 0)
                throw new InvalidOperationException("Native strip errors: " + string.Join(",", errors));
        }
        Pump();
        uint after = GetGuiResources(process.Handle, 0);
        if (batch == 0)
        {
            // First use initializes USER/GDI font caches; compare a second full create/toggle/dispose cycle.
            before = after;
        }
        else if (after > before + 2)
            throw new InvalidOperationException($"Native strip GDI counts: before={before}, after={after}");
        }
    }

    private static void Pump()
    {
        int count = 0;
        while (count++ < 500 && PeekMessage(out var message, IntPtr.Zero, 0, 0, 1))
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Message
    {
        public IntPtr Window; public uint Id; public UIntPtr WParam; public IntPtr LParam;
        public uint Time; public Point Position; public uint Private;
    }
    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] private static extern bool PeekMessage(out Message message, IntPtr window, uint first, uint last, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern IntPtr DispatchMessage(ref Message message);
}
