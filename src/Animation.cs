using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PixView
{
    // One frame of an animation as stored in the file: a rectangle on the canvas plus how it is
    // blended in and what happens to its area afterwards.
    class FramePart
    {
        public BitmapSource Image;
        public int X, Y;
        public int Delay;          // ms
        public bool BlendOver;     // false: replace the area, true: alpha-composite over it
        public int Dispose;        // 0 keep, 1 clear to transparent, 2 restore what was there before
    }

    static class Animation
    {
        public const long MaxPixels = 150000000;   // all composed frames together

        // Composes partial frames into full canvases (APNG and WebP use the same model).
        public static Decoded Compose(int w, int h, List<FramePart> parts, string format)
        {
            if ((long)w * h * parts.Count > MaxPixels || parts.Count == 0) return null;
            var canvas = new byte[w * h * 4];
            var frames = new List<BitmapSource>();
            var delays = new List<int>();
            foreach (var f in parts)
            {
                var src = new FormatConvertedBitmap(f.Image, PixelFormats.Bgra32, null, 0);
                int fw = src.PixelWidth, fh = src.PixelHeight;
                var fp = new byte[fw * fh * 4];
                src.CopyPixels(fp, fw * 4, 0);
                byte[] saved = f.Dispose == 2 ? (byte[])canvas.Clone() : null;

                for (int y = 0; y < fh; y++)
                {
                    int cy = f.Y + y;
                    if (cy < 0 || cy >= h) continue;
                    for (int x = 0; x < fw; x++)
                    {
                        int cx = f.X + x;
                        if (cx < 0 || cx >= w) continue;
                        int s = (y * fw + x) * 4, d = (cy * w + cx) * 4;
                        int sa = fp[s + 3];
                        if (!f.BlendOver || sa == 255) { canvas[d] = fp[s]; canvas[d + 1] = fp[s + 1]; canvas[d + 2] = fp[s + 2]; canvas[d + 3] = (byte)sa; }
                        else if (sa != 0)
                        {
                            // straight-alpha "over"
                            int da = canvas[d + 3];
                            int oa = sa + da * (255 - sa) / 255;
                            for (int c = 0; c < 3; c++)
                                canvas[d + c] = (byte)((fp[s + c] * sa + canvas[d + c] * da * (255 - sa) / 255) / Math.Max(1, oa));
                            canvas[d + 3] = (byte)oa;
                        }
                    }
                }
                frames.Add(Decoders.Bgra(w, h, (byte[])canvas.Clone()));
                delays.Add(f.Delay);

                if (f.Dispose == 1)
                {
                    for (int y = Math.Max(0, f.Y); y < Math.Min(h, f.Y + fh); y++)
                        Array.Clear(canvas, (y * w + Math.Max(0, f.X)) * 4, (Math.Min(w, f.X + fw) - Math.Max(0, f.X)) * 4);
                }
                else if (f.Dispose == 2) canvas = saved;
            }
            return new Decoded { Image = frames[0], Frames = frames.Count > 1 ? frames : null, Delays = delays, Format = format };
        }

        // ------------------------------------------------------------------ APNG

        static readonly byte[] PngSig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        static bool IsPng(byte[] d)
        {
            if (d.Length < 8) return false;
            for (int i = 0; i < 8; i++) if (d[i] != PngSig[i]) return false;
            return true;
        }

        static int BE32(byte[] d, int p) { return (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3]; }
        static int BE16(byte[] d, int p) { return (d[p] << 8) | d[p + 1]; }

        // Is this PNG animated (an acTL chunk before the first IDAT)?
        public static bool IsApng(byte[] d)
        {
            if (!IsPng(d)) return false;
            int p = 8;
            while (p + 8 <= d.Length)
            {
                int len = BE32(d, p);
                string type = System.Text.Encoding.ASCII.GetString(d, p + 4, 4);
                if (type == "acTL") return true;
                if (type == "IDAT" || len < 0) return false;
                p += 12 + len;
            }
            return false;
        }

        public static Decoded DecodeApng(byte[] d)
        {
            int p = 8;
            byte[] ihdr = null;
            var shared = new List<byte[]>();      // PLTE, tRNS, colour info: copied into every frame
            var parts = new List<FramePart>();
            FramePart pending = null;
            int pw = 0, ph = 0;
            var data = new MemoryStream();
            bool seenIdat = false, firstIsDefault = false;

            Action flush = () =>
            {
                if (pending == null) return;
                pending.Image = DecodeFramePng(ihdr, pw, ph, shared, data.ToArray());
                parts.Add(pending);
                pending = null;
                data.SetLength(0);
            };

            while (p + 12 <= d.Length)
            {
                int len = BE32(d, p);
                if (len < 0 || p + 12 + len > d.Length) break;
                string type = System.Text.Encoding.ASCII.GetString(d, p + 4, 4);
                int dp = p + 8;
                switch (type)
                {
                    case "IHDR":
                        ihdr = new byte[len];
                        Buffer.BlockCopy(d, dp, ihdr, 0, len);
                        break;
                    case "fcTL":
                        flush();
                        pw = BE32(d, dp + 4); ph = BE32(d, dp + 8);
                        int num = BE16(d, dp + 20), den = BE16(d, dp + 22);
                        if (den == 0) den = 100;
                        pending = new FramePart
                        {
                            X = BE32(d, dp + 12),
                            Y = BE32(d, dp + 16),
                            Delay = Math.Max(10, num * 1000 / den),
                            Dispose = d[dp + 24],
                            BlendOver = d[dp + 25] == 1
                        };
                        if (!seenIdat && parts.Count == 0) firstIsDefault = true;
                        break;
                    case "IDAT":
                        seenIdat = true;
                        // the default image is a frame only if an fcTL came before it
                        if (pending != null && firstIsDefault && parts.Count == 0) data.Write(d, dp, len);
                        break;
                    case "fdAT":
                        if (pending != null) data.Write(d, dp + 4, len - 4);   // skip sequence number
                        break;
                    case "IEND":
                        break;
                    case "acTL":
                    case "tEXt": case "zTXt": case "iTXt": case "tIME": case "pHYs": case "eXIf":
                        break;
                    default:
                        if (!seenIdat) { var c = new byte[12 + len]; Buffer.BlockCopy(d, p, c, 0, 12 + len); shared.Add(c); }
                        break;
                }
                p += 12 + len;
            }
            flush();
            if (ihdr == null || parts.Count == 0) return null;
            // a first frame may not ask to restore "previous" (there is none): treat as clear
            if (parts[0].Dispose == 2) parts[0].Dispose = 1;
            return Compose(BE32(ihdr, 0), BE32(ihdr, 4), parts, L.T("fmt_apng"));
        }

        // Rebuilds one frame as a standalone PNG and lets the system decoder do the rest.
        static BitmapSource DecodeFramePng(byte[] ihdr, int w, int h, List<byte[]> shared, byte[] zdata)
        {
            var ms = new MemoryStream();
            ms.Write(PngSig, 0, 8);
            var hdr = (byte[])ihdr.Clone();
            hdr[0] = (byte)(w >> 24); hdr[1] = (byte)(w >> 16); hdr[2] = (byte)(w >> 8); hdr[3] = (byte)w;
            hdr[4] = (byte)(h >> 24); hdr[5] = (byte)(h >> 16); hdr[6] = (byte)(h >> 8); hdr[7] = (byte)h;
            WriteChunk(ms, "IHDR", hdr);
            foreach (var c in shared) ms.Write(c, 0, c.Length);
            WriteChunk(ms, "IDAT", zdata);
            WriteChunk(ms, "IEND", new byte[0]);
            ms.Position = 0;
            var dec = new PngBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var f = dec.Frames[0];
            f.Freeze();
            return f;
        }

        static void WriteChunk(Stream s, string type, byte[] data)
        {
            var t = System.Text.Encoding.ASCII.GetBytes(type);
            s.WriteByte((byte)(data.Length >> 24)); s.WriteByte((byte)(data.Length >> 16)); s.WriteByte((byte)(data.Length >> 8)); s.WriteByte((byte)data.Length);
            s.Write(t, 0, 4);
            s.Write(data, 0, data.Length);
            uint crc = Crc(Crc(0xFFFFFFFF, t, 0, 4), data, 0, data.Length) ^ 0xFFFFFFFF;
            s.WriteByte((byte)(crc >> 24)); s.WriteByte((byte)(crc >> 16)); s.WriteByte((byte)(crc >> 8)); s.WriteByte((byte)crc);
        }

        static uint[] crcTable;

        static uint Crc(uint crc, byte[] b, int off, int len)
        {
            if (crcTable == null)
            {
                var t = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                    t[n] = c;
                }
                crcTable = t;
            }
            for (int i = off; i < off + len; i++) crc = crcTable[(crc ^ b[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        // ------------------------------------------------------------------ animated WebP

        // The system WebP decoder already returns full composed canvases; only frame durations
        // (ANMF chunks) have to be read from the file. If it ever returns raw sub-frames, they
        // are composed here with the ANMF offsets, blend and dispose flags.
        public static Decoded FromWebp(BitmapDecoder dec, byte[] d)
        {
            var anmf = new List<FramePart>();
            int cw = 0, ch = 0;
            int p = 12;
            while (p + 8 <= d.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(d, p, 4);
                int len = d[p + 4] | (d[p + 5] << 8) | (d[p + 6] << 16) | (d[p + 7] << 24);
                int dp = p + 8;
                if (len < 0 || dp + len > d.Length) break;
                if (id == "VP8X" && len >= 10) { cw = U24(d, dp + 4) + 1; ch = U24(d, dp + 7) + 1; }
                if (id == "ANMF" && len >= 16)
                {
                    int ms = U24(d, dp + 12);
                    anmf.Add(new FramePart
                    {
                        X = 2 * U24(d, dp),
                        Y = 2 * U24(d, dp + 3),
                        Delay = ms < 20 ? 100 : ms,   // same rule browsers use for tiny durations
                        BlendOver = (d[dp + 15] & 2) == 0,
                        Dispose = (d[dp + 15] & 1) != 0 ? 1 : 0
                    });
                }
                p = dp + len + (len & 1);
            }
            int n = dec.Frames.Count;
            if (n < 2 || cw == 0) return null;
            if ((long)cw * ch * n > MaxPixels) return null;

            var first = dec.Frames[0];
            if (first.PixelWidth == cw && first.PixelHeight == ch)
            {
                var frames = new List<BitmapSource>();
                var delays = new List<int>();
                for (int i = 0; i < n; i++)
                {
                    var f = dec.Frames[i];
                    f.Freeze();
                    frames.Add(f);
                    delays.Add(i < anmf.Count ? anmf[i].Delay : 100);
                }
                return new Decoded { Image = frames[0], Frames = frames, Delays = delays, Format = L.T("fmt_webp_anim") };
            }
            for (int i = 0; i < n && i < anmf.Count; i++) anmf[i].Image = dec.Frames[i];
            if (anmf.Count < n) return null;
            return Compose(cw, ch, anmf, L.T("fmt_webp_anim"));
        }

        static int U24(byte[] d, int p) { return d[p] | (d[p + 1] << 8) | (d[p + 2] << 16); }
    }
}
