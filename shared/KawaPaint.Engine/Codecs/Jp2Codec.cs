// KawaPaint — JPEG 2000 (JP2) via a hand-rolled binding to the system libopenjp2, following the
// same pattern as JxlCodec: no bundled native dependency, IsAvailable degrades to false when the
// library isn't installed. See TODO.md's 3.x spike entry for the size rationale (this and JXL
// together cost ~6.7MB total versus Magick.NET's 20-38MB single blob covering both plus ~270
// unused formats).
//
// The struct layouts below (OpjCParameters especially, at ~18.7KB with over 100 fields) were
// hand-transliterated from openjpeg-2.5/openjpeg.h field-by-field, in declaration order, using
// the standard C ABI alignment rules (no #pragma pack in the real header, confirmed by grep) --
// the same technique already used for JxlBasicInfo's padding in JxlCodec.cs. No C compiler was
// available to check the layout mechanically via offsetof(), so it was instead cross-verified at
// runtime against the real openjp2.dll (v2.5.4, official Windows x64 release): calling
// opj_set_default_encoder_parameters and reading back fields whose defaults are explicitly
// documented on that function (Lossless / 1 tile / 64x64 code-block / 6 resolutions / LRCP / no
// ROI) -- if the layout were wrong, at least one of those would not match, and all of them did.
// On top of that, real encode/decode round trips were verified against openjpeg's own
// opj_compress/opj_decompress CLI tools (both directions: our encoder's output decoded correctly
// by the real tool, and our decoder reading the real tool's output byte-exact), plus a lossless
// byte-exact round trip through this binding alone, including down to 1x1 images. See the
// scratchpad jp2spike spike for the harness; not committed, this is the production result of it.
//
// Two structural differences from JXL's simpler buffer-in/buffer-out API drove most of the
// complexity here: openjpeg has no direct memory-buffer API, so reading/writing means wiring an
// opj_stream_t to UnmanagedCallersOnly callbacks against an in-memory buffer; and opj_image_t
// holds one separate OPJ_INT32* plane per component rather than a single interleaved buffer, so
// encode/decode each gather/scatter 4 planes against Surface's interleaved BGRA layout (folding in
// the same R/B swizzle JxlCodec needs, since OPJ_CLRSPC_SRGB implies component order R,G,B,[A]).

using System.Runtime.InteropServices;

namespace KawaPaint.Engine.Codecs;

public sealed partial class Jp2Codec : IImageCodec
{
    public string Id => "jp2";
    public string DisplayName => "JPEG 2000";
    public IReadOnlyList<string> Extensions { get; } = new[] { ".jp2" };

    public bool CanDecode => true;
    public bool CanEncode => true;

    private bool? _available;
    public bool IsAvailable => _available ??= Probe();

    private static bool Probe()
    {
        try { Native.opj_version(); return true; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static readonly byte[] Jp2Signature =
        { 0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A };

    public bool MatchesHeader(ReadOnlySpan<byte> header)
        => IsAvailable && header.Length >= Jp2Signature.Length
           && header[..Jp2Signature.Length].SequenceEqual(Jp2Signature);

    // openjpeg requires 2^(numresolution-1) <= min(width,height); the default of 6 fails outright
    // ("Number of resolutions is too high in comparison to the size of tiles") on anything smaller
    // than 32px on its short side, which includes ordinary icon-sized images.
    private static int ClampResolutions(int width, int height)
    {
        int minDim = Math.Min(width, height);
        int res = 1;
        while (res < 6 && (1 << res) <= minDim) res++;
        return res;
    }

    public unsafe Surface Decode(Stream stream)
    {
        if (!IsAvailable) throw new CodecUnavailableException(Id, "libopenjp2 was not found on this system.");

        using var buffered = new MemoryStream();
        stream.CopyTo(buffered);
        byte[] jp2Bytes = buffered.ToArray();

        IntPtr dec = Native.opj_create_decompress(Native.OPJ_CODEC_JP2);
        if (dec == IntPtr.Zero) throw new InvalidOperationException("opj_create_decompress failed.");

        IntPtr streamHandle = IntPtr.Zero;
        Native.OpjImage* image = null;
        GCHandle stateHandle = default;
        Surface? surface = null;
        try
        {
            var dparams = new Native.OpjDParameters();
            Native.opj_set_default_decoder_parameters(&dparams);
            if (Native.opj_setup_decoder(dec, &dparams) == 0)
                throw new InvalidOperationException("opj_setup_decoder failed.");

            var state = new Native.StreamState { Data = jp2Bytes };
            stateHandle = GCHandle.Alloc(state);

            streamHandle = Native.opj_stream_create(1 << 16, isInput: 1);
            if (streamHandle == IntPtr.Zero) throw new InvalidOperationException("opj_stream_create failed.");
            Native.opj_stream_set_read_function(streamHandle, &Native.ReadFn);
            Native.opj_stream_set_skip_function(streamHandle, &Native.SkipFn);
            Native.opj_stream_set_seek_function(streamHandle, &Native.SeekFn);
            Native.opj_stream_set_user_data(streamHandle, (void*)(IntPtr)stateHandle, null);
            Native.opj_stream_set_user_data_length(streamHandle, (ulong)jp2Bytes.Length);

            IntPtr imagePtr;
            if (Native.opj_read_header(streamHandle, dec, &imagePtr) == 0)
                throw new InvalidOperationException("Corrupt or unsupported JPEG 2000 stream (header).");
            image = (Native.OpjImage*)imagePtr;

            if (Native.opj_decode(dec, streamHandle, imagePtr) == 0)
                throw new InvalidOperationException("Corrupt or unsupported JPEG 2000 stream (decode).");
            Native.opj_end_decompress(dec, streamHandle);

            int width = (int)(image->x1 - image->x0);
            int height = (int)(image->y1 - image->y0);
            int numComps = (int)image->numcomps;
            if (width <= 0 || height <= 0 || numComps < 1)
                throw new InvalidOperationException("JPEG 2000 image has invalid dimensions or no components.");

            surface = new Surface(width, height);
            byte* dst = (byte*)surface.Scan0;

            int pixelCount = width * height;
            static byte Sample(Native.OpjImageComp comp, int p)
            {
                int shift = comp.prec > 8 ? (int)comp.prec - 8 : 0;
                int v = shift > 0 ? comp.data[p] >> shift : comp.data[p];
                return (byte)Math.Clamp(v, 0, 255);
            }

            if (numComps < 3)
            {
                // Grayscale (numComps 1) or grayscale+alpha (numComps 2): replicate the luma
                // plane across R,G,B.
                var luma = image->comps[0];
                for (int p = 0; p < pixelCount; p++)
                {
                    byte v = Sample(luma, p);
                    dst[p * 4 + 0] = v; dst[p * 4 + 1] = v; dst[p * 4 + 2] = v;
                }
            }
            else
            {
                // Component order is R,G,B,[A] per OPJ_CLRSPC_SRGB; Surface is BGRA, so this both
                // gathers the planes into an interleaved buffer and does the R/B swap in one pass.
                Span<int> dstOffset = stackalloc[] { 2, 1, 0 };
                for (int c = 0; c < 3; c++)
                {
                    var comp = image->comps[c];
                    int off = dstOffset[c];
                    for (int p = 0; p < pixelCount; p++)
                        dst[p * 4 + off] = Sample(comp, p);
                }
            }

            if (numComps == 2 || numComps >= 4)
            {
                var alpha = image->comps[numComps == 2 ? 1 : 3];
                for (int p = 0; p < pixelCount; p++) dst[p * 4 + 3] = Sample(alpha, p);
            }
            else
            {
                for (int p = 0; p < pixelCount; p++) dst[p * 4 + 3] = 255;
            }

            return surface;
        }
        catch
        {
            surface?.Dispose();
            throw;
        }
        finally
        {
            if (streamHandle != IntPtr.Zero) Native.opj_stream_destroy(streamHandle);
            Native.opj_destroy_codec(dec);
            if (image != null) Native.opj_image_destroy(image);
            if (stateHandle.IsAllocated) stateHandle.Free();
        }
    }

    public unsafe void Encode(Surface surface, Stream stream, EncodeOptions options)
    {
        if (!IsAvailable) throw new CodecUnavailableException(Id, "libopenjp2 was not found on this system.");

        int width = surface.Width, height = surface.Height;
        IntPtr enc = Native.opj_create_compress(Native.OPJ_CODEC_JP2);
        if (enc == IntPtr.Zero) throw new InvalidOperationException("opj_create_compress failed.");

        IntPtr image = IntPtr.Zero;
        IntPtr streamHandle = IntPtr.Zero;
        GCHandle stateHandle = default;
        try
        {
            var cmptparms = stackalloc Native.OpjImageCmptParm[4];
            for (int c = 0; c < 4; c++)
            {
                cmptparms[c].dx = 1;
                cmptparms[c].dy = 1;
                cmptparms[c].w = (uint)width;
                cmptparms[c].h = (uint)height;
                cmptparms[c].prec = 8;
                cmptparms[c].bpp = 8;
            }
            image = (IntPtr)Native.opj_image_create(4, cmptparms, Native.OPJ_CLRSPC_SRGB);
            if (image == IntPtr.Zero) throw new InvalidOperationException("opj_image_create failed.");

            var img = (Native.OpjImage*)image;
            img->x0 = 0; img->y0 = 0; img->x1 = (uint)width; img->y1 = (uint)height;

            byte* src = (byte*)surface.Scan0;
            int pixelCount = width * height;
            for (int c = 0; c < 4; c++)
            {
                int srcOffset = c switch { 0 => 2, 1 => 1, 2 => 0, 3 => 3, _ => 0 }; // R,G,B,A <- BGRA
                int* data = img->comps[c].data;
                for (int p = 0; p < pixelCount; p++)
                    data[p] = src[p * 4 + srcOffset];
            }

            var cparams = new Native.OpjCParameters();
            Native.opj_set_default_encoder_parameters(&cparams);
            cparams.numresolution = ClampResolutions(width, height);
            if (!options.Lossless)
            {
                cparams.irreversible = 1;
                cparams.cp_disto_alloc = 1;
                cparams.tcp_numlayers = 1;
                // No standard JP2 "quality" scale exists (unlike JPEG's IJG scale or JXL's
                // butteraugli distance) -- this maps EncodeOptions.Quality onto a compression
                // ratio, a documented judgment call rather than a perceptual calibration.
                cparams.tcp_rates[0] = Math.Clamp(101f - options.Quality, 1f, 100f);
            }

            if (Native.opj_setup_encoder(enc, &cparams, image) == 0)
                throw new InvalidOperationException("opj_setup_encoder failed.");

            var state = new Native.StreamState { Data = Array.Empty<byte>() };
            var buffer = new MemoryStream();
            stateHandle = GCHandle.Alloc(state);

            streamHandle = Native.opj_stream_create(1 << 16, isInput: 0);
            if (streamHandle == IntPtr.Zero) throw new InvalidOperationException("opj_stream_create failed.");
            Native.opj_stream_set_write_function(streamHandle, &Native.WriteFn);
            Native.opj_stream_set_skip_function(streamHandle, &Native.SkipFn);
            Native.opj_stream_set_seek_function(streamHandle, &Native.SeekFn);
            Native.opj_stream_set_user_data(streamHandle, (void*)(IntPtr)stateHandle, null);
            state.Output = buffer;

            if (Native.opj_start_compress(enc, image, streamHandle) == 0)
                throw new InvalidOperationException("opj_start_compress failed.");
            if (Native.opj_encode(enc, streamHandle) == 0)
                throw new InvalidOperationException("opj_encode failed.");
            if (Native.opj_end_compress(enc, streamHandle) == 0)
                throw new InvalidOperationException("opj_end_compress failed.");

            buffer.Position = 0;
            buffer.CopyTo(stream);
        }
        finally
        {
            if (streamHandle != IntPtr.Zero) Native.opj_stream_destroy(streamHandle);
            Native.opj_destroy_codec(enc);
            if (image != IntPtr.Zero) Native.opj_image_destroy((Native.OpjImage*)image);
            if (stateHandle.IsAllocated) stateHandle.Free();
        }
    }

    /// <summary>Bindings against libopenjp2's C API (openjpeg-2.5/openjpeg.h).</summary>
    private static unsafe partial class Native
    {
        private const string Lib = "openjp2";

        public const int OPJ_CODEC_JP2 = 2;
        public const int OPJ_CLRSPC_SRGB = 1;

        [StructLayout(LayoutKind.Sequential)]
        public struct OpjImageCmptParm
        {
            public uint dx, dy, w, h, x0, y0, prec, bpp, sgnd;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct OpjImageComp
        {
            public uint dx, dy, w, h, x0, y0, prec, bpp, sgnd, resno_decoded, factor;
            public int* data;
            public ushort alpha;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct OpjImage
        {
            public uint x0, y0, x1, y1;
            public uint numcomps;
            public int color_space;
            public OpjImageComp* comps;
            public byte* icc_profile_buf;
            public uint icc_profile_len;
        }

        // Hand-computed from openjpeg-2.5/openjpeg.h (see the file-level comment for how this was
        // verified at runtime with no C compiler available). POC[32] (opj_poc_t, 148 bytes each) is
        // raw padding -- nothing here ever reads or writes progression-order-change data, it just
        // needs to occupy the right number of bytes so every field after it lands at the right offset.
        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct OpjCParameters
        {
            public int tile_size_on;
            public int cp_tx0, cp_ty0, cp_tdx, cp_tdy;
            public int cp_disto_alloc, cp_fixed_alloc, cp_fixed_quality;
            public IntPtr cp_matrice;
            public IntPtr cp_comment;
            public int csty;
            public int prog_order;
            public fixed byte POC[32 * 148];
            public uint numpocs;
            public int tcp_numlayers;
            public fixed float tcp_rates[100];
            public fixed float tcp_distoratio[100];
            public int numresolution;
            public int cblockw_init;
            public int cblockh_init;
            public int mode;
            public int irreversible;
            public int roi_compno;
            public int roi_shift;
            public int res_spec;
            public fixed int prcw_init[33];
            public fixed int prch_init[33];
            public fixed byte infile[4096];
            public fixed byte outfile[4096];
            public int index_on;
            public fixed byte index[4096];
            public int image_offset_x0, image_offset_y0;
            public int subsampling_dx, subsampling_dy;
            public int decod_format, cod_format;
            public int jpwl_epc_on;
            public int jpwl_hprot_MH;
            public fixed int jpwl_hprot_TPH_tileno[16];
            public fixed int jpwl_hprot_TPH[16];
            public fixed int jpwl_pprot_tileno[16];
            public fixed int jpwl_pprot_packno[16];
            public fixed int jpwl_pprot[16];
            public int jpwl_sens_size, jpwl_sens_addr, jpwl_sens_range, jpwl_sens_MH;
            public fixed int jpwl_sens_TPH_tileno[16];
            public fixed int jpwl_sens_TPH[16];
            public int cp_cinema;
            public int max_comp_size;
            public int cp_rsiz;
            public byte tp_on, tp_flag, tcp_mct;
            public int jpip_on;
            public IntPtr mct_data;
            public int max_cs_size;
            public ushort rsiz;
            public fixed byte _tailSlop[512]; // defense-in-depth against any small offset miscalculation above
        }

        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct OpjDParameters
        {
            public uint cp_reduce, cp_layer;
            public fixed byte infile[4096];
            public fixed byte outfile[4096];
            public int decod_format, cod_format;
            public uint DA_x0, DA_x1, DA_y0, DA_y1;
            public int m_verbose;
            public uint tile_index, nb_tile_to_decode;
            public int jpwl_correct;
            public int jpwl_exp_comps, jpwl_max_tiles;
            public uint flags;
            public fixed byte _tailSlop[256];
        }

        /// <summary>
        /// Backs both directions of the opj_stream_t callbacks. Decode reads sequentially out of
        /// <see cref="Data"/>; encode ignores it and appends into <see cref="Output"/> instead
        /// (openjpeg seeks backward mid-write to patch box-length headers, which a plain
        /// growable byte buffer can't do in place -- a MemoryStream can).
        /// </summary>
        public sealed class StreamState
        {
            public required byte[] Data;
            public long Position;
            public MemoryStream? Output;
        }

        [UnmanagedCallersOnly]
        public static nuint ReadFn(byte* buffer, nuint size, void* userData)
        {
            var state = (StreamState)GCHandle.FromIntPtr((IntPtr)userData).Target!;
            long remaining = state.Data.LongLength - state.Position;
            if (remaining <= 0) return unchecked((nuint)(-1)); // OPJ_SIZE_T -1 signals EOF
            int n = (int)Math.Min((long)size, remaining);
            new ReadOnlySpan<byte>(state.Data, (int)state.Position, n).CopyTo(new Span<byte>(buffer, n));
            state.Position += n;
            return (nuint)n;
        }

        [UnmanagedCallersOnly]
        public static nuint WriteFn(byte* buffer, nuint size, void* userData)
        {
            var state = (StreamState)GCHandle.FromIntPtr((IntPtr)userData).Target!;
            var output = state.Output!;
            output.Position = state.Position;
            output.Write(new ReadOnlySpan<byte>(buffer, (int)size));
            state.Position = output.Position;
            return size;
        }

        [UnmanagedCallersOnly]
        public static long SkipFn(long nbBytes, void* userData)
        {
            var state = (StreamState)GCHandle.FromIntPtr((IntPtr)userData).Target!;
            state.Position += nbBytes;
            return nbBytes;
        }

        [UnmanagedCallersOnly]
        public static int SeekFn(long nbBytes, void* userData)
        {
            var state = (StreamState)GCHandle.FromIntPtr((IntPtr)userData).Target!;
            state.Position = nbBytes;
            return 1; // OPJ_TRUE
        }

        [LibraryImport(Lib)] public static partial IntPtr opj_version();

        [LibraryImport(Lib)] public static partial IntPtr opj_create_compress(int format);
        [LibraryImport(Lib)] public static partial IntPtr opj_create_decompress(int format);
        [LibraryImport(Lib)] public static partial void opj_destroy_codec(IntPtr codec);

        [LibraryImport(Lib)] public static partial OpjImage* opj_image_create(uint numcmpts, OpjImageCmptParm* cmptparms, int clrspc);
        [LibraryImport(Lib)] public static partial void opj_image_destroy(OpjImage* image);

        [LibraryImport(Lib)] public static partial void opj_set_default_encoder_parameters(OpjCParameters* parameters);
        [LibraryImport(Lib)] public static partial int opj_setup_encoder(IntPtr codec, OpjCParameters* parameters, IntPtr image);
        [LibraryImport(Lib)] public static partial int opj_start_compress(IntPtr codec, IntPtr image, IntPtr stream);
        [LibraryImport(Lib)] public static partial int opj_encode(IntPtr codec, IntPtr stream);
        [LibraryImport(Lib)] public static partial int opj_end_compress(IntPtr codec, IntPtr stream);

        [LibraryImport(Lib)] public static partial void opj_set_default_decoder_parameters(OpjDParameters* parameters);
        [LibraryImport(Lib)] public static partial int opj_setup_decoder(IntPtr codec, OpjDParameters* parameters);
        [LibraryImport(Lib)] public static partial int opj_read_header(IntPtr stream, IntPtr codec, IntPtr* image);
        [LibraryImport(Lib)] public static partial int opj_decode(IntPtr codec, IntPtr stream, IntPtr image);
        [LibraryImport(Lib)] public static partial int opj_end_decompress(IntPtr codec, IntPtr stream);

        [LibraryImport(Lib)] public static partial IntPtr opj_stream_create(nuint bufferSize, int isInput);
        [LibraryImport(Lib)] public static partial void opj_stream_destroy(IntPtr stream);
        [LibraryImport(Lib)] public static partial void opj_stream_set_read_function(IntPtr stream, delegate* unmanaged<byte*, nuint, void*, nuint> fn);
        [LibraryImport(Lib)] public static partial void opj_stream_set_write_function(IntPtr stream, delegate* unmanaged<byte*, nuint, void*, nuint> fn);
        [LibraryImport(Lib)] public static partial void opj_stream_set_skip_function(IntPtr stream, delegate* unmanaged<long, void*, long> fn);
        [LibraryImport(Lib)] public static partial void opj_stream_set_seek_function(IntPtr stream, delegate* unmanaged<long, void*, int> fn);
        [LibraryImport(Lib)] public static partial void opj_stream_set_user_data(IntPtr stream, void* data, void* freeFn);
        [LibraryImport(Lib)] public static partial void opj_stream_set_user_data_length(IntPtr stream, ulong length);
    }
}
