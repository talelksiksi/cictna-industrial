using System.Diagnostics.Metrics;

namespace Cictna.Industrial.Modbus;

/// <summary>
/// Metrics of the Modbus layer (System.Diagnostics.Metrics). Collect them with OpenTelemetry or
/// <c>dotnet-counters monitor --counters Cictna.Industrial.Modbus</c>.
/// </summary>
public static class ModbusTelemetry
{
    /// <summary>Name of the meter.</summary>
    public const string MeterName = "Cictna.Industrial.Modbus";

    internal static readonly Meter Meter = new(MeterName, typeof(ModbusTelemetry).Assembly.GetName().Version?.ToString());

    internal static readonly Counter<long> ClientRequests = Meter.CreateCounter<long>(
        "cictna.modbus.client.requests", description: "Modbus requests completed by the client, by function and status.");

    internal static readonly Histogram<double> ClientRequestDuration = Meter.CreateHistogram<double>(
        "cictna.modbus.client.request.duration", unit: "ms", description: "Modbus request round-trip time.");

    internal static readonly Counter<long> ClientStateChanges = Meter.CreateCounter<long>(
        "cictna.modbus.client.state_changes", description: "Client connection state changes, by new state.");

    internal static readonly Counter<long> ClientDiscardedResponses = Meter.CreateCounter<long>(
        "cictna.modbus.client.discarded_responses", description: "Responses that matched no waiting request (late after a timeout, or unknown).");

    internal static readonly Counter<long> ServerRequests = Meter.CreateCounter<long>(
        "cictna.modbus.server.requests", description: "Requests answered by the server, by function and exception.");

    internal static readonly UpDownCounter<long> ServerConnections = Meter.CreateUpDownCounter<long>(
        "cictna.modbus.server.connections", description: "Open client connections.");

    internal static readonly Counter<long> ServerRefusedConnections = Meter.CreateCounter<long>(
        "cictna.modbus.server.refused_connections", description: "Connections closed at once because MaxConnections was reached.");

    internal static readonly Counter<long> ServerProtocolErrors = Meter.CreateCounter<long>(
        "cictna.modbus.server.protocol_errors", description: "Connections closed because of an invalid frame, or frames skipped.");
}
