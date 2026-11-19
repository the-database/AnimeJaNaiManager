using System;
using System.Runtime.InteropServices;

namespace AnimeJaNaiConfEditor.Services
{
    // Windows: makes another process's window (the player's, from --owner-window) the owner of
    // the Manager's window, so the Manager stays above the player and minimizes with it.
    public static class OwnerWindow
    {
        private const int GWLP_HWNDPARENT = -8;

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        public static bool Exists(long owner) => OperatingSystem.IsWindows() && owner != 0 && IsWindow(new IntPtr(owner));

        // Returns whether the owner was set. A window id that isn't a window (the player closed)
        // is ignored.
        public static bool Set(IntPtr window, long owner)
        {
            if (!OperatingSystem.IsWindows() || window == IntPtr.Zero || !Exists(owner) || owner == window.ToInt64()) return false;
            SetWindowLongPtr(window, GWLP_HWNDPARENT, new IntPtr(owner));
            return GetWindowLongPtr(window, GWLP_HWNDPARENT) == new IntPtr(owner);
        }

        public static void Clear(IntPtr window)
        {
            if (OperatingSystem.IsWindows() && window != IntPtr.Zero) SetWindowLongPtr(window, GWLP_HWNDPARENT, IntPtr.Zero);
        }

        public static long Current(IntPtr window) =>
            OperatingSystem.IsWindows() && window != IntPtr.Zero ? GetWindowLongPtr(window, GWLP_HWNDPARENT).ToInt64() : 0;
    }
}
