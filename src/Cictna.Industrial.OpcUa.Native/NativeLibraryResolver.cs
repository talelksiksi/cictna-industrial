using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Cictna.Industrial.OpcUa.Native;

/// <summary>
/// Finds the native library in both layouts it ships in:
/// <list type="bullet">
/// <item>NuGet consumers: the runtime resolves runtimes/&lt;rid&gt;/native through deps.json (default probing).</item>
/// <item>Project references and plain copies: the file sits in runtimes/&lt;rid&gt;/native under the app folder,
/// which default probing does not look at, so we load it from there explicitly.</item>
/// </list>
/// </summary>
internal static class NativeLibraryResolver
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "The resolver must be registered before the first P/Invoke into the native library.")]
    internal static void Register() =>
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);

    /// <summary>Portable runtime id of the current process, for example linux-arm64.</summary>
    internal static string CurrentRid
    {
        get
        {
            var os = OperatingSystem.IsWindows() ? "win"
                : OperatingSystem.IsMacOS() ? "osx"
                : "linux";
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                Architecture.Arm => "arm",
                var other => other.ToString().ToLowerInvariant(),
            };
            return $"{os}-{arch}";
        }
    }

    internal static string NativeFileName =>
        OperatingSystem.IsWindows() ? "cictna_ua.dll"
        : OperatingSystem.IsMacOS() ? "libcictna_ua.dylib"
        : "libcictna_ua.so";

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != NativeMethods.LibraryName)
        {
            return 0;
        }

        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var handle))
        {
            return handle;
        }

        var candidate = Path.Combine(AppContext.BaseDirectory, "runtimes", CurrentRid, "native", NativeFileName);
        if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle))
        {
            return handle;
        }

        // Returning 0 lets the runtime throw its usual DllNotFoundException.
        return 0;
    }
}
