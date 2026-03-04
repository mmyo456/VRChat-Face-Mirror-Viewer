#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN

using System;
using System.Runtime.InteropServices;
using UnityEngine;

// Windows 窗口置顶控制脚本。
public class AlwaysOnTop : MonoBehaviour
{
    #region WIN32API

    // Win32 置顶/取消置顶标记。
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static readonly IntPtr HWND_NOT_TOPMOST = new IntPtr(-2);
    private const uint SWP_SHOWWINDOW = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public RECT(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public int X
        {
            get { return Left; }
            set
            {
                Right -= Left - value;
                Left = value;
            }
        }

        public int Y
        {
            get { return Top; }
            set
            {
                Bottom -= Top - value;
                Top = value;
            }
        }

        public int Height
        {
            get { return Bottom - Top; }
            set { Bottom = value + Top; }
        }

        public int Width
        {
            get { return Right - Left; }
            set { Right = value + Left; }
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(HandleRef hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    #endregion

    private void Start()
    {
        // 启动时先确保非置顶，避免继承系统历史状态。
        AssignTopmostWindow(false);
    }

    // 对当前进程窗口应用置顶/取消置顶。
    public bool AssignTopmostWindow(bool makeTopmost)
    {
        IntPtr hWnd = GetCurrentProcessWindowHandle();
        if (hWnd == IntPtr.Zero)
        {
            Debug.LogWarning("Current process window handle not found, skip topmost update.");
            return false;
        }

        RECT rect;
        if (!GetWindowRect(new HandleRef(this, hWnd), out rect))
        {
            Debug.LogWarning("GetWindowRect failed, skip topmost update.");
            return false;
        }

        return SetWindowPos(
            hWnd,
            makeTopmost ? HWND_TOPMOST : HWND_NOT_TOPMOST,
            rect.X,
            rect.Y,
            rect.Width,
            rect.Height,
            SWP_SHOWWINDOW
        );
    }

    // 获取当前进程的可见窗口句柄，避免多开时命中其他实例。
    private IntPtr GetCurrentProcessWindowHandle()
    {
        var activeHandle = GetActiveWindow();
        if (IsWindowFromCurrentProcess(activeHandle))
        {
            return activeHandle;
        }

        var currentProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        IntPtr matchedHandle = IntPtr.Zero;

        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd))
            {
                return true;
            }

            GetWindowThreadProcessId(hWnd, out var processId);
            if (processId != currentProcessId)
            {
                return true;
            }

            matchedHandle = hWnd;
            return false;
        }, IntPtr.Zero);

        return matchedHandle;
    }

    // 判断句柄是否属于当前进程。
    private static bool IsWindowFromCurrentProcess(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(hWnd, out var processId);
        return processId == (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
    }
}
#endif
