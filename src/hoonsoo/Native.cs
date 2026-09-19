using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Hoonsoo;
public static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] public static extern IntPtr GetModuleHandle(string? lpModuleName);

    public sealed class EscapeInterceptor : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int VK_ESCAPE = 0x1B;
        private readonly Action onEscape;
        private readonly LowLevelKeyboardProc proc;
        private IntPtr hookId = IntPtr.Zero;
        public EscapeInterceptor(Action onEscape)
        {
            this.onEscape = onEscape;
            this.proc = HookCallback;
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            this.hookId = SetWindowsHookEx(WH_KEYBOARD_LL, this.proc, GetModuleHandle(curModule?.ModuleName), 0);
        }
        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
            {
                int vkCode = Marshal.ReadInt32(lParam);
                if (vkCode == VK_ESCAPE)
                {
                    try { onEscape(); } catch { }
                    return (IntPtr)1; // Consume key!
                }
            }
            return CallNextHookEx(hookId, nCode, wParam, lParam);
        }
        public void Dispose()
        {
            if (hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hookId);
                hookId = IntPtr.Zero;
            }
        }
    }
    public static string? ProcessAt(Point point)
    {
        try { var window = WindowFromPoint(point); if (window == IntPtr.Zero) return null; GetWindowThreadProcessId(window, out var id); using var p = Process.GetProcessById((int)id); return ProcessIdentity.Normalize(p.ProcessName); }
        catch { return null; }
    }
    private static readonly HashSet<string> SystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
        "svchost", "fontdrvhost", "dwm", "conhost", "spoolsv", "explorer", "taskhostw", "sihost",
        "ctfmon", "textinputhost", "searchhost", "searchapp", "shellexperiencehost",
        "startmenuexperiencehost", "systemsettings", "lockapp", "applicationframehost",
        "securityhealthservice", "securityhealthsystray", "wudfhost", "audiodg", "smartscreen",
        "runtimebroker", "dashost", "compattelrunner", "wmiprvse", "msmpeng", "nissrv",
        "dllhost", "taskmgr", "devlingo", "searchindexer", "widgets", "widgetservice",
        "gamebar", "gamebarftserver", "xboxappservices"
    };

    private static readonly HashSet<string> UserAccessories = new(StringComparer.OrdinalIgnoreCase)
    {
        "notepad", "mspaint", "calc", "wordpad", "snippingtool"
    };

    public static IReadOnlyList<string> RunningApps()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    if (string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;

                    string rawName = p.ProcessName;
                    if (SystemProcesses.Contains(rawName)) continue;

                    if (!UserAccessories.Contains(rawName))
                    {
                        try
                        {
                            string? path = p.MainModule?.FileName;
                            if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(winDir) &&
                                path.StartsWith(winDir, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                        }
                        catch { }
                    }

                    names.Add(ProcessIdentity.Normalize(rawName));
                }
                catch { }
            }
        }
        return names.OrderBy(x => x).ToArray();
    }
}
public sealed class Hotkey : IDisposable
{
    private readonly HwndSource source;
    private int activeId = 100;
    private bool registered;
    private uint modifiers, key;
    public event Action? Pressed;
    public Hotkey()
    {
        source = new HwndSource(new HwndSourceParameters("hoonsoo hotkey") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0 });
        source.AddHook(Hook);
    }
    public static bool IsValid(uint modifiers, uint key) => modifiers is > 0 and <= 15 && (key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x70 and <= 0x7B or 0x20);
    public bool Set(uint newModifiers, uint newKey)
    {
        if (!IsValid(newModifiers, newKey)) return false;
        if (registered && modifiers == newModifiers && key == newKey) return true;
        int next = activeId == 100 ? 101 : 100;
        if (!Native.RegisterHotKey(source.Handle, next, newModifiers | 0x4000, newKey)) return false;
        if (registered) Native.UnregisterHotKey(source.Handle, activeId);
        activeId = next; registered = true; modifiers = newModifiers; key = newKey; return true;
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x312 && wParam.ToInt32() == activeId) { handled = true; Pressed?.Invoke(); } return IntPtr.Zero;
    }
    public void Dispose() { if (registered) Native.UnregisterHotKey(source.Handle, activeId); source.Dispose(); }
}
