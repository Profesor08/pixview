using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace PixView
{
    static class Native
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        public static extern int StrCmpLogicalW(string a, string b);

        [DllImport("shell32.dll")]
        public static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

        public const int SHCNE_ASSOCCHANGED = 0x08000000;

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int AssocQueryString(int flags, int str, string assoc, string extra, System.Text.StringBuilder outStr, ref int size);

        public const int ASSOCSTR_EXECUTABLE = 2, ASSOCSTR_PROGID = 20;

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_DONOTROUND = 1, DWMWCP_ROUND = 2;
        const int DWMWA_BORDER_COLOR = 34, DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

        // Windows 11 corners and the 1 px frame it draws around captioned windows; ignored
        // (harmless) on Windows 10. Rounded with a dark frame for a window, square and frameless
        // for full screen.
        public static void SetWindowShape(IntPtr hwnd, bool windowLook)
        {
            if (hwnd == IntPtr.Zero) return;
            int corner = windowLook ? DWMWCP_ROUND : DWMWCP_DONOTROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            int border = windowLook ? 0x003A3A3A : DWMWA_COLOR_NONE;   // COLORREF 0x00BBGGRR
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }

        // What Windows itself resolves for an extension (the same answer Explorer uses on double-click).
        public static string Assoc(string ext, int what)
        {
            int n = 1024;
            var sb = new System.Text.StringBuilder(n);
            return AssocQueryString(0, what, ext, "open", sb, ref n) == 0 ? sb.ToString() : null;
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hwnd);

        // Windows refuses SetForegroundWindow when another process owns the foreground (e.g. right
        // after the Open dialog closed). Sharing the input queue with the foreground thread lifts that.
        public static void ForceForeground(IntPtr hwnd)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == hwnd) return;
            uint me = GetCurrentThreadId();
            uint other = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, IntPtr.Zero);
            bool attached = other != 0 && other != me && AttachThreadInput(me, other, true);
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
                SetFocus(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(me, other, false);
            }
        }
    }

    // Background STA thread with a FIFO work queue (WPF imaging and RenderTargetBitmap need STA).
    class StaWorker
    {
        readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();

        public StaWorker(string name)
        {
            var t = new Thread(Run);
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Name = name;
            t.Priority = ThreadPriority.BelowNormal;
            t.Start();
        }

        void Run()
        {
            foreach (var a in queue.GetConsumingEnumerable())
            {
                try { a(); }
                catch { }
            }
        }

        public void Post(Action a)
        {
            queue.Add(a);
        }
    }
}
