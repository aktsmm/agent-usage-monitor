using System;
using System.ComponentModel;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.IO;
using AiUsageTray.Core;

namespace AiUsageTray.Desktop;

public sealed class NativeTray : IDisposable
{
    private const int Callback = 0x8001;
    private const int Dispatch = 0x8002;
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> work = new();
    private readonly WindowProcedure procedure;
    private readonly string className = "AiUsageTray-" + Guid.NewGuid().ToString("N");
    public IntPtr Handle { get; private set; }
    private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly Action show;
    private readonly Action refresh;
    private readonly Action settings;
    private readonly Action exit;
    private readonly Action updates;
    private readonly Action startup;
    private readonly Func<bool> startupEnabled;
    private readonly Action stripToggle;
    private readonly Func<bool> stripEnabled;
    private readonly Action history;
    private readonly Func<UiLocalizer> localizer;
    private readonly Action<string> report;
    private bool added;
    private readonly IntPtr icon;
    private readonly bool ownsIcon;
    private bool disposed;

    public NativeTray(Action show, Action refresh, Action settings, Action exit, Action<string> report,
        Action updates, Action startup, Func<bool> startupEnabled, Action? stripToggle = null,
        Func<bool>? stripEnabled = null, Action? history = null, Func<UiLocalizer>? localizer = null)
    {
        this.show = show; this.refresh = refresh; this.settings = settings; this.exit = exit; this.report = report;
        this.updates = updates; this.startup = startup; this.startupEnabled = startupEnabled;
        this.stripToggle = stripToggle ?? (() => { });
        this.stripEnabled = stripEnabled ?? (() => false);
        this.history = history ?? show;
        this.localizer = localizer ?? (() => new());
        icon = LoadImage(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, "assets", "app.ico"),
            1, GetSystemMetrics(49), GetSystemMetrics(50), 0x0010);
        ownsIcon = icon != IntPtr.Zero;
        if (!ownsIcon)
        {
            report("tray_icon_missing");
            icon = LoadIcon(IntPtr.Zero, new IntPtr(32512));
        }
        procedure = WndProc;
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = Marshal.GetFunctionPointerForDelegate(procedure),
            Instance = GetModuleHandle(null), ClassName = className
        };
        if (RegisterClassEx(ref windowClass) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        Handle = CreateWindowEx(0, className, "Agent Usage Monitor Host", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, windowClass.Instance, IntPtr.Zero);
        if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Post(SendOrPostCallback callback, object? state)
    {
        work.Enqueue((callback, state));
        if (!PostMessage(Handle, Dispatch, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "tray_dispatch_failed");
    }

    public void Run()
    {
        int result;
        while ((result = GetMessage(out var message, IntPtr.Zero, 0, 0)) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
        if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Stop() => PostQuitMessage(0);
    public static void ShowError(string text, UiLocalizer? locale = null) =>
        MessageBox(IntPtr.Zero, text, (locale ?? new()).Text(UiText.AgentUsage), 16);
    public static void ShowInformation(string text, UiLocalizer? locale = null) =>
        MessageBox(IntPtr.Zero, text, (locale ?? new()).Text(UiText.AgentUsage), 64);
    public static bool Confirm(string text, UiLocalizer? locale = null) =>
        MessageBox(IntPtr.Zero, text, (locale ?? new()).Text(UiText.AgentUsage), 4 | 32) == 6;

    private IconData Data(uint flags) => new()
    {
        Size = (uint)Marshal.SizeOf<IconData>(), Window = Handle, Id = 1, Flags = flags,
        CallbackMessage = Callback, Icon = icon,
        Tip = localizer().Text(UiText.Brand), Info = "", InfoTitle = ""
    };

    public void Show()
    {
        var data = Data(1 | 2 | 4);
        if (!ShellNotifyIcon(0, ref data))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "tray_add_failed");
        added = true;
    }

    public void Notify(string title, string text, bool error = false)
    {
        var data = Data(16);
        data.InfoTitle = title.Length > 63 ? title[..63] : title;
        data.Info = text.Length > 255 ? text[..255] : text;
        data.InfoFlags = error ? 3u : 1u;
        data.TimeoutOrVersion = 5000;
        if (!ShellNotifyIcon(1, ref data)) report("tray_notification_failed");
    }

    private IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == Dispatch)
        {
            while (work.TryDequeue(out var item)) item.Callback(item.State);
            return IntPtr.Zero;
        }
        if (message == Callback)
        {
            int action = (int)lParam;
            if (action == 0x0202) show();
            if (action == 0x0205 || action == 0x007B) Menu();
            return IntPtr.Zero;
        }
        if (message == 0x0010)
        {
            exit();
            return IntPtr.Zero;
        }
        if (message == taskbarCreated && added)
        {
            try { Show(); }
            catch (Win32Exception) { report("tray_restore_failed"); }
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    private void Menu() => ShowMenu();

    public void ShowMenu(int? x = null, int? y = null)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) { report("tray_menu_create_failed"); return; }
        try
        {
            var locale = localizer();
            foreach (var (id, key) in new[] { (1u, UiText.AgentUsage), (2u, UiText.Refresh), (8u, UiText.History),
                (3u, UiText.Settings), (5u, UiText.UpdateCheck), (7u, UiText.ShowUsageStrip),
                (6u, UiText.StartAtLogon), (4u, UiText.Exit) })
                if (!AppendMenu(menu, id == 6 && startupEnabled() || id == 7 && stripEnabled() ? 8u : 0,
                    new UIntPtr(id), locale.Text(key)))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!GetCursorPos(out var position)) throw new Win32Exception(Marshal.GetLastWin32Error());
            SetForegroundWindow(Handle);
            uint selected = TrackPopupMenu(menu, 0x0100 | 0x0002, x ?? position.X, y ?? position.Y, 0, Handle, IntPtr.Zero);
            switch (selected)
            {
                case 1: show(); break;
                case 2: refresh(); break;
                case 3: settings(); break;
                case 4: exit(); break;
                case 5: updates(); break;
                case 6: startup(); break;
                case 7: stripToggle(); break;
                case 8: history(); break;
            }
            PostMessage(Handle, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Win32Exception) { report("tray_menu_failed"); }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (added) { var data = Data(0); ShellNotifyIcon(2, ref data); added = false; }
        if (Handle != IntPtr.Zero) { DestroyWindow(Handle); Handle = IntPtr.Zero; }
        UnregisterClass(className, GetModuleHandle(null));
        if (ownsIcon) DestroyIcon(icon);
    }

    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size; public uint Style; public IntPtr Procedure; public int ClassExtra; public int WindowExtra;
        public IntPtr Instance; public IntPtr Icon; public IntPtr Cursor; public IntPtr Background;
        public string? MenuName; public string ClassName; public IntPtr SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window; public uint Id; public UIntPtr WParam; public IntPtr LParam;
        public uint Time; public Point Position; public uint Private;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct IconData
    {
        public uint Size; public IntPtr Window; public uint Id; public uint Flags;
        public uint CallbackMessage; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State; public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint command, ref IconData data);
    [DllImport("user32.dll", EntryPoint = "LoadIconW")] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr window, IntPtr bounds);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string className, IntPtr instance);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)] private static extern int GetMessage(out Message message, IntPtr window, uint minimum, uint maximum);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)] private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}

public sealed class NativeContext(NativeTray tray) : SynchronizationContext
{
    public override void Post(SendOrPostCallback callback, object? state) => tray.Post(callback, state);
    public override SynchronizationContext CreateCopy() => new NativeContext(tray);
}
