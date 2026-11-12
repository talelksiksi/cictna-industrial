using Microsoft.Extensions.Logging;

namespace Cictna.Industrial.OpcUa.Internal;

/// <summary>
/// Forwards log lines queued by the native library (open62541) to an <see cref="ILogger"/>.
/// The native side formats and queues; the worker thread drains the queue after each iteration.
/// </summary>
internal static partial class NativeLog
{
    // open62541's UA_LogCategory values, in order.
    private static readonly string[] Categories =
    [
        "network", "securechannel", "session", "server", "client", "application", "security", "eventloop", "pubsub", "discovery",
    ];

    /// <summary>open62541 level (100..600) for a minimum <see cref="LogLevel"/>.</summary>
    public static int ToNativeLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => 100,
        LogLevel.Debug => 200,
        LogLevel.Information => 300,
        LogLevel.Warning => 400,
        LogLevel.Error => 500,
        _ => 600,
    };

    private static LogLevel FromNativeLevel(int level) => level switch
    {
        < 200 => LogLevel.Trace,
        < 300 => LogLevel.Debug,
        < 400 => LogLevel.Information,
        < 500 => LogLevel.Warning,
        < 600 => LogLevel.Error,
        _ => LogLevel.Critical,
    };

    public static void Write(ILogger logger, int level, int category, string message, string source)
    {
        var logLevel = FromNativeLevel(level);
        if (!logger.IsEnabled(logLevel))
        {
            return;
        }
        var cat = category >= 0 && category < Categories.Length ? Categories[category] : "other";
        Line(logger, logLevel, source, cat, message);
    }

    public static void Dropped(ILogger logger, uint count, string source)
    {
        OpcUaTelemetry.DroppedLogLines.Add(count);
        DroppedLines(logger, source, count);
    }

    [LoggerMessage(Message = "[{Source}/{NativeCategory}] {NativeMessage}")]
    private static partial void Line(ILogger logger, LogLevel level, string source, string nativeCategory, string nativeMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{Source}] {Count} native log lines were dropped because the queue was full")]
    private static partial void DroppedLines(ILogger logger, string source, uint count);
}
