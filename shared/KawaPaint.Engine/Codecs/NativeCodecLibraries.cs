// KawaPaint - finds the native JPEG XL / JPEG 2000 libraries under their versioned names.
//
// [LibraryImport("jxl")] makes the runtime probe libjxl.so, but Linux distributions only ship that
// unversioned name in their -dev packages: an ordinary install has libjxl.so.0.7 (Ubuntu 24.04,
// Debian 12) or newer, and libopenjp2.so.7. Without this resolver both codecs report themselves
// unavailable on a stock desktop even with the libraries installed. Windows and macOS bundle the
// libraries under the plain names, which the default probe below still finds first.

using System.Reflection;
using System.Runtime.InteropServices;

namespace KawaPaint.Engine.Codecs;

internal static class NativeCodecLibraries
{
    private static readonly Dictionary<string, string[]> VersionedNames = new()
    {
        // Newest first: the C API is stable across these, and later releases fix decoder bugs.
        ["jxl"] = new[] { "libjxl.so.0.12", "libjxl.so.0.11", "libjxl.so.0.10", "libjxl.so.0.9",
                          "libjxl.so.0.8", "libjxl.so.0.7" },
        ["openjp2"] = new[] { "libopenjp2.so.7" },
    };

    private static int _registered;

    /// <summary>Installs the resolver for this assembly; safe to call any number of times. Called
    /// from the codecs' static constructors, before their first native call.</summary>
    internal static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0) return;
        try { NativeLibrary.SetDllImportResolver(typeof(NativeCodecLibraries).Assembly, Resolve); }
        catch (InvalidOperationException) { /* the host installed its own resolver: keep it */ }
        catch (PlatformNotSupportedException) { /* the browser build has no native libraries */ }
    }

    internal static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (NativeLibrary.TryLoad(name, assembly, searchPath, out IntPtr handle)) return handle;
        if (VersionedNames.TryGetValue(name, out string[]? candidates))
            foreach (string candidate in candidates)
                if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out handle)) return handle;
        return IntPtr.Zero;   // let the runtime raise its usual DllNotFoundException
    }
}
