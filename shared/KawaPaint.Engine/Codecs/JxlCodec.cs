// KawaPaint - JPEG XL via a hand-rolled binding to libjxl.
//
// Spiked and measured 2026-08-19 against pulling in Magick.NET instead: Magick.NET's native blob
// is 20-38MB per platform for JXL+JP2 riding along with ~270 unused formats. Binding libjxl's own
// C API directly costs ~6.7MB total (libjxl + libjxl_cms + libhwy + brotli), scoped to exactly
// what JXL needs - chosen for that footprint, not for lack of alternatives.
//
// Windows x64 and macOS arm64 heads bundle the native dependency closure; other targets degrade
// gracefully and expose this codec only when libjxl is installed on the system.
//
// libjxl has no BGR(A) pixel format (see the TODO comment in jxl/types.h), only RGB(A), so every
// encode/decode does one extra channel swizzle against Surface's native BGRA layout.

using System.Runtime.InteropServices;

namespace KawaPaint.Engine.Codecs;

public sealed partial class JxlCodec : IImageCodec
{
    public string Id => "jxl";
    public string DisplayName => "JPEG XL";
    public IReadOnlyList<string> Extensions { get; } = new[] { ".jxl" };

    public bool CanDecode => true;
    public bool CanEncode => true;

    private bool? _available;
    public bool IsAvailable => _available ??= Probe();

    private static bool Probe()
    {
        try { Native.JxlDecoderVersion(); return true; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    public unsafe bool MatchesHeader(ReadOnlySpan<byte> header)
    {
        if (!IsAvailable) return false;
        fixed (byte* p = header)
        {
            var sig = Native.JxlSignatureCheck(p, (nuint)header.Length);
            return sig is Native.JxlSignature.Codestream or Native.JxlSignature.Container;
        }
    }

    public unsafe Surface Decode(Stream stream)
    {
        if (!IsAvailable) throw new CodecUnavailableException(Id, "libjxl was not found on this system.");

        using var buffered = new MemoryStream();
        stream.CopyTo(buffered);
        byte[] jxlBytes = buffered.ToArray();

        IntPtr dec = Native.JxlDecoderCreate(IntPtr.Zero);
        if (dec == IntPtr.Zero) throw new InvalidOperationException("JxlDecoderCreate failed.");

        Surface? surface = null;
        byte* rgba = null;
        try
        {
            if (Native.JxlDecoderSubscribeEvents(dec, Native.JXL_DEC_BASIC_INFO | Native.JXL_DEC_FULL_IMAGE) != Native.JXL_DEC_SUCCESS)
                throw new InvalidOperationException("JxlDecoderSubscribeEvents failed.");

            fixed (byte* data = jxlBytes)
            {
                if (Native.JxlDecoderSetInput(dec, data, (nuint)jxlBytes.Length) != Native.JXL_DEC_SUCCESS)
                    throw new InvalidOperationException("JxlDecoderSetInput failed.");
                Native.JxlDecoderCloseInput(dec);

                var info = new Native.JxlBasicInfo();
                var format = Native.JxlPixelFormat.Rgba8;

                while (true)
                {
                    int status = Native.JxlDecoderProcessInput(dec);
                    if (status == Native.JXL_DEC_ERROR)
                        throw new InvalidOperationException("Corrupt or unsupported JPEG XL stream.");
                    if (status == Native.JXL_DEC_NEED_MORE_INPUT)
                        throw new InvalidOperationException("Truncated JPEG XL stream.");

                    if (status == Native.JXL_DEC_BASIC_INFO)
                    {
                        if (Native.JxlDecoderGetBasicInfo(dec, ref info) != Native.JXL_DEC_SUCCESS)
                            throw new InvalidOperationException("JxlDecoderGetBasicInfo failed.");
                        surface = new Surface((int)info.xsize, (int)info.ysize);
                    }
                    else if (status == Native.JXL_DEC_NEED_IMAGE_OUT_BUFFER)
                    {
                        if (Native.JxlDecoderImageOutBufferSize(dec, ref format, out nuint size) != Native.JXL_DEC_SUCCESS)
                            throw new InvalidOperationException("JxlDecoderImageOutBufferSize failed.");
                        rgba = (byte*)NativeMemory.Alloc(size);
                        if (Native.JxlDecoderSetImageOutBuffer(dec, ref format, rgba, size) != Native.JXL_DEC_SUCCESS)
                            throw new InvalidOperationException("JxlDecoderSetImageOutBuffer failed.");
                    }
                    else if (status == Native.JXL_DEC_SUCCESS)
                    {
                        break;
                    }
                }
            }

            if (surface is null || rgba is null)
                throw new InvalidOperationException("Decoder never produced image data.");

            Swizzle(rgba, (byte*)surface.Scan0, surface.Width * surface.Height);
            return surface;
        }
        catch
        {
            surface?.Dispose();
            throw;
        }
        finally
        {
            if (rgba != null) NativeMemory.Free(rgba);
            Native.JxlDecoderDestroy(dec);
        }
    }

    public unsafe void Encode(Surface surface, Stream stream, EncodeOptions options)
    {
        if (!IsAvailable) throw new CodecUnavailableException(Id, "libjxl was not found on this system.");

        int pixelCount = surface.Width * surface.Height;
        byte* rgba = (byte*)NativeMemory.Alloc((nuint)(pixelCount * 4));
        IntPtr enc = IntPtr.Zero;
        try
        {
            Swizzle((byte*)surface.Scan0, rgba, pixelCount);

            enc = Native.JxlEncoderCreate(IntPtr.Zero);
            if (enc == IntPtr.Zero) throw new InvalidOperationException("JxlEncoderCreate failed.");

            Native.JxlEncoderUseContainer(enc, 1); // so other tools see a standard boxed .jxl, not a bare codestream

            var info = new Native.JxlBasicInfo();
            Native.JxlEncoderInitBasicInfo(ref info);
            info.xsize = (uint)surface.Width;
            info.ysize = (uint)surface.Height;
            info.bits_per_sample = 8;
            info.num_color_channels = 3;
            info.alpha_bits = 8;
            info.num_extra_channels = 1;
            // Original (non-XYB) profile is right for lossless; XYB gives better ratios for lossy.
            info.uses_original_profile = options.Lossless ? 1 : 0;

            if (Native.JxlEncoderSetBasicInfo(enc, ref info) != Native.JXL_ENC_SUCCESS)
                throw new InvalidOperationException("JxlEncoderSetBasicInfo failed.");

            IntPtr settings = Native.JxlEncoderFrameSettingsCreate(enc, IntPtr.Zero);

            if (options.Lossless)
            {
                if (Native.JxlEncoderSetFrameLossless(settings, 1) != Native.JXL_ENC_SUCCESS)
                    throw new InvalidOperationException("JxlEncoderSetFrameLossless failed.");
            }
            else
            {
                float distance = Native.JxlEncoderDistanceFromQuality(Math.Clamp(options.Quality, 1, 100));
                if (Native.JxlEncoderSetFrameDistance(settings, distance) != Native.JXL_ENC_SUCCESS)
                    throw new InvalidOperationException("JxlEncoderSetFrameDistance failed.");
            }

            var format = Native.JxlPixelFormat.Rgba8;
            if (Native.JxlEncoderAddImageFrame(settings, ref format, rgba, (nuint)(pixelCount * 4)) != Native.JXL_ENC_SUCCESS)
                throw new InvalidOperationException("JxlEncoderAddImageFrame failed.");
            Native.JxlEncoderCloseInput(enc);

            byte[] outBuf = new byte[256 * 1024];
            int status;
            do
            {
                fixed (byte* outPtr = outBuf)
                {
                    byte* next = outPtr;
                    nuint avail = (nuint)outBuf.Length;
                    status = Native.JxlEncoderProcessOutput(enc, &next, &avail);
                    int written = (int)((nuint)outBuf.Length - avail);
                    stream.Write(outBuf, 0, written);
                }
            } while (status == Native.JXL_ENC_NEED_MORE_OUTPUT);

            if (status != Native.JXL_ENC_SUCCESS)
                throw new InvalidOperationException($"JxlEncoderProcessOutput failed: {status}");
        }
        finally
        {
            if (enc != IntPtr.Zero) Native.JxlEncoderDestroy(enc);
            NativeMemory.Free(rgba);
        }
    }

    /// <summary>RGBA &lt;-&gt; BGRA: swapping R and B is its own inverse, so one routine serves both directions.</summary>
    private static unsafe void Swizzle(byte* src, byte* dst, int pixelCount)
    {
        for (int i = 0; i < pixelCount; i++)
        {
            int o = i * 4;
            byte r = src[o + 0], g = src[o + 1], b = src[o + 2], a = src[o + 3];
            dst[o + 0] = b;
            dst[o + 1] = g;
            dst[o + 2] = r;
            dst[o + 3] = a;
        }
    }

    /// <summary>Bindings against libjxl's C API (jxl/decode.h, jxl/encode.h, jxl/codestream_header.h, jxl/types.h).</summary>
    private static partial class Native
    {
        private const string Lib = "jxl";

        public enum JxlSignature
        {
            NotEnoughBytes = 0,
            Invalid = 1,
            Codestream = 2,
            Container = 3,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JxlPreviewHeader
        {
            public uint xsize, ysize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JxlAnimationHeader
        {
            public uint tps_numerator, tps_denominator, num_loops;
            public int have_timecodes;
        }

        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct JxlBasicInfo
        {
            public int have_container;
            public uint xsize, ysize;
            public uint bits_per_sample, exponent_bits_per_sample;
            public float intensity_target, min_nits;
            public int relative_to_max_display;
            public float linear_below;
            public int uses_original_profile;
            public int have_preview;
            public int have_animation;
            public int orientation;
            public uint num_color_channels, num_extra_channels;
            public uint alpha_bits, alpha_exponent_bits;
            public int alpha_premultiplied;
            public JxlPreviewHeader preview;
            public JxlAnimationHeader animation;
            public uint intrinsic_xsize, intrinsic_ysize;
            // fixed buffer, not byte[]: LibraryImport's source generator only marshals blittable
            // structs and a MarshalAs array field isn't blittable.
            public unsafe fixed byte padding[100];
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JxlPixelFormat
        {
            public uint num_channels;
            public int data_type; // JxlDataType
            public int endianness; // JxlEndianness
            public nuint align;

            public static JxlPixelFormat Rgba8 => new() { num_channels = 4, data_type = JXL_TYPE_UINT8, endianness = 0, align = 0 };
        }

        public const int JXL_TYPE_UINT8 = 2;

        public const int JXL_DEC_SUCCESS = 0;
        public const int JXL_DEC_ERROR = 1;
        public const int JXL_DEC_NEED_MORE_INPUT = 2;
        public const int JXL_DEC_NEED_IMAGE_OUT_BUFFER = 5;
        public const int JXL_DEC_BASIC_INFO = 0x40;
        public const int JXL_DEC_FULL_IMAGE = 0x1000;

        public const int JXL_ENC_SUCCESS = 0;
        public const int JXL_ENC_NEED_MORE_OUTPUT = 2;

        [LibraryImport(Lib)] public static partial uint JxlDecoderVersion();
        [LibraryImport(Lib)] public static unsafe partial JxlSignature JxlSignatureCheck(byte* buf, nuint len);

        [LibraryImport(Lib)] public static partial IntPtr JxlEncoderCreate(IntPtr memoryManager);
        [LibraryImport(Lib)] public static partial void JxlEncoderDestroy(IntPtr enc);
        [LibraryImport(Lib)] public static partial IntPtr JxlEncoderFrameSettingsCreate(IntPtr enc, IntPtr source);
        [LibraryImport(Lib)] public static partial void JxlEncoderInitBasicInfo(ref JxlBasicInfo info);
        [LibraryImport(Lib)] public static partial int JxlEncoderSetBasicInfo(IntPtr enc, ref JxlBasicInfo info);
        [LibraryImport(Lib)] public static partial int JxlEncoderSetFrameLossless(IntPtr frameSettings, int lossless);
        [LibraryImport(Lib)] public static partial int JxlEncoderSetFrameDistance(IntPtr frameSettings, float distance);
        [LibraryImport(Lib)] public static partial float JxlEncoderDistanceFromQuality(float quality);
        [LibraryImport(Lib)] public static partial int JxlEncoderUseContainer(IntPtr enc, int useContainer);
        [LibraryImport(Lib)]
        public static unsafe partial int JxlEncoderAddImageFrame(IntPtr frameSettings, ref JxlPixelFormat format, byte* buffer, nuint size);
        [LibraryImport(Lib)] public static partial void JxlEncoderCloseInput(IntPtr enc);
        [LibraryImport(Lib)]
        public static unsafe partial int JxlEncoderProcessOutput(IntPtr enc, byte** nextOut, nuint* availOut);

        [LibraryImport(Lib)] public static partial IntPtr JxlDecoderCreate(IntPtr memoryManager);
        [LibraryImport(Lib)] public static partial void JxlDecoderDestroy(IntPtr dec);
        [LibraryImport(Lib)] public static partial int JxlDecoderSubscribeEvents(IntPtr dec, int eventsWanted);
        [LibraryImport(Lib)]
        public static unsafe partial int JxlDecoderSetInput(IntPtr dec, byte* data, nuint size);
        [LibraryImport(Lib)] public static partial void JxlDecoderCloseInput(IntPtr dec);
        [LibraryImport(Lib)] public static partial int JxlDecoderProcessInput(IntPtr dec);
        [LibraryImport(Lib)] public static partial int JxlDecoderGetBasicInfo(IntPtr dec, ref JxlBasicInfo info);
        [LibraryImport(Lib)]
        public static partial int JxlDecoderImageOutBufferSize(IntPtr dec, ref JxlPixelFormat format, out nuint size);
        [LibraryImport(Lib)]
        public static unsafe partial int JxlDecoderSetImageOutBuffer(IntPtr dec, ref JxlPixelFormat format, byte* buffer, nuint size);
    }
}
