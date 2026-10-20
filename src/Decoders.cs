using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PixView
{
    class Decoded
    {
        public BitmapSource Image;
        public List<BitmapSource> Frames;   // animation frames, composed to full size; null for stills
        public List<int> Delays;            // ms per frame
        public string Format;
    }

    // Formats Windows has no codec for, decoded in managed code.
    static class Decoders
    {
        public static bool Handles(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".tga": case ".pcx": case ".qoi": case ".hdr":
                case ".pbm": case ".pgm": case ".ppm": case ".pnm": case ".pam":
                case ".emf": case ".wmf": case ".icns":
                    return true;
                default:
                    return false;
            }
        }

        public static Decoded Decode(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".emf" || ext == ".wmf") return Metafiles.Render(path);
            byte[] d = File.ReadAllBytes(path);
            switch (ext)
            {
                case ".tga": return Still(Tga(d), "TGA (Truevision)");
                case ".pcx": return Still(Pcx(d), "PCX (ZSoft)");
                case ".qoi": return Still(Qoi(d), "QOI");
                case ".hdr": return Still(Hdr(d), L.T("fmt_hdr"));
                case ".icns": return Still(Icns(d), L.T("fmt_icns"));
                default: return Still(Pnm(d), "Netpbm (" + ext.Substring(1).ToUpperInvariant() + ")");
            }
        }

        static Decoded Still(BitmapSource b, string format)
        {
            return new Decoded { Image = b, Format = format };
        }

        // ------------------------------------------------------------------ helpers

        static Exception Bad()
        {
            return new InvalidDataException(L.T("err_corrupt"));
        }

        static Exception Unsupported(string what)
        {
            return new NotSupportedException(L.F("err_variant", what));
        }

        static void CheckSize(long w, long h)
        {
            if (w <= 0 || h <= 0 || w > 65535 || h > 65535 || w * h > 400000000)
                throw new InvalidDataException(L.F("err_size", w, h));
        }

        static int U16LE(byte[] d, int p)
        {
            if (p + 1 >= d.Length) throw Bad();
            return d[p] | (d[p + 1] << 8);
        }

        static int U32BE(byte[] d, int p)
        {
            if (p + 3 >= d.Length) throw Bad();
            return (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];
        }

        public static BitmapSource Bgra(int w, int h, byte[] px)
        {
            var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            b.Freeze();
            return b;
        }

        static void Put(byte[] px, int o, int r, int g, int b, int a)
        {
            px[o] = (byte)b; px[o + 1] = (byte)g; px[o + 2] = (byte)r; px[o + 3] = (byte)a;
        }

        // ------------------------------------------------------------------ TGA

        class Cursor
        {
            public byte[] D;
            public int P;
            public int U8() { if (P >= D.Length) throw Bad(); return D[P++]; }
            public int U16() { int v = U16LE(D, P); P += 2; return v; }
        }

        static BitmapSource Tga(byte[] d)
        {
            if (d.Length < 18) throw Bad();
            int idLen = d[0], cmType = d[1], type = d[2];
            int cmFirst = U16LE(d, 3), cmLen = U16LE(d, 5), cmDepth = d[7];
            int w = U16LE(d, 12), h = U16LE(d, 14), bpp = d[16], desc = d[17];
            int kind = type & 7;   // 1 colour-mapped, 2 true colour, 3 greyscale (+8 = RLE)
            bool rle = (type & 8) != 0;
            if (kind < 1 || kind > 3) throw Unsupported(L.F("v_tga_type", type));
            CheckSize(w, h);
            int alphaBits = desc & 15;
            bool topDown = (desc & 0x20) != 0, rightToLeft = (desc & 0x10) != 0;

            var c = new Cursor { D = d, P = 18 + idLen };
            uint[] palette = null;
            if (cmType == 1)
            {
                palette = new uint[cmFirst + cmLen];
                for (int i = 0; i < cmLen; i++) palette[cmFirst + i] = TgaColor(c, cmDepth, true);
            }

            var px = new byte[w * h * 4];
            int n = w * h, idx = 0;
            bool anyAlpha = false;
            while (idx < n)
            {
                int count = 1;
                bool run = false;
                if (rle)
                {
                    int hdr = c.U8();
                    count = (hdr & 0x7F) + 1;
                    run = (hdr & 0x80) != 0;
                }
                uint col = 0;
                for (int k = 0; k < count && idx < n; k++)
                {
                    if (k == 0 || !run)
                    {
                        if (kind == 1)
                        {
                            int i = bpp == 8 ? c.U8() : c.U16();
                            col = palette != null && i < palette.Length ? palette[i] : 0xFF000000;
                        }
                        else if (kind == 3)
                        {
                            int g = c.U8();
                            int a = bpp == 16 ? c.U8() : 255;
                            col = ((uint)a << 24) | ((uint)g << 16) | ((uint)g << 8) | (uint)g;
                        }
                        else col = TgaColor(c, bpp, alphaBits > 0);
                    }
                    int x = idx % w, y = idx / w;
                    if (!topDown) y = h - 1 - y;
                    if (rightToLeft) x = w - 1 - x;
                    int o = (y * w + x) * 4;
                    px[o] = (byte)col; px[o + 1] = (byte)(col >> 8); px[o + 2] = (byte)(col >> 16); px[o + 3] = (byte)(col >> 24);
                    if ((col >> 24) != 0) anyAlpha = true;
                    idx++;
                }
            }
            // many writers leave the alpha channel zeroed: then the picture is meant to be opaque
            if (!anyAlpha) for (int o = 3; o < px.Length; o += 4) px[o] = 255;
            return Bgra(w, h, px);
        }

        static uint TgaColor(Cursor c, int depth, bool useAlpha)
        {
            int r, g, b, a = 255;
            switch (depth)
            {
                case 15:
                case 16:
                    int v = c.U16();
                    r = (v >> 10) & 31; g = (v >> 5) & 31; b = v & 31;
                    r = (r << 3) | (r >> 2); g = (g << 3) | (g >> 2); b = (b << 3) | (b >> 2);
                    if (depth == 16 && useAlpha) a = (v & 0x8000) != 0 ? 255 : 0;
                    break;
                case 24:
                    b = c.U8(); g = c.U8(); r = c.U8();
                    break;
                case 32:
                    b = c.U8(); g = c.U8(); r = c.U8(); a = c.U8();
                    break;
                case 8:
                    r = g = b = c.U8();
                    break;
                default:
                    throw Unsupported(L.F("v_tga_bits", depth));
            }
            return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
        }

        // ------------------------------------------------------------------ PCX

        static BitmapSource Pcx(byte[] d)
        {
            if (d.Length < 128 || d[0] != 0x0A) throw Bad();
            int version = d[1], enc = d[2], bpp = d[3];
            int w = U16LE(d, 8) - U16LE(d, 4) + 1, h = U16LE(d, 10) - U16LE(d, 6) + 1;
            int planes = d[65], bpl = U16LE(d, 66);
            CheckSize(w, h);
            if (planes < 1 || bpl * 8 < w * bpp) throw Bad();
            int lineBytes = planes * bpl;

            var raw = new byte[(long)lineBytes * h];
            int p = 128, o = 0;
            while (o < raw.Length && p < d.Length)
            {
                int b = d[p++];
                if (enc == 1 && (b & 0xC0) == 0xC0)
                {
                    int cnt = b & 0x3F;
                    if (p >= d.Length) break;
                    byte v = d[p++];
                    while (cnt-- > 0 && o < raw.Length) raw[o++] = v;
                }
                else raw[o++] = (byte)b;
            }

            var px = new byte[w * h * 4];
            if (bpp == 8 && planes >= 3)
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int s = y * lineBytes + x;
                        Put(px, (y * w + x) * 4, raw[s], raw[s + bpl], raw[s + 2 * bpl], planes >= 4 ? raw[s + 3 * bpl] : 255);
                    }
            }
            else if (bpp == 8 && planes == 1)
            {
                // 256-colour palette after a 0x0C marker at the end of the file; greyscale without it
                bool pal = d.Length >= 769 + 128 && d[d.Length - 769] == 0x0C;
                int pb = d.Length - 768;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = raw[y * lineBytes + x];
                        if (pal) Put(px, (y * w + x) * 4, d[pb + i * 3], d[pb + i * 3 + 1], d[pb + i * 3 + 2], 255);
                        else Put(px, (y * w + x) * 4, i, i, i, 255);
                    }
            }
            else if (bpp == 1 || bpp == 2 || bpp == 4)
            {
                // planar/packed up to 16 colours, palette in the header (EGA)
                var pal = new int[16, 3];
                for (int i = 0; i < 16; i++) { pal[i, 0] = d[16 + i * 3]; pal[i, 1] = d[17 + i * 3]; pal[i, 2] = d[18 + i * 3]; }
                bool mono = bpp == 1 && planes == 1 &&
                    (pal[0, 0] == pal[1, 0] && pal[0, 1] == pal[1, 1] && pal[0, 2] == pal[1, 2] || version < 2);
                int mask = (1 << bpp) - 1;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int idx = 0;
                        for (int pl = 0; pl < planes; pl++)
                        {
                            int bit = x * bpp;
                            int v = (raw[y * lineBytes + pl * bpl + bit / 8] >> (8 - bpp - bit % 8)) & mask;
                            idx |= v << (pl * bpp);
                        }
                        int oo = (y * w + x) * 4;
                        if (mono) { int g = idx != 0 ? 255 : 0; Put(px, oo, g, g, g, 255); }
                        else if (idx < 16) Put(px, oo, pal[idx, 0], pal[idx, 1], pal[idx, 2], 255);
                        else Put(px, oo, 0, 0, 0, 255);
                    }
            }
            else throw Unsupported(L.F("v_pcx", bpp, planes));
            return Bgra(w, h, px);
        }

        // ------------------------------------------------------------------ Netpbm (PBM/PGM/PPM/PAM)

        class Tokens
        {
            public byte[] D;
            public int P;

            public void SkipSpace()
            {
                while (P < D.Length)
                {
                    byte c = D[P];
                    if (c == '#') { while (P < D.Length && D[P] != '\n' && D[P] != '\r') P++; }
                    else if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\v' || c == '\f') P++;
                    else break;
                }
            }

            public int Int()
            {
                SkipSpace();
                if (P >= D.Length || D[P] < '0' || D[P] > '9') throw Bad();
                long v = 0;
                while (P < D.Length && D[P] >= '0' && D[P] <= '9') { v = v * 10 + (D[P] - '0'); if (v > int.MaxValue) throw Bad(); P++; }
                return (int)v;
            }

            public string Line()
            {
                int s = P;
                while (P < D.Length && D[P] != '\n') P++;
                string line = System.Text.Encoding.ASCII.GetString(D, s, P - s).Trim();
                if (P < D.Length) P++;
                return line;
            }
        }

        static BitmapSource Pnm(byte[] d)
        {
            if (d.Length < 3 || d[0] != 'P') throw Bad();
            int kind = d[1] - '0';
            var t = new Tokens { D = d, P = 2 };
            int w, h, depth, maxval;
            bool pamBlackWhite = false;
            if (kind == 7)
            {
                w = h = depth = maxval = 0;
                t.Line();
                while (true)
                {
                    if (t.P >= d.Length) throw Bad();
                    string line = t.Line();
                    if (line.Length == 0 || line[0] == '#') continue;
                    if (line == "ENDHDR") break;
                    var parts = line.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    string val = parts.Length > 1 ? parts[1].Trim() : "";
                    switch (parts[0])
                    {
                        case "WIDTH": w = int.Parse(val); break;
                        case "HEIGHT": h = int.Parse(val); break;
                        case "DEPTH": depth = int.Parse(val); break;
                        case "MAXVAL": maxval = int.Parse(val); break;
                        case "TUPLTYPE": pamBlackWhite = val.StartsWith("BLACKANDWHITE"); break;
                    }
                }
            }
            else
            {
                if (kind < 1 || kind > 6) throw Unsupported("Netpbm P" + kind);
                w = t.Int();
                h = t.Int();
                maxval = kind == 1 || kind == 4 ? 1 : t.Int();
                depth = kind == 3 || kind == 6 ? 3 : 1;
                if (kind >= 4) t.P++; // exactly one whitespace before binary data
            }
            CheckSize(w, h);
            if (maxval < 1 || maxval > 65535 || depth < 1 || depth > 4) throw Bad();

            var px = new byte[w * h * 4];
            int n = w * h;
            if (kind == 1 || kind == 4)
            {
                // PBM: 1 = black
                int rowBytes = (w + 7) / 8;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int bit;
                        if (kind == 4)
                        {
                            int pos = t.P + y * rowBytes + x / 8;
                            if (pos >= d.Length) throw Bad();
                            bit = (d[pos] >> (7 - x % 8)) & 1;
                        }
                        else
                        {
                            t.SkipSpace();
                            if (t.P >= d.Length) throw Bad();
                            bit = d[t.P++] == '1' ? 1 : 0;
                        }
                        int g = bit == 1 ? 0 : 255;
                        Put(px, (y * w + x) * 4, g, g, g, 255);
                    }
                return Bgra(w, h, px);
            }

            bool ascii = kind == 2 || kind == 3;
            bool wide = maxval > 255;
            var sample = new int[4];
            for (int i = 0; i < n; i++)
            {
                for (int ch = 0; ch < depth; ch++)
                {
                    int v;
                    if (ascii) v = t.Int();
                    else if (wide) { if (t.P + 1 >= d.Length) throw Bad(); v = (d[t.P] << 8) | d[t.P + 1]; t.P += 2; }
                    else { if (t.P >= d.Length) throw Bad(); v = d[t.P++]; }
                    sample[ch] = Math.Min(255, (int)((v * 255L + maxval / 2) / maxval));
                }
                int o = i * 4;
                if (depth >= 3) Put(px, o, sample[0], sample[1], sample[2], depth == 4 ? sample[3] : 255);
                else
                {
                    int g = sample[0];
                    if (pamBlackWhite) g = g != 0 ? 255 : 0;
                    Put(px, o, g, g, g, depth == 2 ? sample[1] : 255);
                }
            }
            return Bgra(w, h, px);
        }

        // ------------------------------------------------------------------ QOI (qoiformat.org)

        static BitmapSource Qoi(byte[] d)
        {
            if (d.Length < 22 || d[0] != 'q' || d[1] != 'o' || d[2] != 'i' || d[3] != 'f') throw Bad();
            long w = (uint)U32BE(d, 4), h = (uint)U32BE(d, 8);
            CheckSize(w, h);
            var px = new byte[w * h * 4];
            var index = new byte[64 * 4];
            int r = 0, g = 0, b = 0, a = 255, run = 0, p = 14;
            int end = d.Length - 8;  // 8-byte end marker
            for (long o = 0; o < px.Length; o += 4)
            {
                if (run > 0) run--;
                else if (p < end)
                {
                    int b1 = d[p++];
                    if (b1 == 0xFE) { r = d[p++]; g = d[p++]; b = d[p++]; }
                    else if (b1 == 0xFF) { r = d[p++]; g = d[p++]; b = d[p++]; a = d[p++]; }
                    else
                    {
                        switch (b1 & 0xC0)
                        {
                            case 0x00:
                                int k = (b1 & 0x3F) * 4;
                                r = index[k]; g = index[k + 1]; b = index[k + 2]; a = index[k + 3];
                                break;
                            case 0x40:
                                r = (r + ((b1 >> 4) & 3) - 2) & 0xFF;
                                g = (g + ((b1 >> 2) & 3) - 2) & 0xFF;
                                b = (b + (b1 & 3) - 2) & 0xFF;
                                break;
                            case 0x80:
                                int b2 = d[p++], vg = (b1 & 0x3F) - 32;
                                r = (r + vg - 8 + ((b2 >> 4) & 0x0F)) & 0xFF;
                                g = (g + vg) & 0xFF;
                                b = (b + vg - 8 + (b2 & 0x0F)) & 0xFF;
                                break;
                            default:
                                run = b1 & 0x3F;
                                break;
                        }
                    }
                    int hsh = ((r * 3 + g * 5 + b * 7 + a * 11) % 64) * 4;
                    index[hsh] = (byte)r; index[hsh + 1] = (byte)g; index[hsh + 2] = (byte)b; index[hsh + 3] = (byte)a;
                }
                px[o] = (byte)b; px[o + 1] = (byte)g; px[o + 2] = (byte)r; px[o + 3] = (byte)a;
            }
            return Bgra((int)w, (int)h, px);
        }

        // ------------------------------------------------------------------ Radiance HDR (RGBE)

        static BitmapSource Hdr(byte[] d)
        {
            var t = new Tokens { D = d, P = 0 };
            string magic = t.Line();
            if (!magic.StartsWith("#?")) throw Bad();
            while (true)
            {
                if (t.P >= d.Length) throw Bad();
                string line = t.Line();
                if (line.Length == 0) break;
                if (line.StartsWith("FORMAT=") && line != "FORMAT=32-bit_rle_rgbe")
                    throw Unsupported("HDR " + line.Substring(7));
            }
            // resolution line, normally "-Y height +X width"
            var res = t.Line().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (res.Length != 4 || res[0].Length != 2 || res[2].Length != 2) throw Bad();
            bool rowsAlongY = res[0][1] == 'Y';
            int n1 = int.Parse(res[1]), n2 = int.Parse(res[3]);
            int w = rowsAlongY ? n2 : n1, h = rowsAlongY ? n1 : n2;
            CheckSize(w, h);
            int scan = n2, lines = n1;

            var rgbe = new byte[(long)scan * lines * 4];
            int p = t.P;
            var line4 = new byte[scan * 4];
            for (int y = 0; y < lines; y++)
            {
                if (scan >= 8 && scan < 0x8000 && p + 4 <= d.Length && d[p] == 2 && d[p + 1] == 2 && ((d[p + 2] << 8) | d[p + 3]) == scan)
                {
                    // new-style RLE: each of the 4 channels run-length coded separately
                    p += 4;
                    for (int ch = 0; ch < 4; ch++)
                    {
                        int x = 0;
                        while (x < scan)
                        {
                            if (p >= d.Length) throw Bad();
                            int cnt = d[p++];
                            if (cnt > 128)
                            {
                                cnt -= 128;
                                if (p >= d.Length || x + cnt > scan) throw Bad();
                                byte v = d[p++];
                                while (cnt-- > 0) line4[(x++) * 4 + ch] = v;
                            }
                            else
                            {
                                if (cnt == 0 || p + cnt > d.Length || x + cnt > scan) throw Bad();
                                while (cnt-- > 0) line4[(x++) * 4 + ch] = d[p++];
                            }
                        }
                    }
                }
                else
                {
                    // flat (optionally old-style run) pixels
                    int x = 0, shift = 0;
                    while (x < scan)
                    {
                        if (p + 4 > d.Length) throw Bad();
                        if (d[p] == 1 && d[p + 1] == 1 && d[p + 2] == 1 && x > 0)
                        {
                            int cnt = d[p + 3] << shift;
                            for (int k = 0; k < cnt && x < scan; k++, x++) Buffer.BlockCopy(line4, (x - 1) * 4, line4, x * 4, 4);
                            shift += 8;
                        }
                        else
                        {
                            Buffer.BlockCopy(d, p, line4, x * 4, 4);
                            x++;
                            shift = 0;
                        }
                        p += 4;
                    }
                }
                Buffer.BlockCopy(line4, 0, rgbe, y * scan * 4, scan * 4);
            }

            // RGBE -> linear float, then Reinhard tone mapping keyed on the log-average luminance
            int n = scan * lines;
            var lin = new float[n * 3];
            double logSum = 0;
            for (int i = 0; i < n; i++)
            {
                int e = rgbe[i * 4 + 3];
                float f = e == 0 ? 0 : (float)Math.Pow(2, e - 136);
                float r = rgbe[i * 4] * f, g = rgbe[i * 4 + 1] * f, b = rgbe[i * 4 + 2] * f;
                lin[i * 3] = r; lin[i * 3 + 1] = g; lin[i * 3 + 2] = b;
                logSum += Math.Log(1e-4 + 0.2126 * r + 0.7152 * g + 0.0722 * b);
            }
            double key = 0.18 / Math.Exp(logSum / n);
            double maxL = 0;
            for (int i = 0; i < n; i++)
                maxL = Math.Max(maxL, key * (0.2126 * lin[i * 3] + 0.7152 * lin[i * 3 + 1] + 0.0722 * lin[i * 3 + 2]));
            double white2 = Math.Max(1, maxL * maxL);

            var px = new byte[n * 4];
            for (int i = 0; i < n; i++)
            {
                double r = lin[i * 3] * key, g = lin[i * 3 + 1] * key, b = lin[i * 3 + 2] * key;
                double L = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                double s = L > 0 ? (1 + L / white2) / (1 + L) : 0;
                // pixel order: rows along the first axis of the resolution line
                int line = i / scan, col = i % scan;
                int x, y;
                if (rowsAlongY) { y = res[0][0] == '-' ? line : lines - 1 - line; x = res[2][0] == '+' ? col : scan - 1 - col; }
                else { x = res[0][0] == '+' ? line : lines - 1 - line; y = res[2][0] == '-' ? col : scan - 1 - col; }
                Put(px, (y * w + x) * 4, Srgb(r * s), Srgb(g * s), Srgb(b * s), 255);
            }
            return Bgra(w, h, px);
        }

        static int Srgb(double v)
        {
            if (v <= 0) return 0;
            if (v >= 1) return 255;
            double s = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
            return (int)(s * 255 + 0.5);
        }

        // ------------------------------------------------------------------ ICNS (Apple icon)

        static BitmapSource Icns(byte[] d)
        {
            if (d.Length < 8 || d[0] != 'i' || d[1] != 'c' || d[2] != 'n' || d[3] != 's') throw Bad();
            var masks = new Dictionary<string, byte[]>();
            var rle = new Dictionary<string, KeyValuePair<int, int>>();   // type -> (offset, length)
            BitmapSource best = null;
            string skipped = null;
            int p = 8;
            while (p + 8 <= d.Length)
            {
                string type = System.Text.Encoding.ASCII.GetString(d, p, 4);
                int len = U32BE(d, p + 4);
                if (len < 8 || p + len > d.Length) break;
                int dp = p + 8, dl = len - 8;
                if (dl >= 8 && d[dp] == 0x89 && d[dp + 1] == 'P' && d[dp + 2] == 'N' && d[dp + 3] == 'G')
                {
                    var bmp = DecodeEmbedded(d, dp, dl);
                    if (bmp != null && (best == null || bmp.PixelWidth > best.PixelWidth)) best = bmp;
                }
                else if (dl >= 12 && d[dp] == 0 && d[dp + 1] == 0 && d[dp + 2] == 0 && d[dp + 3] == 12 && d[dp + 4] == 'j' && d[dp + 5] == 'P')
                {
                    var bmp = DecodeEmbedded(d, dp, dl);   // JPEG 2000: only with an installed codec
                    if (bmp != null && (best == null || bmp.PixelWidth > best.PixelWidth)) best = bmp;
                    else if (bmp == null) skipped = "JPEG 2000";
                }
                else if (type == "it32" || type == "ih32" || type == "il32" || type == "is32")
                    rle[type] = new KeyValuePair<int, int>(dp, dl);
                else if (type == "t8mk" || type == "h8mk" || type == "l8mk" || type == "s8mk")
                {
                    var m = new byte[dl];
                    Buffer.BlockCopy(d, dp, m, 0, dl);
                    masks[type] = m;
                }
                else if ((type == "ic04" || type == "ic05") && dl > 4 && d[dp] == 'A' && d[dp + 1] == 'R' && d[dp + 2] == 'G' && d[dp + 3] == 'B')
                {
                    int sz = type == "ic04" ? 16 : 32;
                    var bmp = IcnsRle(d, dp + 4, dl - 4, sz, 4, null);
                    if (bmp != null && (best == null || sz > best.PixelWidth)) best = bmp;
                }
                p += len;
            }
            // legacy RLE icons, used only if nothing larger exists
            var legacy = new[] { new[] { "it32", "t8mk", "128" }, new[] { "ih32", "h8mk", "48" }, new[] { "il32", "l8mk", "32" }, new[] { "is32", "s8mk", "16" } };
            foreach (var l in legacy)
            {
                int sz = int.Parse(l[2]);
                if (best != null && best.PixelWidth >= sz) break;
                KeyValuePair<int, int> e;
                if (!rle.TryGetValue(l[0], out e)) continue;
                byte[] mask;
                masks.TryGetValue(l[1], out mask);
                int off = l[0] == "it32" ? 4 : 0;   // it32 data starts with 4 zero bytes
                var bmp = IcnsRle(d, e.Key + off, e.Value - off, sz, 3, mask);
                if (bmp != null) { best = bmp; break; }
            }
            if (best == null)
                throw skipped != null
                    ? (Exception)new NotSupportedException(L.T("err_icns_jp2"))
                    : Bad();
            return best;
        }

        static BitmapSource DecodeEmbedded(byte[] d, int offset, int length)
        {
            try
            {
                using (var ms = new MemoryStream(d, offset, length, false))
                {
                    var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    var f = dec.Frames[0];
                    f.Freeze();
                    return f;
                }
            }
            catch { return null; }
        }

        // Apple's PackBits variant; channels stored one after another (A,R,G,B or R,G,B)
        static BitmapSource IcnsRle(byte[] d, int p, int len, int size, int channels, byte[] mask)
        {
            int n = size * size, end = p + len;
            var ch = new byte[channels * n];
            for (int c = 0; c < channels; c++)
            {
                int x = 0;
                while (x < n)
                {
                    if (p >= end) return null;
                    int b = d[p++];
                    if ((b & 0x80) != 0)
                    {
                        int cnt = b - 125;
                        if (p >= end) return null;
                        byte v = d[p++];
                        while (cnt-- > 0 && x < n) ch[c * n + x++] = v;
                    }
                    else
                    {
                        int cnt = b + 1;
                        while (cnt-- > 0 && x < n) { if (p >= end) return null; ch[c * n + x++] = d[p++]; }
                    }
                }
            }
            var px = new byte[n * 4];
            for (int i = 0; i < n; i++)
            {
                if (channels == 4) Put(px, i * 4, ch[n + i], ch[2 * n + i], ch[3 * n + i], ch[i]);
                else Put(px, i * 4, ch[i], ch[n + i], ch[2 * n + i], mask != null && mask.Length >= n ? mask[i] : 255);
            }
            return Bgra(size, size, px);
        }
    }
}
