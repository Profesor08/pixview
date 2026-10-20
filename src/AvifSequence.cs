using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace PixView
{
    // Animated AVIF ("avis") is an MP4-style track of AV1 frames. The system HEIF codec returns only
    // the still image, so the frames are decoded with Media Foundation (needs the AV1 Video Extension).
    static class AvifSequence
    {
        // ftyp lists the "avis" brand
        public static bool IsSequence(string path)
        {
            var head = new byte[64];
            int n;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                n = fs.Read(head, 0, head.Length);
            if (n < 16 || head[4] != 'f' || head[5] != 't' || head[6] != 'y' || head[7] != 'p') return false;
            int boxLen = Math.Min(n, (head[0] << 24) | (head[1] << 16) | (head[2] << 8) | head[3]);
            for (int i = 8; i + 4 <= boxLen; i += 4)
                if (head[i] == 'a' && head[i + 1] == 'v' && head[i + 2] == 'i' && head[i + 3] == 's') return true;
            return false;
        }

        public static Decoded Decode(string path)
        {
            // Media Foundation's MP4 parser rejects the AVIF brands and the "pict" track handler;
            // a copy relabelled as plain ISO video ("isom", "vide") decodes fine.
            byte[] data = File.ReadAllBytes(path);
            int trackW, trackH;
            PatchForMp4(data, out trackW, out trackH);
            var yuv = ReadColour(data);
            string tmp = Path.Combine(Path.GetTempPath(), "pixview-" + Guid.NewGuid().ToString("N") + ".mp4");
            File.WriteAllBytes(tmp, data);
            try
            {
                // 1) advanced video processing straight to RGB: right colours, but refuses 10/12-bit
                // 2) the decoder's own NV12 output, converted here with the file's colour matrix
                //    (the basic processor turns 10-bit frames green)
                //    (P010 for 10/12-bit, NV12 for 8-bit)
                var result = Run(tmp, MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, MFVideoFormat_RGB32, trackW, trackH, yuv)
                    ?? Run(tmp, Guid.Empty, MFVideoFormat_P010, trackW, trackH, yuv)
                    ?? Run(tmp, Guid.Empty, MFVideoFormat_NV12, trackW, trackH, yuv);
                // decoders sometimes mislabel their output; accept the frames only if the first one
                // matches the still image the system HEIF codec decodes (otherwise show that still)
                return result != null && LooksLike(result.Image, path) ? result : null;
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }

        static Decoded Run(string file, Guid processing, Guid subtype, int trackW, int trackH, YuvMatrix yuv)
        {
            if (MFStartup(0x00020070, 0) < 0) return null;
            IMFByteStream bs = null;
            IMFSourceReader reader = null;
            try
            {
                Check(MFCreateFile(1, 0, 0, file, out bs), "MFCreateFile");
                // the MP4 parser is picked by content type; .avif is not registered for it
                var ctype = MF_BYTESTREAM_CONTENT_TYPE;
                ((IMFAttributes)bs).SetString(ref ctype, "video/mp4");

                IMFAttributes attr;
                Check(MFCreateAttributes(out attr, 1), "MFCreateAttributes");
                if (processing != Guid.Empty) attr.SetUINT32(ref processing, 1);
                Check(MFCreateSourceReaderFromByteStream(bs, attr, out reader), "MFCreateSourceReaderFromByteStream");
                Marshal.ReleaseComObject(attr);

                reader.SetStreamSelection(AllStreams, false);
                reader.SetStreamSelection(FirstVideo, true);
                IMFMediaType want;
                Check(MFCreateMediaType(out want), "MFCreateMediaType");
                var major = MF_MT_MAJOR_TYPE; var video = MFMediaType_Video; var sub = MF_MT_SUBTYPE;
                want.SetGUID(ref major, ref video);
                want.SetGUID(ref sub, ref subtype);
                reader.SetCurrentMediaType(FirstVideo, IntPtr.Zero, want);   // throws if not offered
                Marshal.ReleaseComObject(want);

                IMFMediaType cur;
                reader.GetCurrentMediaType(FirstVideo, out cur);
                long size;
                var fsz = MF_MT_FRAME_SIZE;
                cur.GetUINT64(ref fsz, out size);
                int w = (int)(size >> 32), h = (int)(size & 0xFFFFFFFF);
                bool nv12 = subtype == MFVideoFormat_NV12, p010 = subtype == MFVideoFormat_P010;
                int stride = nv12 ? w : p010 ? w * 2 : w * 4;
                try { int s; var ds = MF_MT_DEFAULT_STRIDE; cur.GetUINT32(ref ds, out s); if (s != 0) stride = s; } catch { }
                Guid actualSub;
                cur.GetGUID(ref sub, out actualSub);
                Marshal.ReleaseComObject(cur);
                if (w <= 0 || h <= 0 || (long)w * h > 100000000) return null;
                // decoder output is padded to even sizes: crop to the track's real size
                int ow = trackW > 0 && trackW <= w ? trackW : w, oh = trackH > 0 && trackH <= h ? trackH : h;

                var frames = new List<BitmapSource>();
                var times = new List<long>();
                long lastDuration = 0;
                while (true)
                {
                    int actual, flags; long ts; IMFSample sample;
                    reader.ReadSample(FirstVideo, 0, out actual, out flags, out ts, out sample);
                    if ((flags & 2) != 0 || (flags & 1) != 0)   // end of stream / error
                    {
                        if (sample != null) Marshal.ReleaseComObject(sample);
                        break;
                    }
                    if (sample == null) continue;
                    try
                    {
                        if ((long)ow * oh * (frames.Count + 1) > Animation.MaxPixels) break;
                        long dur;
                        sample.GetSampleDuration(out dur);
                        lastDuration = dur;
                        IMFMediaBuffer buf;
                        sample.ConvertToContiguousBuffer(out buf);
                        try
                        {
                            IntPtr p; int max, len;
                            buf.Lock(out p, out max, out len);
                            try
                            {
                                var raw = new byte[len];
                                Marshal.Copy(p, raw, 0, len);
                                                var px = nv12 ? YuvToBgra(raw, stride, h, ow, oh, yuv, false)
                                    : p010 ? YuvToBgra(raw, stride, h, ow, oh, yuv, true)
                                    : Rgb32Rows(raw, stride, h, ow, oh);
                                if (px == null) return null;
                                frames.Add(Decoders.Bgra(ow, oh, px));
                                times.Add(ts);
                            }
                            finally { buf.Unlock(); }
                        }
                        finally { Marshal.ReleaseComObject(buf); }
                    }
                    finally { Marshal.ReleaseComObject(sample); }
                }
                if (frames.Count == 0) return null;

                var delays = new List<int>();
                for (int i = 0; i < frames.Count; i++)
                {
                    long d = i + 1 < times.Count ? times[i + 1] - times[i] : lastDuration;
                    int ms = (int)(d / 10000);
                    delays.Add(ms < 10 ? 100 : ms);
                }
                return new Decoded
                {
                    Image = frames[0],
                    Frames = frames.Count > 1 ? frames : null,
                    Delays = delays,
                    Format = L.F("fmt_avif_anim", frames.Count)
                };
            }
            catch
            {
                return null;   // this way is not available; the caller tries the next one
            }
            finally
            {
                if (reader != null) Marshal.ReleaseComObject(reader);
                if (bs != null) Marshal.ReleaseComObject(bs);
                MFShutdown();
            }
        }

        static byte[] Rgb32Rows(byte[] raw, int stride, int h, int ow, int oh)
        {
            var px = new byte[ow * oh * 4];
            int abs = Math.Abs(stride);
            for (int y = 0; y < oh; y++)
            {
                int srcRow = stride > 0 ? y : h - 1 - y;   // negative stride: bottom-up rows
                if ((long)srcRow * abs + ow * 4 > raw.Length) return null;
                Buffer.BlockCopy(raw, srcRow * abs, px, y * ow * 4, ow * 4);
            }
            for (int i = 3; i < px.Length; i += 4) px[i] = 255;   // RGB32 has no alpha
            return px;
        }

        // ------------------------------------------------------------------ YUV -> RGB

        class YuvMatrix
        {
            public double Kr = 0.299, Kb = 0.114;   // BT.601 unless the file says otherwise
            public bool FullRange;
        }

        // colour info from the first "colr"/"nclx" box: matrix coefficients and range
        static YuvMatrix ReadColour(byte[] d)
        {
            var m = new YuvMatrix();
            for (int i = 0; i + 15 <= d.Length; i++)
            {
                if (d[i] != 'c' || d[i + 1] != 'o' || d[i + 2] != 'l' || d[i + 3] != 'r' || d[i + 4] != 'n' || d[i + 5] != 'c' || d[i + 6] != 'l' || d[i + 7] != 'x')
                    continue;
                int matrix = (d[i + 12] << 8) | d[i + 13];
                m.FullRange = (d[i + 14] & 0x80) != 0;
                switch (matrix)
                {
                    case 1: m.Kr = 0.2126; m.Kb = 0.0722; break;   // BT.709
                    case 9: case 10: m.Kr = 0.2627; m.Kb = 0.0593; break;   // BT.2020
                    case 4: m.Kr = 0.30; m.Kb = 0.11; break;       // FCC
                    case 7: m.Kr = 0.212; m.Kb = 0.087; break;     // SMPTE 240M
                }
                break;
            }
            return m;
        }

        // NV12 (8-bit) or P010 (16-bit little-endian words, value in the top bits): Y plane, then
        // interleaved U/V at half resolution
        static byte[] YuvToBgra(byte[] raw, int stride, int h, int ow, int oh, YuvMatrix m, bool wide)
        {
            // anything but exactly the requested layout means the decoder delivered something else
            if (stride <= 0 || (long)stride * h * 3 / 2 != raw.Length) return null;
            double kg = 1 - m.Kr - m.Kb;
            double rv = 2 * (1 - m.Kr), bu = 2 * (1 - m.Kb);
            double gu = 2 * m.Kb * (1 - m.Kb) / kg, gv = 2 * m.Kr * (1 - m.Kr) / kg;
            double ys = m.FullRange ? 1.0 / 255 : 1.0 / 219, yo = m.FullRange ? 0 : 16;
            double cs = m.FullRange ? 1.0 / 255 : 1.0 / 224;
            int uvBase = stride * h;
            var px = new byte[ow * oh * 4];
            for (int y = 0; y < oh; y++)
            {
                int uvRow = uvBase + (y / 2) * stride;
                for (int x = 0; x < ow; x++)
                {
                    double Y, U, V;
                    if (wide)
                    {
                        int yi = y * stride + x * 2, ui = uvRow + (x & ~1) * 2;
                        Y = (((raw[yi] | (raw[yi + 1] << 8)) / 256.0) - yo) * ys;
                        U = (((raw[ui] | (raw[ui + 1] << 8)) / 256.0) - 128) * cs;
                        V = (((raw[ui + 2] | (raw[ui + 3] << 8)) / 256.0) - 128) * cs;
                    }
                    else
                    {
                        Y = (raw[y * stride + x] - yo) * ys;
                        int ui = uvRow + (x & ~1);
                        U = (raw[ui] - 128) * cs;
                        V = (raw[ui + 1] - 128) * cs;
                    }
                    int o = (y * ow + x) * 4;
                    px[o + 2] = Clamp(Y + rv * V);
                    px[o + 1] = Clamp(Y - gu * U - gv * V);
                    px[o] = Clamp(Y + bu * U);
                    px[o + 3] = 255;
                }
            }
            return px;
        }

        // mean colour difference against the system codec's still image, on a coarse grid
        static bool LooksLike(BitmapSource first, string path)
        {
            BitmapSource still;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    still = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            }
            catch { return true; }   // nothing to compare with
            if (still.PixelWidth != first.PixelWidth || still.PixelHeight != first.PixelHeight) return true;
            var a = Pixels(first); var b = Pixels(still);
            long sum = 0, n = 0;
            for (int i = 0; i < a.Length; i += 4 * 7)
            {
                if (b[i + 3] < 128) continue;   // ignore transparent areas (alpha is not decoded here)
                sum += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
                n++;
            }
            return n == 0 || sum / (3.0 * n) < 24;
        }

        static byte[] Pixels(BitmapSource s)
        {
            var c = new FormatConvertedBitmap(s, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var px = new byte[c.PixelWidth * c.PixelHeight * 4];
            c.CopyPixels(px, c.PixelWidth * 4, 0);
            return px;
        }

        static byte Clamp(double v)
        {
            int i = (int)(v * 255 + 0.5);
            return (byte)(i < 0 ? 0 : i > 255 ? 255 : i);
        }
        // ftyp -> isom/iso6, "pict" handlers -> "vide"; returns the first track's display size (tkhd)
        static void PatchForMp4(byte[] d, out int trackW, out int trackH)
        {
            trackW = trackH = 0;
            int ftypLen = (d[0] << 24) | (d[1] << 16) | (d[2] << 8) | d[3];
            Ascii(d, 8, "isom");
            for (int i = 16; i + 4 <= ftypLen && i + 4 <= d.Length; i += 4) Ascii(d, i, "iso6");
            for (int i = 4; i + 16 <= d.Length; i++)
            {
                if (d[i] == 'h' && d[i + 1] == 'd' && d[i + 2] == 'l' && d[i + 3] == 'r'
                    && d[i + 12] == 'p' && d[i + 13] == 'i' && d[i + 14] == 'c' && d[i + 15] == 't')
                    Ascii(d, i + 12, "vide");
                if (trackW == 0 && d[i] == 't' && d[i + 1] == 'k' && d[i + 2] == 'h' && d[i + 3] == 'd')
                {
                    // version 0: width/height (16.16) at +80 from the box type; version 1: +92
                    int off = i + (d[i + 4] == 1 ? 92 : 80);
                    if (off + 8 <= d.Length)
                    {
                        trackW = ((d[off] << 24) | (d[off + 1] << 16) | (d[off + 2] << 8) | d[off + 3]) >> 16;
                        trackH = ((d[off + 4] << 24) | (d[off + 5] << 16) | (d[off + 6] << 8) | d[off + 7]) >> 16;
                    }
                }
            }
        }

        static void Ascii(byte[] d, int at, string s)
        {
            for (int k = 0; k < 4; k++) d[at + k] = (byte)s[k];
        }

        static void Check(int hr, string step)
        {
            if (hr < 0) throw new InvalidOperationException(step + " failed: 0x" + hr.ToString("x8"));
        }

        const int FirstVideo = unchecked((int)0xFFFFFFFC), AllStreams = unchecked((int)0xFFFFFFFE);

        static readonly Guid MF_BYTESTREAM_CONTENT_TYPE = new Guid("fc358289-3cb6-460c-a424-b6681260375a");
        static readonly Guid MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING = new Guid("0f81da2c-b537-4672-a8b2-a681b17307a3");
        static readonly Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        static readonly Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        static readonly Guid MF_MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        static readonly Guid MF_MT_DEFAULT_STRIDE = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        static readonly Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        static readonly Guid MFVideoFormat_RGB32 = new Guid("00000016-0000-0010-8000-00AA00389B71");
        static readonly Guid MFVideoFormat_NV12 = new Guid("3231564E-0000-0010-8000-00AA00389B71");
        static readonly Guid MFVideoFormat_P010 = new Guid("30313050-0000-0010-8000-00AA00389B71");

        [DllImport("mfplat.dll")] static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll")] static extern int MFShutdown();
        [DllImport("mfplat.dll")] static extern int MFCreateAttributes(out IMFAttributes attrs, int size);
        [DllImport("mfplat.dll")] static extern int MFCreateMediaType(out IMFMediaType type);
        [DllImport("mfplat.dll", CharSet = CharSet.Unicode)]
        static extern int MFCreateFile(int access, int open, int flags, string url, out IMFByteStream stream);
        [DllImport("mfreadwrite.dll")]
        static extern int MFCreateSourceReaderFromByteStream(IMFByteStream stream, IMFAttributes attrs, out IMFSourceReader reader);

        // Only the vtable slots that are called are typed; the rest keep their positions.
        [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFAttributes
        {
            void GetItem(); void GetItemType(); void CompareItem(); void Compare();
            void GetUINT32(ref Guid key, out int value);
            void GetUINT64(ref Guid key, out long value);
            void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
            void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem();
            void DeleteItem(); void DeleteAllItems();
            void SetUINT32(ref Guid key, int value);
            void SetUINT64(); void SetDouble();
            void SetGUID(ref Guid key, ref Guid value);
            void SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        }

        [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFMediaType
        {
            void GetItem(); void GetItemType(); void CompareItem(); void Compare();
            void GetUINT32(ref Guid key, out int value);
            void GetUINT64(ref Guid key, out long value);
            void GetDouble();
            void GetGUID(ref Guid key, out Guid value);
            void GetStringLength(); void GetString(); void GetAllocatedString();
            void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem();
            void DeleteItem(); void DeleteAllItems(); void SetUINT32(); void SetUINT64(); void SetDouble();
            void SetGUID(ref Guid key, ref Guid value);
        }

        [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFSample
        {
            // IMFAttributes (30 slots)
            void A01(); void A02(); void A03(); void A04(); void A05(); void A06(); void A07(); void A08(); void A09(); void A10();
            void A11(); void A12(); void A13(); void A14(); void A15(); void A16(); void A17(); void A18(); void A19(); void A20();
            void A21(); void A22(); void A23(); void A24(); void A25(); void A26(); void A27(); void A28(); void A29(); void A30();
            void GetSampleFlags(); void SetSampleFlags();
            void GetSampleTime(out long time); void SetSampleTime();
            void GetSampleDuration(out long duration); void SetSampleDuration();
            void GetBufferCount(); void GetBufferByIndex();
            void ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        }

        [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFMediaBuffer
        {
            void Lock(out IntPtr buffer, out int maxLength, out int currentLength);
            void Unlock();
        }

        [ComImport, Guid("ad4c1b00-4bf7-422f-9175-756693d9130d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFByteStream { }

        [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFSourceReader
        {
            void GetStreamSelection();
            void SetStreamSelection(int stream, [MarshalAs(UnmanagedType.Bool)] bool selected);
            void GetNativeMediaType();
            void GetCurrentMediaType(int stream, out IMFMediaType type);
            void SetCurrentMediaType(int stream, IntPtr reserved, IMFMediaType type);
            void SetCurrentPosition();
            void ReadSample(int stream, int control, out int actualStream, out int flags, out long timestamp, out IMFSample sample);
        }
    }
}
