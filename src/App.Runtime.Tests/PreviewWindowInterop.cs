using System.Runtime.InteropServices;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    // Shared native helpers for the real-HWND preview interaction tests.
    [StructLayout(LayoutKind.Sequential)] private struct UnfixRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")] private static extern bool UnfixGetWindowRect(nint hwnd, out UnfixRect rect);
    [DllImport("user32.dll", EntryPoint = "GetClientRect")] private static extern bool UnfixGetClientRect(nint hwnd, out UnfixRect rect);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern nint UnfixForeground();
    [DllImport("user32.dll", EntryPoint = "GetCapture")] private static extern nint UnfixCapture();
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void UnfixMouse(uint flags, uint dx, uint dy, uint data, nuint extra);
    [DllImport("user32.dll", EntryPoint = "keybd_event")] private static extern void UnfixKey(byte key, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool UnfixPostMessage(nint hwnd, int message, nint wParam, nint lParam);
}
