using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace PixView
{
    // EMF / EMF+ / WMF are vector pictures: GDI+ renders them into a bitmap big enough to stay
    // sharp when zoomed (longest side at least 3000 px, at most 8192 px).
    static class Metafiles
    {
        const int MinSide = 3000, MaxSide = 8192;

        public static Decoded Render(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            using (var ms = new MemoryStream(data))
            using (var mf = new Metafile(ms))
            {
                var hdr = mf.GetMetafileHeader();
                // natural size in pixels at 96 dpi
                double nw = mf.Width * 96.0 / Math.Max(1f, mf.HorizontalResolution);
                double nh = mf.Height * 96.0 / Math.Max(1f, mf.VerticalResolution);
                if (nw < 1 || nh < 1) { nw = hdr.Bounds.Width; nh = hdr.Bounds.Height; }
                if (nw < 1 || nh < 1) throw new InvalidDataException(L.T("err_no_dims"));
                double scale = Math.Max(1, MinSide / Math.Max(nw, nh));
                scale = Math.Min(scale, MaxSide / Math.Max(nw, nh));
                int w = Math.Max(1, (int)Math.Round(nw * scale)), h = Math.Max(1, (int)Math.Round(nh * scale));

                using (var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.White);   // metafiles are drawn for paper: white page
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                        g.DrawImage(mf, new Rectangle(0, 0, w, h));
                    }
                    var px = new byte[w * h * 4];
                    var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    try
                    {
                        for (int y = 0; y < h; y++)
                            Marshal.Copy(bd.Scan0 + y * bd.Stride, px, y * w * 4, w * 4);
                    }
                    finally { bmp.UnlockBits(bd); }

                    string kind = path.EndsWith(".wmf", StringComparison.OrdinalIgnoreCase) ? "WMF" : hdr.IsEmfPlus() ? "EMF+" : "EMF";
                    return new Decoded
                    {
                        Image = Decoders.Bgra(w, h, px),
                        Format = L.F("fmt_vector", kind, w, h)
                    };
                }
            }
        }
    }
}
