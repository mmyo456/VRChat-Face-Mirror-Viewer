#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN

using UnityEngine;
using System;
using System.Runtime.InteropServices;


public class AlwaysOnTop : MonoBehaviour
{
    #region WIN32API

    public static readonly System.IntPtr HWND_TOPMOST = new System.IntPtr(-1);
    public static readonly System.IntPtr HWND_NOT_TOPMOST = new System.IntPtr(-2);
    const System.UInt32 SWP_SHOWWINDOW = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;

        public RECT(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public int X
        {
            get
            {
                return Left;
            }
            set
            {
                Right -= (Left - value);
                Left = value;
            }
        }

        public int Y
        {
            get
            {
                return Top;
            }
            set
            {
                Bottom -= (Top - value);
                Top = value;
            }
        }

        public int Height
        {
            get
            {
                return Bottom - Top;
            }
            set
            {
                Bottom = value + Top;
            }
        }

        public int Width
        {
            get
            {
                return Right - Left;
            }
            set
            {
                Right = value + Left;
            }
        }

    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern System.IntPtr FindWindow(String lpClassName, String lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(HandleRef hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(System.IntPtr hWnd, System.IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    #endregion


    // Use this for initialization
    void Start()
    {
        AssignTopmostWindow(Application.productName, false);
    }

    public bool AssignTopmostWindow(string WindowTitle, bool MakeTopmost)
    {
        UnityEngine.Debug.Log("Assigning top most flag to window of title: " + WindowTitle);

        System.IntPtr hWnd = FindWindow((string)null, WindowTitle);
        if (hWnd == System.IntPtr.Zero)
        {
            UnityEngine.Debug.LogWarning("Window handle not found, skip topmost update.");
            return false;
        }

        RECT rect = new RECT();
        if (!GetWindowRect(new HandleRef(this, hWnd), out rect))
        {
            UnityEngine.Debug.LogWarning("GetWindowRect failed, skip topmost update.");
            return false;
        }

        return SetWindowPos(hWnd, MakeTopmost ? HWND_TOPMOST : HWND_NOT_TOPMOST, rect.X, rect.Y, rect.Width, rect.Height, SWP_SHOWWINDOW);
    }

}
#endif
