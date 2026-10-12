using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using AiUsageTray.Core;

namespace AiUsageTray.Desktop;

public sealed class NativeUsageStrip : IDisposable
{
    private readonly WindowProcedure procedure;
    private readonly string className = "AgentUsageStrip-" + Guid.NewGuid().ToString("N");
    private readonly Action open;
    private readonly Action<int, int> menu;
    private readonly Action<UsageStripPosition> moved;
    private readonly Action<string> report;
    private readonly IntPtr handle;
    private IntPtr font;
    private uint fontDpi;
    private QuotaSnapshot[] snapshots = [];
    private HashSet<Provider> failed = [];
    private UiLocalizer localizer = new();
    private TimeSpan maximumAge = TimeSpan.FromMinutes(10);
    private UsageStripPosition? requested;
    private bool enabled;
    private bool dark = true;
    private bool positioning;
    private bool disposed;
    private string text = "";
    public IntPtr Handle => handle;

    public NativeUsageStrip(IntPtr owner, Action open, Action<int, int> menu,
        Action<UsageStripPosition> moved, Action<string> report)
    {
        this.open = open; this.menu = menu; this.moved = moved; this.report = report;
        procedure = WndProc;
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(), Style = 8,
            Procedure = Marshal.GetFunctionPointerForDelegate(procedure),
            Instance = GetModuleHandle(null), ClassName = className
        };
        if (RegisterClassEx(ref windowClass) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        handle = CreateWindowEx(0x08000088, className, "Agent Usage Monitor", 0x80000000,
            0, 0, 320, 32, owner, IntPtr.Zero, windowClass.Instance, IntPtr.Zero);
        if (handle == IntPtr.Zero)
        {
            UnregisterClass(className, windowClass.Instance);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        if (SetTimer(handle, new UIntPtr(1), 60_000, IntPtr.Zero) == UIntPtr.Zero)
        {
            DestroyWindow(handle); UnregisterClass(className, windowClass.Instance);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Update(DesktopSettings settings, IEnumerable<QuotaSnapshot> verified, TimeSpan age,
        IEnumerable<Provider>? failedProviders = null)
    {
        enabled = settings.UsageStripEnabled;
        requested = settings.UsageStripPosition;
        dark = settings.Theme == "Dark";
        localizer = new(settings.Language);
        snapshots = verified.ToArray();
        failed = failedProviders?.ToHashSet() ?? [];
        maximumAge = age;
        Refresh();
    }

    private void Refresh()
    {
        if (disposed) return;
        text = string.Join("  |  ", UsageStrip.Values(snapshots, DateTimeOffset.UtcNow, maximumAge)
            .Select(v => $"{v.Provider} {(v.Percentage is { } value ? value.ToString("0.#", localizer.Culture) + "%" : "—")}"
                + (v.Stale || failed.Contains(v.Provider) && v.Percentage is not null
                    ? " · " + localizer.Text(UiText.Stale) : v.Ambiguous ? " · ?" : "")));
        SetWindowText(handle, text + " · " + localizer.Text(UiText.UsageStripNotes));
        if (!enabled) { ShowWindow(handle, 0); return; }
        Place();
        InvalidateRect(handle, IntPtr.Zero, false);
    }

    private void UpdateFont()
    {
        uint dpi = GetDpiForWindow(handle);
        if (dpi == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "usage_strip_dpi_failed");
        if (font != IntPtr.Zero && dpi == fontDpi) return;
        var next = CreateFont(-(int)Math.Round(11 * dpi / 72d), 0, 0, 0, 400, 0, 0, 0,
            1, 0, 0, 5, 0, "Segoe UI");
        if (next == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "usage_strip_font_failed");
        if (font != IntPtr.Zero) DeleteObject(font);
        font = next; fontDpi = dpi;
    }

    private void Place()
    {
        if (positioning) return;
        positioning = true;
        try
        {
            UpdateFont();
            var point = requested is { } position ? new Point { X = position.X, Y = position.Y } : new Point();
            var monitor = MonitorFromPoint(point, requested is null ? 1u : 2u);
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var safe = info.Work;
            int margin = Math.Max(8, (int)(8 * fontDpi / 96));
            // Auto-hidden taskbars do not reduce WorkArea; reserve space inside every monitor edge.
            if (safe.Left == info.Monitor.Left && safe.Top == info.Monitor.Top &&
                safe.Right == info.Monitor.Right && safe.Bottom == info.Monitor.Bottom)
            {
                int reserve = (int)(48 * fontDpi / 96);
                safe.Left += reserve; safe.Top += reserve; safe.Right -= reserve; safe.Bottom -= reserve;
            }
            var dc = GetDC(handle);
            if (dc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            Size size;
            try
            {
                var previous = SelectObject(dc, font);
                try
                {
                    if (!GetTextExtentPoint32(dc, text, text.Length, out size))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                finally { SelectObject(dc, previous); }
            }
            finally { ReleaseDC(handle, dc); }
            int width = Math.Min(size.Width + margin * 3, safe.Right - safe.Left);
            int height = Math.Min(size.Height + margin * 2, safe.Bottom - safe.Top);
            var start = requested ?? new UsageStripPosition(safe.Right - width - margin,
                info.Work.Top > info.Monitor.Top ? safe.Top + margin : safe.Bottom - height - margin);
            var clamped = UsageStrip.Clamp(start, new(safe.Left, safe.Top, safe.Right, safe.Bottom), width, height);
            if (!SetWindowPos(handle, new IntPtr(-1), clamped.X, clamped.Y, width, height, 0x0010 | 0x0040))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { positioning = false; }
    }

    private IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (message)
            {
                case 0x000F:
                    Paint(window);
                    return IntPtr.Zero;
                case 0x0014: return new IntPtr(1);
                case 0x0021: return new IntPtr(3);
                case 0x0201:
                    ReleaseCapture();
                    SendMessage(window, 0x00A1, new IntPtr(2), IntPtr.Zero);
                    return IntPtr.Zero;
                case 0x0203: open(); return IntPtr.Zero;
                case 0x007B:
                    GetWindowRect(window, out var location);
                    long value = lParam.ToInt64();
                    menu(value == -1 ? location.Left + 12 : (short)(value & 0xffff),
                        value == -1 ? location.Top + 12 : (short)((value >> 16) & 0xffff));
                    return IntPtr.Zero;
                case 0x0232:
                    if (GetWindowRect(window, out var rect))
                    {
                        requested = new(rect.Left, rect.Top);
                        Place();
                        GetWindowRect(window, out rect);
                        requested = new(rect.Left, rect.Top);
                        moved(requested);
                    }
                    return IntPtr.Zero;
                case 0x02E0:
                    PostMessage(window, 0x8003, IntPtr.Zero, IntPtr.Zero);
                    return IntPtr.Zero;
                case 0x0113:
                case 0x007E:
                case 0x001A:
                case 0x8003:
                    Refresh();
                    return IntPtr.Zero;
            }
        }
        catch (Win32Exception ex) { report("usage_strip_failed:" + ex.NativeErrorCode); }
        catch (UsageException ex) { report(ex.SafeCode); }
        return DefWindowProc(window, message, wParam, lParam);
    }

    private void Paint(IntPtr window)
    {
        var dc = BeginPaint(window, out var paint);
        if (dc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr background = IntPtr.Zero, pen = IntPtr.Zero, previousBrush = IntPtr.Zero, previousPen = IntPtr.Zero;
        try
        {
            GetClientRect(window, out var rect);
            background = CreateSolidBrush(dark ? 0x00292929u : 0x00ffffffu);
            pen = CreatePen(0, 1, dark ? 0x00a18efdu : 0x004b1fb1u);
            if (background == IntPtr.Zero || pen == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            previousBrush = SelectObject(dc, background); previousPen = SelectObject(dc, pen);
            RoundRect(dc, 0, 0, rect.Right, rect.Bottom, 12, 12);
            SetBkMode(dc, 1);
            SetTextColor(dc, dark ? 0x00dededeu : 0x00242424u);
            var previousFont = font == IntPtr.Zero ? IntPtr.Zero : SelectObject(dc, font);
            try
            {
                int inset = Math.Max(8, (int)(8 * fontDpi / 96));
                rect.Left += inset; rect.Right -= inset;
                DrawText(dc, text, text.Length, ref rect, 0x0004 | 0x0020 | 0x8000 | 0x0800);
            }
            finally { if (previousFont != IntPtr.Zero) SelectObject(dc, previousFont); }
        }
        finally
        {
            if (previousPen != IntPtr.Zero) SelectObject(dc, previousPen);
            if (previousBrush != IntPtr.Zero) SelectObject(dc, previousBrush);
            if (pen != IntPtr.Zero) DeleteObject(pen);
            if (background != IntPtr.Zero) DeleteObject(background);
            EndPaint(window, ref paint);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        KillTimer(handle, new UIntPtr(1));
        DestroyWindow(handle);
        if (font != IntPtr.Zero) DeleteObject(font);
        UnregisterClass(className, GetModuleHandle(null));
    }

    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style; public IntPtr Procedure; public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName; public IntPtr SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Size { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct PaintData
    {
        public IntPtr DC; public int Erase; public Rect Paint; public int Restore, Incremental;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
    }
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass value);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, IntPtr instance);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint extended, string name, string title, uint style, int x, int y, int width, int height, IntPtr owner, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "SetWindowTextW", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(IntPtr window, string value);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr window, UIntPtr id, uint interval, IntPtr callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr window, UIntPtr id);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr window, IntPtr rect, bool erase);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr BeginPaint(IntPtr window, out PaintData paint);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr window, ref PaintData paint);
    [DllImport("gdi32.dll", EntryPoint = "CreateFontW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateFont(int height, int width, int angle, int orientation, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern IntPtr CreatePen(int style, int width, uint color);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool RoundRect(IntPtr dc, int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll", EntryPoint = "GetTextExtentPoint32W", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetTextExtentPoint32(IntPtr dc, string text, int count, out Size size);
    [DllImport("user32.dll", EntryPoint = "DrawTextW", CharSet = CharSet.Unicode)] private static extern int DrawText(IntPtr dc, string text, int count, ref Rect rect, uint format);
}
