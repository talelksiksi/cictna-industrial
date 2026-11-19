using Cictna.Industrial.Core;
using Cictna.Industrial.OpcUa.Encoding;
using Microsoft.Extensions.Logging;

namespace Cictna.Industrial.OpcUa.Internal;

/// <summary>Decoding and result checks shared by every service call.</summary>
internal static class ServiceCodec
{
    /// <summary>Decodes a whole response; trailing bytes or malformed data become BadDecodingError.</summary>
    public static T Decode<T>(byte[] bytes, Func<BinaryDecoder, T> read)
    {
        try
        {
            var decoder = new BinaryDecoder(bytes);
            var value = read(decoder);
            decoder.EnsureFullyConsumed();
            return value;
        }
        catch (OpcUaDecodingException ex)
        {
            throw new OpcUaException(StatusCodes.BadDecodingError, "Response could not be decoded", ex);
        }
    }

    public static void CheckServiceResult(string service, ResponseHeader header)
    {
        if (header.ServiceResult.IsBad)
        {
            throw new OpcUaException(header.ServiceResult, $"{service} failed");
        }
    }

    public static void CheckCount(string service, int actual, int expected)
    {
        if (actual != expected)
        {
            throw new OpcUaException(StatusCodes.BadUnexpectedError, $"{service} returned {actual} results for {expected} items");
        }
    }
}

/// <summary>What subscriptions and the publish engine need from the client that owns them.</summary>
internal interface ISubscriptionHost
{
    /// <summary>Sends one request on the current session. <paramref name="timeout"/> null means the client's request timeout.</summary>
    Task<byte[]> SendAsync(string service, uint requestEncodingId, uint responseEncodingId, byte[] request, TimeSpan? timeout, CancellationToken cancellationToken);

    /// <summary>A request header with the client's request timeout as hint.</summary>
    RequestHeader Header();

    /// <summary>The client's request timeout.</summary>
    TimeSpan RequestTimeout { get; }

    /// <summary>Monitored items per Create/DeleteMonitoredItems call (the server's MaxMonitoredItemsPerCall).</summary>
    int MaxMonitoredItemsPerCall { get; }

    /// <summary>Increases with every new session; a subscription knows in which one it lives.</summary>
    long SessionGeneration { get; }

    /// <summary>Whether a session is active right now.</summary>
    bool IsConnected { get; }

    /// <summary>A client handle unique within the client, for a new monitored item.</summary>
    uint NextClientHandle();

    /// <summary>Runs an event handler invocation on the client's event thread, in order.</summary>
    void RaiseEvent(Action raise);

    /// <summary>The subscription was disposed; the client stops tracking it.</summary>
    void Forget(OpcUaSubscription subscription);

    /// <summary>The engine saw no keep-alive in time: the connection is probably dead.</summary>
    void ReportKeepAliveMissed();

    ILogger Logger { get; }
}
