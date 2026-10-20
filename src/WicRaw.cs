using System;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace PixView
{
    // For DNG, Windows picks its old built-in "DNG Decoder", which returns only the small embedded
    // preview. The camera RAW decoder (Raw Image Extension) develops the full sensor image; it is
    // chosen here explicitly by container format through WIC.
    static class WicRaw
    {
        static readonly Guid CLSID_WICImagingFactory = new Guid("cacaf262-9370-4615-a13b-9f5539da4c0a");
        static readonly Guid GUID_ContainerFormatRaw = new Guid("fe99ce60-f19c-433c-a3ae-00acefa9ca21");
        static readonly Guid GUID_WICPixelFormat32bppBGRA = new Guid("6fddc324-4e03-4bfe-b185-3d77768dc90f");

        public static BitmapSource Decode(string path)
        {
            var factory = (IWICImagingFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_WICImagingFactory));
            IWICStream stream = null;
            IWICBitmapDecoder dec = null;
            IWICBitmapFrameDecode frame = null;
            IWICFormatConverter conv = null;
            try
            {
                factory.CreateStream(out stream);
                stream.InitializeFromFilename(path, 0x80000000 /* GENERIC_READ */);
                var raw = GUID_ContainerFormatRaw;
                factory.CreateDecoder(ref raw, IntPtr.Zero, out dec);
                dec.Initialize(stream, 1 /* WICDecodeMetadataCacheOnLoad */);
                dec.GetFrame(0, out frame);
                factory.CreateFormatConverter(out conv);
                var bgra = GUID_WICPixelFormat32bppBGRA;
                conv.Initialize(frame, ref bgra, 0, IntPtr.Zero, 0.0, 0);
                int w, h;
                conv.GetSize(out w, out h);
                if (w <= 0 || h <= 0 || (long)w * h > 400000000) return null;
                var px = new byte[w * h * 4];
                // pinned buffer: a byte[] in a COM signature would be marshalled as a SAFEARRAY
                var pin = GCHandle.Alloc(px, GCHandleType.Pinned);
                try { conv.CopyPixels(IntPtr.Zero, w * 4, px.Length, pin.AddrOfPinnedObject()); }
                finally { pin.Free(); }
                return Decoders.Bgra(w, h, px);
            }
            finally
            {
                foreach (var o in new object[] { conv, frame, dec, stream, factory })
                    if (o != null) Marshal.ReleaseComObject(o);
            }
        }

        [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IWICImagingFactory
        {
            void CreateDecoderFromFilename(); void CreateDecoderFromStream(); void CreateDecoderFromFileHandle(); void CreateComponentInfo();
            void CreateDecoder(ref Guid containerFormat, IntPtr vendor, out IWICBitmapDecoder decoder);
            void CreateEncoder(); void CreatePalette();
            void CreateFormatConverter(out IWICFormatConverter converter);
            void CreateBitmapScaler(); void CreateBitmapClipper(); void CreateBitmapFlipRotator();
            void CreateStream(out IWICStream stream);
        }

        [ComImport, Guid("135ff860-22b7-4ddf-b0f6-218f4f299a43"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IWICStream
        {
            // IStream: Read, Write, Seek, SetSize, CopyTo, Commit, Revert, LockRegion, UnlockRegion, Stat, Clone
            void S01(); void S02(); void S03(); void S04(); void S05(); void S06(); void S07(); void S08(); void S09(); void S10(); void S11();
            void InitializeFromIStream();
            void InitializeFromFilename([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint access);
        }

        [ComImport, Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IWICBitmapDecoder
        {
            void QueryCapability();
            void Initialize(IWICStream stream, int cacheOptions);
            void GetContainerFormat(); void GetDecoderInfo(); void CopyPalette(); void GetMetadataQueryReader();
            void GetPreview(); void GetColorContexts(); void GetThumbnail(); void GetFrameCount();
            void GetFrame(int index, out IWICBitmapFrameDecode frame);
        }

        [ComImport, Guid("3b16811b-6a43-4ec9-a813-3d930c13b940"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IWICBitmapFrameDecode { }

        [ComImport, Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IWICFormatConverter
        {
            // IWICBitmapSource
            void GetSize(out int width, out int height);
            void GetPixelFormat(); void GetResolution(); void CopyPalette();
            void CopyPixels(IntPtr rect, int stride, int bufferSize, IntPtr buffer);
            // IWICFormatConverter
            void Initialize(IWICBitmapFrameDecode source, ref Guid dstFormat, int dither, IntPtr palette, double alphaThreshold, int paletteType);
        }
    }
}
