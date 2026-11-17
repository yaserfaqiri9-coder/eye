using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class AlShifaTvNative
{
    public sealed class Display
    {
        public string DeviceName;
        public bool Primary;
        public int X, Y, Width, Height;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool MonitorCallback(IntPtr handle, IntPtr hdc, IntPtr rect, IntPtr data);
    private delegate bool WindowCallback(IntPtr handle, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr handle, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder value, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr handle);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr handle, int index, int value);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern int SetDisplayConfig(uint paths, IntPtr pathArray, uint modes, IntPtr modeArray, uint flags);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    public static void InitDpi()
    {
        SetProcessDPIAware();
        // Physical monitor coordinates remain correct when the employee screen and TV use different scaling.
        try { SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { }
    }
    public static int Extend() { return SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, 0x84); } // SDC_APPLY | SDC_TOPOLOGY_EXTEND
    public static Display[] Displays()
    {
        var displays = new List<Display>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr h, IntPtr dc, IntPtr r, IntPtr data) {
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (GetMonitorInfo(h, ref info)) displays.Add(new Display {
                DeviceName = info.Device, Primary = (info.Flags & 1) != 0,
                X = info.Monitor.Left, Y = info.Monitor.Top,
                Width = info.Monitor.Right - info.Monitor.Left, Height = info.Monitor.Bottom - info.Monitor.Top
            });
            return true;
        }, IntPtr.Zero);
        return displays.ToArray();
    }
    public static uint ProcessId(IntPtr handle) { uint processId; GetWindowThreadProcessId(handle, out processId); return processId; }
    public static IntPtr[] Windows(int[] processIds)
    {
        var ids = new HashSet<int>(processIds);
        var windows = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr data) {
            var name = new StringBuilder(128);
            GetClassName(h, name, name.Capacity);
            // Include an initially hidden app window, but exclude Chromium's internal message/helper windows.
            if (ids.Contains((int)ProcessId(h)) && name.ToString() == "Chrome_WidgetWin_1" && GetWindowTextLength(h) > 0) windows.Add(h);
            return true;
        }, IntPtr.Zero);
        return windows.ToArray();
    }
    public static bool Place(IntPtr handle, Display display)
    {
        ShowWindow(handle, 4); // SW_SHOWNOACTIVATE
        int style = GetWindowLong(handle, -16);
        SetWindowLong(handle, -16, style & ~0x00C40000); // Remove caption + sizing frame; no kiosk policies on the employee browser.
        return SetWindowPos(handle, IntPtr.Zero, display.X, display.Y, display.Width, display.Height, 0x74); // NOACTIVATE | NOZORDER | FRAMECHANGED | SHOWWINDOW
    }
    public static void Hide(IntPtr handle) { ShowWindow(handle, 0); }
    public static void RestoreFocus(IntPtr previous, int[] televisionProcessIds)
    {
        IntPtr current = GetForegroundWindow();
        var ids = new HashSet<int>(televisionProcessIds);
        ids.Add(System.Diagnostics.Process.GetCurrentProcess().Id);
        // Never steal focus if the employee has switched to an unrelated application during launch.
        if (IsWindow(previous) && ids.Contains((int)ProcessId(current))) SetForegroundWindow(previous);
    }
}
