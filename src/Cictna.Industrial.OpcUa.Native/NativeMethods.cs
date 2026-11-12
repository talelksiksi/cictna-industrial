using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]

namespace Cictna.Industrial.OpcUa.Native;

/// <summary>
/// Raw P/Invoke declarations for the cictna_ua shim (native/shim/cictna_ua.h).
/// Keep this file a one-to-one mirror of the header. Nothing here is public:
/// callers go through <see cref="OpcUaNativeLibrary"/> and, from Phase 1, safe handles.
/// </summary>
internal static partial class NativeMethods
{
    internal const string LibraryName = "cictna_ua";

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_abi_version")]
    internal static partial int AbiVersion();

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_stack_version")]
    internal static partial nint StackVersion();

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_encryption_backend")]
    internal static partial nint EncryptionBackend();

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_features")]
    internal static partial uint Features();

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_status_name")]
    internal static partial nint StatusName(uint status);

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_selftest_loopback")]
    internal static partial uint SelfTestLoopback(ushort port, uint timeoutMs, out long serverTime);

    /// <summary>Reads a static UTF-8 string owned by the native library. Never frees it.</summary>
    internal static string ReadStaticString(nint value) =>
        value == 0 ? string.Empty : Marshal.PtrToStringUTF8(value) ?? string.Empty;
}
