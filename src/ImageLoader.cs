using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PixView
{
    static class Formats
    {
        // Popular formats. Decoding goes through Windows Imaging Component, so the "extension"
        // formats need the matching free Microsoft Store codec (see CodecHint).
        static readonly string[] Known =
        {
            ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".apng", ".bmp", ".dib", ".gif", ".tif", ".tiff",
            ".ico", ".cur", ".dds", ".jxr", ".wdp", ".hdp",
            ".webp", ".heic", ".heif", ".hif", ".avif", ".avifs", ".jxl",
            // own decoders (Decoders.cs)
            ".tga", ".pcx", ".qoi", ".hdr", ".pbm", ".pgm", ".ppm", ".pnm", ".pam", ".emf", ".wmf", ".icns"
        };

        public static readonly HashSet<string> Extensions = Build();

        static HashSet<string> Build()
        {
            var set = new HashSet<string>(Known, StringComparer.OrdinalIgnoreCase);
            // plus whatever extra decoders are installed (camera RAW, third-party codecs ...)
            try
            {
                const string Decoders = @"SOFTWARE\Classes\CLSID\{7ED96837-96F0-4812-B211-F13C24117ED3}\Instance";
                using (var cat = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(Decoders))
                    if (cat != null)
                        foreach (var id in cat.GetSubKeyNames())
                            using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes\CLSID\" + id))
                            {
                                var exts = k == null ? null : k.GetValue("FileExtensions") as string;
                                if (exts == null) continue;
                                foreach (var e in exts.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                                    if (e.StartsWith(".")) set.Add(e.ToLowerInvariant());
                            }
            }
            catch { }
            return set;
        }

        public static bool IsSupported(string path)
        {
            return Extensions.Contains(Path.GetExtension(path));
        }

        // What to install when Windows has no decoder for the file.
        public static string CodecHint(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".webp": return L.T("hint_webp");
                case ".heic": case ".heif": case ".hif": return L.T("hint_heif");
                case ".avif": return L.T("hint_avif");
                case ".jxl": return L.T("hint_jxl");
                default: return null;
            }
        }
    }

    class LoadedImage
    {
        public string Path;
        public BitmapSource Bitmap;          // oriented still image (first frame for animations)
        public int PixelWidth, PixelHeight;
        public int Page, PageCount = 1;      // multi-page TIFF
        public List<BitmapSource> Mips;      // [0] = Bitmap, then halves; null for small/animated images
        public List<BitmapSource> Frames;    // composed GIF frames, null for still images
        public List<int> Delays;             // ms
        public long FileSize;
        public string Error;
        public List<KeyValuePair<string, string>> Info = new List<KeyValuePair<string, string>>();
    }

    class ThumbInfo
    {
        public BitmapSource Bitmap;
        public int FullWidth, FullHeight;    // oriented size of the original image
    }

    static class ImageLoader
    {
        const long MaxGifPixels = 150000000; // total pixels of all composed frames

        static Stream Open(string path)
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            if (!string.Equals(Path.GetExtension(path), ".cur", StringComparison.OrdinalIgnoreCase)) return fs;
            // a cursor is an icon with type 2 in the header; the icon decoder only accepts type 1
            var ms = new MemoryStream();
            using (fs) fs.CopyTo(ms);
            var buf = ms.GetBuffer();
            if (ms.Length > 4 && buf[2] == 2) buf[2] = 1;
            ms.Position = 0;
            return ms;
        }

        public static LoadedImage Load(string path)
        {
            return Load(path, 0);
        }

        public static LoadedImage Load(string path, int page)
        {
            var li = new LoadedImage { Path = path };
            try
            {
                var fi = new FileInfo(path);
                li.FileSize = fi.Length;
                Decoded own = null;
                if (Decoders.Handles(path)) own = Decoders.Decode(path);
                else if (MayBeApng(path))
                {
                    var bytes = File.ReadAllBytes(path);
                    if (Animation.IsApng(bytes)) own = Animation.DecodeApng(bytes);
                }                else if ((path.EndsWith(".avif", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".avifs", StringComparison.OrdinalIgnoreCase))
                    && AvifSequence.IsSequence(path))
                {
                    // without Media Foundation / AV1 support fall back to the still image below
                    try { own = AvifSequence.Decode(path); } catch { own = null; }
                }

                if (own != null)
                {
                    Apply(li, own, fi);
                    return li;
                }
                using (var fs = Open(path))
                {
                    var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    var frame = PickFrame(dec);
                    if (dec is TiffBitmapDecoder && dec.Frames.Count > 1)
                    {
                        li.PageCount = dec.Frames.Count;
                        li.Page = Math.Max(0, Math.Min(page, li.PageCount - 1));
                        frame = dec.Frames[li.Page];
                    }
                    var md = SafeMeta(frame);
                    int orient = ReadOrientation(md);

                    // DNG: the default decoder gives only the embedded preview; develop the real RAW
                    if (path.EndsWith(".dng", StringComparison.OrdinalIgnoreCase) && Math.Max(frame.PixelWidth, frame.PixelHeight) < 2000)
                    {
                        BitmapSource full = null;
                        try { full = WicRaw.Decode(path); } catch { }
                        if (full != null && full.PixelWidth > frame.PixelWidth)
                        {
                            li.Bitmap = full;
                            li.PixelWidth = full.PixelWidth;
                            li.PixelHeight = full.PixelHeight;
                            li.Mips = BuildMips(full);
                            FillInfo(li, md, L.T("fmt_dng"), 1, fi.LastWriteTime);
                            return li;
                        }
                    }

                    if (dec is GifBitmapDecoder && dec.Frames.Count > 1)
                        ComposeGif(dec, li);
                    else if (dec.Frames.Count > 1 && path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                    {
                        var anim = Animation.FromWebp(dec, File.ReadAllBytes(path));
                        if (anim != null)
                        {
                            Apply(li, anim, fi);
                            return li;
                        }
                    }

                    BitmapSource bmp;
                    if (li.Frames != null) bmp = li.Frames[0];
                    else if (orient != 1) bmp = ApplyOrientation(frame, orient);
                    else bmp = frame;
                    if (bmp.CanFreeze && !bmp.IsFrozen) bmp.Freeze();

                    li.Bitmap = bmp;
                    if (li.Frames == null) li.Mips = BuildMips(bmp);
                    li.PixelWidth = bmp.PixelWidth;
                    li.PixelHeight = bmp.PixelHeight;

                    string format = null;
                    try { format = dec.CodecInfo.FriendlyName; } catch { }
                    FillInfo(li, md, format, dec.Frames.Count, fi.LastWriteTime);
                }
            }
            catch (Exception ex)
            {
                li.Error = ex.Message;
                string hint = Formats.CodecHint(path);
                if (hint != null && (ex is NotSupportedException || ex is FileFormatException))
                    li.Error = L.F("err_no_codec", hint);
            }
            return li;
        }

        static void Apply(LoadedImage li, Decoded dd, FileInfo fi)
        {
            li.Frames = dd.Frames;
            li.Delays = dd.Delays;
            li.Bitmap = dd.Image;
            li.PixelWidth = dd.Image.PixelWidth;
            li.PixelHeight = dd.Image.PixelHeight;
            if (li.Frames == null) li.Mips = BuildMips(dd.Image);
            FillInfo(li, null, dd.Format, dd.Frames == null ? 1 : dd.Frames.Count, fi.LastWriteTime);
        }

        // cheap pre-check: an animated PNG has its acTL chunk near the start of the file
        static bool MayBeApng(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".png" && ext != ".apng") return false;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var head = new byte[(int)Math.Min(fs.Length, 1 << 20)];
                int n = fs.Read(head, 0, head.Length);
                for (int i = 8; i + 4 <= n; i++)
                    if (head[i] == 'a' && head[i + 1] == 'c' && head[i + 2] == 'T' && head[i + 3] == 'L') return true;
            }
            return false;
        }

        public static ThumbInfo LoadThumb(string path, int box)
        {
            if (Decoders.Handles(path))
            {
                var img = Decoders.Decode(path).Image;
                double s = Math.Min(1.0, (double)box / Math.Max(img.PixelWidth, img.PixelHeight));
                BitmapSource small = img;
                if (s < 1)
                {
                    var cb = new CachedBitmap(new TransformedBitmap(img, new ScaleTransform(s, s)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    cb.Freeze();
                    small = cb;
                }
                return new ThumbInfo { Bitmap = small, FullWidth = img.PixelWidth, FullHeight = img.PixelHeight };
            }
            int pw, ph, orient;
            using (var fs = Open(path))
            {
                var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                var f = PickFrame(dec);
                pw = f.PixelWidth;
                ph = f.PixelHeight;
                orient = ReadOrientation(SafeMeta(f));
            }

            BitmapSource bmp;
            using (var fs = Open(path))
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bi.StreamSource = fs;
                if (pw > box || ph > box)
                {
                    if (pw >= ph) bi.DecodePixelWidth = box;
                    else bi.DecodePixelHeight = box;
                }
                bi.EndInit();
                bi.Freeze();
                bmp = bi;
            }
            if (orient != 1) bmp = ApplyOrientation(bmp, orient);

            bool swap = orient >= 5;
            return new ThumbInfo { Bitmap = bmp, FullWidth = swap ? ph : pw, FullHeight = swap ? pw : ph };
        }

// Half-size copies used while zoom is animating: cheap to scale with Linear filtering
        // and free of the aliasing a big downscale gives.
        static List<BitmapSource> BuildMips(BitmapSource full)
        {
            var list = new List<BitmapSource> { full };
            var cur = full;
            while (Math.Max(cur.PixelWidth, cur.PixelHeight) > 1600)
            {
                var tb = new TransformedBitmap(cur, new ScaleTransform(0.5, 0.5));
                var cb = new CachedBitmap(tb, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                cb.Freeze();
                list.Add(cb);
                cur = cb;
            }
            return list.Count > 1 ? list : null;
        }

        static BitmapFrame PickFrame(BitmapDecoder dec)
        {
            // Icons contain several sizes - show the largest one.
            if (dec is IconBitmapDecoder && dec.Frames.Count > 1)
                return dec.Frames.OrderByDescending(f => f.PixelWidth).First();
            return dec.Frames[0];
        }

        static BitmapMetadata SafeMeta(BitmapFrame f)
        {
            try { return f.Metadata as BitmapMetadata; }
            catch { return null; }
        }

        static object Q(BitmapMetadata md, string query)
        {
            if (md == null) return null;
            try { return md.GetQuery(query); }
            catch { return null; }
        }

        static int Qi(BitmapMetadata md, string query, int def)
        {
            var v = Q(md, query);
            if (v == null) return def;
            try { return Convert.ToInt32(v); }
            catch { return def; }
        }

        static int ReadOrientation(BitmapMetadata md)
        {
            int o = Qi(md, "System.Photo.Orientation", 1);
            return o >= 1 && o <= 8 ? o : 1;
        }

        public static BitmapSource ApplyOrientation(BitmapSource src, int o)
        {
            Transform t;
            switch (o)
            {
                case 2: t = new ScaleTransform(-1, 1); break;
                case 3: t = new RotateTransform(180); break;
                case 4: t = new ScaleTransform(1, -1); break;
                case 5: t = Group(new RotateTransform(90), new ScaleTransform(-1, 1)); break;
                case 6: t = new RotateTransform(90); break;
                case 7: t = Group(new RotateTransform(270), new ScaleTransform(-1, 1)); break;
                case 8: t = new RotateTransform(270); break;
                default: return src;
            }
            var tb = new TransformedBitmap(src, t);
            var cb = new CachedBitmap(tb, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            cb.Freeze();
            return cb;
        }

        static Transform Group(params Transform[] items)
        {
            var g = new TransformGroup();
            foreach (var i in items) g.Children.Add(i);
            return g;
        }

        // Composes GIF frames onto a full canvas honoring offsets and disposal methods.
        static void ComposeGif(BitmapDecoder dec, LoadedImage li)
        {
            int w = Qi(dec.Metadata, "/logscrdesc/Width", 0);
            int h = Qi(dec.Metadata, "/logscrdesc/Height", 0);
            if (w <= 0 || h <= 0) { w = dec.Frames[0].PixelWidth; h = dec.Frames[0].PixelHeight; }
            if ((long)w * h * dec.Frames.Count > MaxGifPixels) return;

            var full = new Rect(0, 0, w, h);
            var frames = new List<BitmapSource>();
            var delays = new List<int>();
            BitmapSource canvas = null;

            foreach (var f in dec.Frames)
            {
                var md = SafeMeta(f);
                int left = Qi(md, "/imgdesc/Left", 0);
                int top = Qi(md, "/imgdesc/Top", 0);
                int delay = Qi(md, "/grctlext/Delay", 10) * 10;
                int disposal = Qi(md, "/grctlext/Disposal", 0);
                if (delay < 20) delay = 100; // same rule browsers use
                var rect = new Rect(left, top, f.PixelWidth, f.PixelHeight);

                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    if (canvas != null) dc.DrawImage(canvas, full);
                    dc.DrawImage(f, rect);
                }
                var rtb = Render(dv, w, h);
                frames.Add(rtb);
                delays.Add(delay);

                if (disposal == 2)
                {
                    var dv2 = new DrawingVisual();
                    using (var dc = dv2.RenderOpen())
                    {
                        dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(rect)));
                        dc.DrawImage(rtb, full);
                        dc.Pop();
                    }
                    canvas = Render(dv2, w, h);
                }
                else if (disposal != 3)
                {
                    canvas = rtb;
                }
            }
            li.Frames = frames;
            li.Delays = delays;
        }

        static BitmapSource Render(Visual v, int w, int h)
        {
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(v);
            rtb.Freeze();
            return rtb;
        }

        static void FillInfo(LoadedImage li, BitmapMetadata md, string format, int frameCount, DateTime modified)
        {
            Action<string, string> add = (k, v) =>
            {
                if (!string.IsNullOrWhiteSpace(v)) li.Info.Add(new KeyValuePair<string, string>(k, v.Trim()));
            };

            add(L.T("i_file"), Path.GetFileName(li.Path));
            add(L.T("i_folder"), Path.GetDirectoryName(li.Path));
            double mp = li.PixelWidth * (double)li.PixelHeight / 1e6;
            add(L.T("i_resolution"), li.PixelWidth + " × " + li.PixelHeight +
                (mp >= 0.1 ? "  (" + mp.ToString("0.#", CultureInfo.CurrentCulture) + " " + L.T("u_mp") + ")" : ""));
            add(L.T("i_filesize"), FormatSize(li.FileSize));
            add(L.T("i_format"), format + (li.PageCount > 1 ? L.F("i_pages", li.PageCount) : frameCount > 1 ? L.F("i_frames", frameCount) : ""));
            add(L.T("i_modified"), modified.ToString("g", CultureInfo.CurrentCulture));

            if (md == null) return;
            string make = TryStr(() => md.CameraManufacturer);
            string model = TryStr(() => md.CameraModel);
            string camera = model;
            if (make != null && (model == null || !model.StartsWith(make, StringComparison.OrdinalIgnoreCase)))
                camera = (make + " " + model).Trim();
            add(L.T("i_camera"), camera);
            add(L.T("i_taken"), TryStr(() => md.DateTaken));

            double? exp = ToDouble(Q(md, "System.Photo.ExposureTime"));
            if (exp.HasValue && exp.Value > 0)
                add(L.T("i_exposure"), (exp.Value < 1 && Math.Round(1 / exp.Value) > 1 ? "1/" + Math.Round(1 / exp.Value) : exp.Value.ToString("0.#")) + " " + L.T("u_s"));
            double? fn = ToDouble(Q(md, "System.Photo.FNumber"));
            if (fn.HasValue && fn.Value > 0) add(L.T("i_aperture"), "f/" + fn.Value.ToString("0.#"));
            double? iso = ToDouble(Q(md, "System.Photo.ISOSpeed"));
            if (iso.HasValue && iso.Value > 0) add("ISO", iso.Value.ToString("0"));
            double? focal = ToDouble(Q(md, "System.Photo.FocalLength"));
            if (focal.HasValue && focal.Value > 0) add(L.T("i_focal"), focal.Value.ToString("0.#") + " " + L.T("u_mm"));
            add(L.T("i_software"), TryStr(() => md.ApplicationName));
        }

        static string TryStr(Func<string> f)
        {
            try
            {
                var s = f();
                return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            }
            catch { return null; }
        }

        static double? ToDouble(object v)
        {
            if (v == null) return null;
            var arr = v as Array;
            if (arr != null) { if (arr.Length == 0) return null; v = arr.GetValue(0); }
            if (v is ulong)
            {
                ulong u = (ulong)v;
                uint num = (uint)(u & 0xFFFFFFFF), den = (uint)(u >> 32);
                return den == 0 ? (double?)null : (double)num / den;
            }
            if (v is long)
            {
                long l = (long)v;
                int num = (int)(l & 0xFFFFFFFF), den = (int)(l >> 32);
                return den == 0 ? (double?)null : (double)num / den;
            }
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " " + L.T("u_b");
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " " + L.T("u_kb");
            return (bytes / 1048576.0).ToString("0.#") + " " + L.T("u_mb");
        }
    }
}
