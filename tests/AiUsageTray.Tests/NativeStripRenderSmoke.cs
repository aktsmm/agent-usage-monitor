using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AiUsageTray.Core;
using AiUsageTray.Desktop;

static class NativeStripRenderSmoke
{
    public static void Run(string? captureFolder = null)
    {
        if (captureFolder is not null) Directory.CreateDirectory(captureFolder);
        var errors = new List<string>();
        foreach (var language in new[] { DisplayLanguage.Japanese, DisplayLanguage.English })
        foreach (var theme in new[] { "Dark", "Light" })
        {
            int opened = 0;
            IntPtr tooltip = IntPtr.Zero;
            using (var owner = new NativeTray(() => { }, () => { }, () => { }, () => { }, errors.Add,
                () => { }, () => { }, () => false))
            using (var strip = new NativeUsageStrip(owner.Handle, () => opened++, (_, _) => { }, _ => { }, errors.Add))
            {
                var now = DateTimeOffset.UtcNow;
                var copilot = UsageStripPresentationTests.Sample(Provider.Copilot, 0, now);
                var codex = UsageStripPresentationTests.Sample(Provider.Codex, 84, now) with
                    { ObservedAt = now.AddHours(-1) };
                var settings = new DesktopSettings { CopilotEnabled = true, GithubLogin = "synthetic",
                    GithubPinnedAccount = copilot.Key.Account.Id, CodexEnabled = true,
                    CodexPinnedAccount = codex.Key.Account.Id, CodexEmail = "synthetic@example.test",
                    ClaudeEnabled = true, ClaudeAccountLabel = "synthetic", UsageStripEnabled = true,
                    Theme = theme, Language = language, UsageStripPosition = new(64, 64) };
                strip.Update(settings, [copilot, codex], TimeSpan.FromMinutes(10));
                Check(Caption(strip.Handle) == "Copilot 0%  |  Codex 84%", "Native caption");
                tooltip = Tooltip(strip.Handle);
                Check(tooltip != IntPtr.Zero, "Owned tooltip missing");
                Check(GetWindow(tooltip, 4) == strip.Handle, "Tooltip owner mismatch");
                Check((GetWindowLong(tooltip, -20) & 0x08000000) != 0, "Tooltip can activate itself");
                CheckTooltip(strip.Handle, tooltip, language, "Codex 84%");
                var bitmap = Capture(strip.Handle);
                var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
                byte hatch = theme == "Dark" ? (byte)0x52 : (byte)0xd5;
                byte gray = theme == "Dark" ? (byte)0xcc : (byte)0x55;
                Check(Pixels(bytes, hatch) > 20, "Hatch pixels missing");
                Check(Pixels(bytes, gray) > 5, "Stale gray text missing");
                Check(Pixels(bytes, theme == "Dark" ? (byte)0x33 : (byte)0xf2) > 20, "Hatch background wrong");
                Check(Contrast(gray, hatch) >= 4.5, "Stale text/hatch contrast below 4.5:1");
                Save(bitmap, captureFolder, $"{language}-{theme}-stale.png");

                strip.Update(settings, [], TimeSpan.FromMinutes(10));
                var waiting = new UiLocalizer(language).Text(UiText.UsageStripWaiting);
                Check(Caption(strip.Handle) == waiting, "Empty strip did not retain neutral state");
                CheckTooltip(strip.Handle, tooltip, language, waiting);
                SendMessage(strip.Handle, 0x0203, IntPtr.Zero, IntPtr.Zero);
                Check(opened == 1, "Neutral state cannot open app");
                SendMessage(strip.Handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
                Check(IsWindow(strip.Handle), "WM_CLOSE destroyed strip");
                SendMessage(strip.Handle, 0x0113, new IntPtr(1), IntPtr.Zero);
                Check(GetWindowRect(strip.Handle, out var dpiBounds), "DPI bounds missing");
                uint dpi = GetDpiForWindow(strip.Handle);
                var dpiMemory = Marshal.AllocHGlobal(Marshal.SizeOf<Rect>());
                try
                {
                    Marshal.StructureToPtr(dpiBounds, dpiMemory, false);
                    SendMessage(strip.Handle, 0x02e0, new IntPtr(dpi | (dpi << 16)), dpiMemory);
                }
                finally { Marshal.FreeHGlobal(dpiMemory); }
                SendMessage(strip.Handle, 0x8003, IntPtr.Zero, IntPtr.Zero);
                var neutral = Capture(strip.Handle);
                var neutralBytes = new byte[neutral.PixelWidth * neutral.PixelHeight * 4];
                neutral.CopyPixels(neutralBytes, neutral.PixelWidth * 4, 0);
                Check(Pixels(neutralBytes, theme == "Dark" ? (byte)0x29 : (byte)0xff) > 20,
                    "Neutral theme background wrong");
                Save(neutral, captureFolder, $"{language}-{theme}-waiting.png");
                Check(GetForegroundWindow() != strip.Handle && GetForegroundWindow() != tooltip,
                    "Strip or tooltip stole foreground");
                if (errors.Count > 0) throw new InvalidOperationException(string.Join(",", errors));
            }
            Check(!IsWindow(tooltip), "Owned tooltip leaked");
        }
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }

    private static int Pixels(byte[] pixels, byte gray)
    {
        int count = 0;
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i] == gray && pixels[i + 1] == gray && pixels[i + 2] == gray) count++;
        return count;
    }

    private static double Contrast(byte first, byte second)
    {
        double Luminance(byte channel)
        {
            double value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return (Math.Max(Luminance(first), Luminance(second)) + 0.05) /
            (Math.Min(Luminance(first), Luminance(second)) + 0.05);
    }

    private static void CheckTooltip(IntPtr strip, IntPtr tooltip, DisplayLanguage language, string contains)
    {
        Check(GetClientRect(strip, out var bounds), "Client bounds missing");
        var hit = new HitInfo { Window = strip, Position = new() { X = bounds.Right / 2, Y = bounds.Bottom / 2 },
            Tool = Tool(strip) };
        Check(SendHit(tooltip, 0x0437, IntPtr.Zero, ref hit) != IntPtr.Zero, "Tooltip hit-test failed");
        Check(hit.Tool.Id == new UIntPtr(unchecked((ulong)strip.ToInt64())), "Tooltip hit-test wrong owner");
        var memory = Marshal.AllocHGlobal(8192);
        try
        {
            Marshal.WriteInt16(memory, 0);
            var tool = Tool(strip);
            tool.Text = memory;
            SendTool(tooltip, 0x0438, new IntPtr(4096), ref tool);
            var text = Marshal.PtrToStringUni(memory)!;
            Check(text.Contains(contains), "Tooltip text lost");
            if (contains.StartsWith("Codex", StringComparison.Ordinal))
            {
                var locale = new UiLocalizer(language);
                Check(text.Contains(locale.Text(UiText.Stale)), "Tooltip stale status lost");
                Check(text.Contains("JST"), "Tooltip last-fetch time lost");
            }
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static ToolInfo Tool(IntPtr strip) => new()
    {
        Size = (uint)Marshal.OffsetOf<ToolInfo>(nameof(ToolInfo.Reserved)).ToInt32(), Window = strip,
        Id = new UIntPtr(unchecked((ulong)strip.ToInt64()))
    };

    private static IntPtr Tooltip(IntPtr owner)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            var name = new StringBuilder(256);
            GetClassName(window, name, name.Capacity);
            if (GetWindow(window, 4) == owner && name.ToString() == "tooltips_class32") found = window;
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string Caption(IntPtr window)
    {
        var text = new StringBuilder(512);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    private static BitmapSource Capture(IntPtr window)
    {
        Check(GetClientRect(window, out var rect), "Capture bounds missing");
        var screen = GetDC(window);
        if (screen == IntPtr.Zero) throw new InvalidOperationException("Capture DC missing");
        IntPtr dc = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            dc = CreateCompatibleDC(screen);
            bitmap = CreateCompatibleBitmap(screen, rect.Right, rect.Bottom);
            if (dc == IntPtr.Zero || bitmap == IntPtr.Zero)
                throw new InvalidOperationException("Capture allocation failed");
            previous = SelectObject(dc, bitmap);
            // Render only this synthetic HWND into memory; never capture the user's desktop.
            SendMessage(window, 0x0318, dc, IntPtr.Zero);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            return converted;
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(dc, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (dc != IntPtr.Zero) DeleteDC(dc);
            ReleaseDC(window, screen);
        }
    }

    private static void Save(BitmapSource image, string? folder, string name)
    {
        if (folder is null) return;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new FileStream(Path.Combine(folder, name), FileMode.CreateNew);
        encoder.Save(output);
    }

    private delegate bool Visitor(IntPtr window, IntPtr state);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct ToolInfo
    {
        public uint Size, Flags; public IntPtr Window; public UIntPtr Id; public Rect Bounds;
        public IntPtr Instance, Text, Parameter, Reserved;
    }
    [StructLayout(LayoutKind.Sequential)] private struct HitInfo { public IntPtr Window; public Point Position; public ToolInfo Tool; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(Visitor visitor, IntPtr state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint relation);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendHit(IntPtr window, uint message, IntPtr wParam, ref HitInfo hit);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendTool(IntPtr window, uint message, IntPtr wParam, ref ToolInfo tool);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
