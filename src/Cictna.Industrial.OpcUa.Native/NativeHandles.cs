using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cictna.Industrial.OpcUa.Native;

/// <summary>
/// Owns a native cictna_ua_client. Disposing (or finalizing) deletes it, which closes the
/// connection and frees all native memory. Only the worker thread that drives the client may
/// use the handle while it is alive; see OpcUaClient.
/// </summary>
internal sealed class ClientHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public ClientHandle() : base(ownsHandle: true) { }

    protected override bool ReleaseHandle()
    {
        NativeMethods.ClientDelete(handle);
        return true;
    }
}

/// <summary>Owns a native cictna_ua_server; disposing stops and deletes it.</summary>
internal sealed class ServerHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public ServerHandle() : base(ownsHandle: true) { }

    protected override bool ReleaseHandle()
    {
        NativeMethods.ServerDelete(handle);
        return true;
    }
}

/// <summary>Mirror of cictna_ua_client_options in cictna_ua.h. Field order and types must match exactly.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClientOptionsNative
{
    public uint StructSize;
    public uint RequestTimeoutMs;
    public uint SecureChannelLifetimeMs;
    public uint SessionTimeoutMs;
    public int SecurityMode;
    public byte* SecurityPolicyUri;
    public byte* ApplicationUri;
    public byte* ApplicationName;
    public byte* Certificate;
    public nuint CertificateLength;
    public byte* PrivateKey;
    public nuint PrivateKeyLength;
    public byte* Username;
    public byte* Password;
    public uint AcceptAnyServerCertificate;
    public int LogLevel;
    public byte* TrustList;
    public nuint TrustListLength;
}

/// <summary>Mirror of cictna_ua_server_options in cictna_ua.h. Field order and types must match exactly.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ServerOptionsNative
{
    public uint StructSize;
    public ushort Port;
    public ushort Reserved;
    public byte* ApplicationUri;
    public byte* ApplicationName;
    public byte* Certificate;
    public nuint CertificateLength;
    public byte* PrivateKey;
    public nuint PrivateKeyLength;
    public uint AllowAnonymous;
    public byte* Username;
    public byte* Password;
    public uint MaxNodesPerRead;
    public uint MaxNodesPerWrite;
    public uint MaxNodesPerBrowse;
    public uint MaxReferencesPerNode;
    public int LogLevel;
    public uint MaxMonitoredItemQueueSize;
    public uint AcceptAnyClientCertificate;
    public byte* TrustList;
    public nuint TrustListLength;
}

/// <summary>
/// Null-terminated UTF-8 copies of strings for one native call. The native side copies what it
/// keeps, so the memory is freed as soon as the call returns.
/// </summary>
internal sealed class Utf8Strings : IDisposable
{
    private readonly List<nint> _allocations = [];

    public unsafe byte* Add(string? value)
    {
        if (value is null)
        {
            return null;
        }
        var p = Marshal.StringToCoTaskMemUTF8(value);
        _allocations.Add(p);
        return (byte*)p;
    }

    public void Dispose()
    {
        foreach (var p in _allocations)
        {
            Marshal.ZeroFreeCoTaskMemUTF8(p); // zeroed: may hold a password
        }
        _allocations.Clear();
    }
}
