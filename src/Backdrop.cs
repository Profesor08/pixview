using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace PixView
{
    // Snapshot of the work area of the monitor under the cursor, taken before any window of ours appears.
    // Drawn dimmed behind the photo it looks like a translucent overlay, but lets the viewer use
    // an ordinary opaque window: a layered (AllowsTransparency) one copies every frame from the
    // GPU to system memory, which makes zoom and panning stutter on large screens.
    static class Backdrop
    {
        public static System.Drawing.Rectangle Bounds;
        public static BitmapSource Image;

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr obj);

        public static void Capture()
        {
            var scr = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            Bounds = scr.WorkingArea;   // the viewer covers the work area, the taskbar stays visible
            try
            {
                using (var bmp = new System.Drawing.Bitmap(Bounds.Width, Bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                        g.CopyFromScreen(Bounds.Location, System.Drawing.Point.Empty, Bounds.Size);
                    IntPtr h = bmp.GetHbitmap();
                    try
                    {
                        var src = Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        src.Freeze();
                        Image = src;
                    }
                    finally { DeleteObject(h); }
                }
            }
            catch
            {
                Image = null; // plain dark background then
            }
        }
    }
}
