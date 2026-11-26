using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HongguoRemote
{
    internal static class Native
    {
        internal delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);
        internal delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr param);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] internal static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] internal static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr SetFocus(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] internal static extern void SetLastError(uint error);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct GuiInfo { public int Size; public uint Flags; public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret; public Rect CaretRect; }
        [StructLayout(LayoutKind.Sequential)] struct MouseInfo { public Point Point; public uint Data, Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] struct KeyboardInfo { public uint Key, Scan, Flags, Time; public UIntPtr Extra; }

        internal static IntPtr FindWindow(int process)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hwnd, p) => {
                uint pid; GetWindowThreadProcessId(hwnd, out pid);
                if (pid == process && IsWindowVisible(hwnd)) { found = hwnd; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }
        internal static bool PlaybackFocused(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            var info = new GuiInfo { Size = Marshal.SizeOf(typeof(GuiInfo)) };
            return GetGUIThreadInfo(0, ref info) && (info.Focus == hwnd || IsChild(hwnd, info.Focus));
        }

        internal sealed class InputHooks : IDisposable
        {
            readonly HookProc keyboardProc, mouseProc;
            IntPtr keyboardHook, mouseHook;
            readonly HashSet<int> held = new HashSet<int>();
            internal Func<IntPtr> Target;
            internal Func<bool> Watch;
            internal Action<int> Key;
            internal Action<bool> Wheel;
            long lastWheel;

            internal InputHooks()
            {
                keyboardProc = Keyboard; mouseProc = Mouse;
                IntPtr module = GetModuleHandle(Process.GetCurrentProcess().MainModule.ModuleName);
                keyboardHook = SetWindowsHookEx(13, keyboardProc, module, 0);
                mouseHook = SetWindowsHookEx(14, mouseProc, module, 0);
                if (keyboardHook == IntPtr.Zero || mouseHook == IntPtr.Zero) { Dispose(); throw new InvalidOperationException("快捷键注册失败；仍可使用界面按钮和鼠标拖动。"); }
            }
            IntPtr Keyboard(int code, IntPtr message, IntPtr data)
            {
                if (code >= 0)
                {
                    var info = (KeyboardInfo)Marshal.PtrToStructure(data, typeof(KeyboardInfo));
                    int key = (int)info.Key, msg = message.ToInt32();
                    bool up = msg == 0x101 || msg == 0x105;
                    if (up && held.Remove(key)) return new IntPtr(1);
                    bool modified = (GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0x12) & 0x8000) != 0 || (GetAsyncKeyState(0x10) & 0x8000) != 0;
                    if (!up && !modified && Target != null && PlaybackFocused(Target()) &&
                        (key == 122 || key == 27 || (Watch() && (key == 38 || key == 40))))
                    {
                        if (held.Add(key) && Key != null) Key(key);
                        return new IntPtr(1);
                    }
                }
                return CallNextHookEx(keyboardHook, code, message, data);
            }
            IntPtr Mouse(int code, IntPtr message, IntPtr data)
            {
                if (code >= 0 && message.ToInt32() == 0x20A && Target != null && Watch())
                {
                    IntPtr target = Target();
                    var info = (MouseInfo)Marshal.PtrToStructure(data, typeof(MouseInfo));
                    IntPtr under = WindowFromPoint(info.Point);
                    if (target != IntPtr.Zero && (under == target || IsChild(target, under)) && PlaybackFocused(target))
                    {
                        long now = Environment.TickCount & Int32.MaxValue;
                        if (now - lastWheel > 450 || now < lastWheel)
                        {
                            lastWheel = now;
                            if (Wheel != null) Wheel(unchecked((short)(info.Data >> 16)) < 0);
                        }
                        return new IntPtr(1);
                    }
                }
                return CallNextHookEx(mouseHook, code, message, data);
            }
            public void Dispose()
            {
                if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
                if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
                keyboardHook = mouseHook = IntPtr.Zero;
            }
        }
    }
}
