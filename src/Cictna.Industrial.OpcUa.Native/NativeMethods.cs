using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]

namespace Cictna.Industrial.OpcUa.Native;

/// <summary>
/// Raw P/Invoke declarations for the cictna_ua shim (native/shim/cictna_ua.h).
/// Keep this file a one-to-one mirror of the header. Nothing here is public:
/// callers go through <see cref="OpcUaNativeLibrary"/> and, from Phase 1, safe handles.
/// </summary>
/// <remarks>
/// <para>
/// <c>LibraryImport</c> (source-generated marshalling) rather than <c>DllImport</c>: the
/// marshalling code is generated at compile time, so it is visible, trimming- and AOT-safe,
/// and costs nothing at run time. All parameters are blittable (ints, pointers), so no
/// marshalling stubs are needed at all.
/// </para>
/// <para>
/// Type mapping to C: int32_t = int, uint32_t = uint, uint16_t = ushort, int64_t = long,
/// const char* = nint (read with <see cref="ReadStaticString"/>, never freed, because the
/// shim returns pointers to static storage). Calling convention is the platform default;
/// x64 and ARM64 each have a single C calling convention, so nothing needs to be specified.
/// </para>
/// <para>
/// When changing a signature: change cictna_ua.h, this file and <c>CICTNA_UA_ABI_VERSION</c> /
/// <see cref="OpcUaNativeLibrary.ExpectedAbiVersion"/> together, in one commit.
/// </para>
/// </remarks>
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

    // ---- memory, codec, certificates (ABI 2) ------------------------------------------

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_free")]
    internal static partial void Free(nint buffer);

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_codec_roundtrip")]
    internal static unsafe partial uint CodecRoundtrip(uint typeId, byte* input, nuint inputLength,
        out nint output, out nuint outputLength, out nuint consumed);

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_create_certificate", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial uint CreateCertificate(string commonName, string? organization, string applicationUri,
        string? dnsName, uint keySizeBits, uint validDays, out nint certificate, out nuint certificateLength,
        out nint privateKey, out nuint privateKeyLength);

    // ---- client (ABI 2) ---------------------------------------------------------------------

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_client_new")]
    internal static unsafe partial uint ClientNew(ClientOptionsNative* options, out ClientHandle client);

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_client_delete")]
    internal static partial void ClientDelete(nint client);

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_client_connect", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial uint ClientConnect(ClientHandle client, string endpointUrl, byte* endpoint,
        nuint endpointLength, int userTokenType);

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_client_disconnect")]
    internal static partial uint ClientDisconnect(ClientHandle client);

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_client_iterate")]
    internal static partial uint ClientIterate(ClientHandle client, uint timeoutMs);

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_client_state")]
    internal static partial void ClientState(ClientHandle client, out int channelState, out int sessionState, out uint connectStatus);

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_client_send")]
    internal static unsafe partial uint ClientSend(ClientHandle client, uint requestEncodingId, uint responseEncodingId,
        byte* request, nuint requestLength, out uint requestId);

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_client_cancel")]
    internal static partial uint ClientCancel(ClientHandle client, uint requestId);

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_client_poll")]
    internal static unsafe partial int ClientPoll(ClientHandle client, out uint requestId, out uint status,
        out byte* response, out nuint responseLength);

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_client_poll_log")]
    internal static unsafe partial int ClientPollLog(ClientHandle client, out int level, out int category,
        out byte* message, out uint dropped);

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_client_get_endpoints", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial uint ClientGetEndpoints(ClientHandle client, string url, out nint response, out nuint responseLength);

    // ---- server (ABI 2) ---------------------------------------------------------------------

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_server_new")]
    internal static unsafe partial uint ServerNew(ServerOptionsNative* options, out ServerHandle server);

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_server_delete")]
    internal static partial void ServerDelete(nint server);

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_server_start")]
    internal static partial uint ServerStart(ServerHandle server);

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_server_iterate")]
    internal static partial uint ServerIterate(ServerHandle server, out uint nextMs);

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_server_stop")]
    internal static partial uint ServerStop(ServerHandle server);

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_server_add_namespace", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial uint ServerAddNamespace(ServerHandle server, string uri, out ushort index);

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_server_add_node")]
    internal static unsafe partial uint ServerAddNode(ServerHandle server, byte* item, nuint itemLength,
        out nint nodeId, out nuint nodeIdLength);

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_server_write")]
    internal static unsafe partial uint ServerWrite(ServerHandle server, byte* writeValue, nuint length);

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_server_read")]
    internal static unsafe partial uint ServerRead(ServerHandle server, byte* readValueId, nuint length,
        int timestamps, out nint dataValue, out nuint dataValueLength);

    [LibraryImport(LibraryName, EntryPoint = "cictna_ua_server_poll_log")]
    internal static unsafe partial int ServerPollLog(ServerHandle server, out int level, out int category,
        out byte* message, out uint dropped);

    /// <summary>Copies a buffer allocated by the native library and frees it.</summary>
    internal static byte[] TakeBuffer(nint buffer, nuint length)
    {
        try
        {
            if (buffer == 0)
            {
                return [];
            }
            var result = new byte[checked((int)length)];
            Marshal.Copy(buffer, result, 0, result.Length);
            return result;
        }
        finally
        {
            Free(buffer);
        }
    }

    /// <summary>Reads a static UTF-8 string owned by the native library. Never frees it.</summary>
    internal static string ReadStaticString(nint value) =>
        value == 0 ? string.Empty : Marshal.PtrToStringUTF8(value) ?? string.Empty;
}
