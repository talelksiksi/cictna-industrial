using System.Net;
using System.Net.Sockets;
using Cictna.Industrial.Core;
using Microsoft.Extensions.Logging;

namespace Cictna.Industrial.OpcUa.IntegrationTests;

/// <summary>
/// An <see cref="OpcUaServer"/> with a known address space, started on a free port:
/// <list type="bullet">
/// <item>Types/{Type}: one writable scalar variable per built-in type</item>
/// <item>Types/{Type}Array: one writable array per built-in type, and Types/DoubleMatrix (2x3)</item>
/// <item>Many/Tag000..Tag249: 250 Int32 variables whose value is their index</item>
/// <item>ReadOnly: a variable clients may not write</item>
/// </list>
/// Operation limits are set low (50 nodes per read/write, 10 per browse) so batching is exercised.
/// </summary>
public sealed class TestServer : IAsyncLifetime
{
    public const string NamespaceUri = "urn:cictna:test";
    public const int ManyCount = 250;
    public const int MaxNodesPerRead = 50;

    public OpcUaServer Server { get; private set; } = null!;
    public ushort Ns { get; private set; }
    public NodeId TypesFolder { get; private set; }
    public NodeId ManyFolder { get; private set; }
    public NodeId ReadOnly { get; private set; }
    public Dictionary<string, (NodeId Node, Variant Initial)> Variables { get; } = [];
    public ListLoggerFactory Logs { get; } = new();

    public string Url => Server.LocalEndpointUrl;

    /// <summary>Server options; override in a subclass fixture for security tests.</summary>
    public Func<int, OpcUaServerOptions> Options { get; init; } = port => new OpcUaServerOptions
    {
        Port = port,
        MaxNodesPerRead = MaxNodesPerRead,
        MaxNodesPerWrite = 50,
        MaxNodesPerBrowse = 10,
    };

    public async ValueTask InitializeAsync()
    {
        Server = await OpcUaServer.StartAsync(Options(FreePort()) with { LoggerFactory = Logs }).ConfigureAwait(false);
        Ns = await Server.AddNamespaceAsync(NamespaceUri);
        TypesFolder = await Server.AddFolderAsync(WellKnownNodes.ObjectsFolder, new NodeId("Types", Ns), "Types");
        foreach (var (name, value) in SampleValues())
        {
            var node = await Server.AddVariableAsync(TypesFolder, new NodeId(name, Ns), name, value);
            Variables[name] = (node, value);
        }
        ManyFolder = await Server.AddFolderAsync(WellKnownNodes.ObjectsFolder, new NodeId("Many", Ns), "Many");
        for (var i = 0; i < ManyCount; i++)
        {
            await Server.AddVariableAsync(ManyFolder, ManyTag(i), $"Tag{i:000}", i);
        }
        ReadOnly = await Server.AddVariableAsync(WellKnownNodes.ObjectsFolder, new NodeId("ReadOnly", Ns), "ReadOnly", 7, writable: false);
    }

    public NodeId ManyTag(int i) => new($"Many.Tag{i:000}", Ns);

    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>One initial value per built-in type that can be a variable's value, scalar and array.</summary>
    /// <remarks>
    /// NodeIds and QualifiedNames inside values use namespaces that exist on the server (0, 1, 2):
    /// open62541's client maps unknown namespace indexes to 65535 - index (docs/native-layer.md).
    /// </remarks>
    public static IEnumerable<(string Name, Variant Value)> SampleValues()
    {
        var t = new DateTime(2026, 11, 12, 10, 30, 15, DateTimeKind.Utc).AddTicks(1234567);
        var g = Guid.Parse("09087e75-8e5e-499b-954f-f2a9603db28a");
        yield return ("Boolean", true);
        yield return ("SByte", Variant.From((sbyte)-5));
        yield return ("Byte", Variant.From((byte)200));
        yield return ("Int16", Variant.From((short)-1234));
        yield return ("UInt16", Variant.From((ushort)54321));
        yield return ("Int32", -123456);
        yield return ("UInt32", 4000000000u);
        yield return ("Int64", long.MinValue + 1);
        yield return ("UInt64", Variant.From(ulong.MaxValue - 1));
        yield return ("Float", 3.25f);
        yield return ("Double", Math.PI);
        yield return ("String", "Pompe d'acide, خزان 2");
        yield return ("DateTime", t);
        yield return ("Guid", Variant.From(g));
        yield return ("ByteString", Variant.From(new byte[] { 0, 1, 2, 254, 255 }));
        yield return ("XmlElement", Variant.From(new XmlElement("<tank level=\"3.5\"/>")));
        yield return ("NodeId", Variant.From(new NodeId("Pump01", 1)));
        yield return ("ExpandedNodeId", Variant.From(new ExpandedNodeId(new NodeId(42), "urn:other", 0)));
        yield return ("StatusCode", Variant.From(StatusCodes.UncertainLastUsableValue));
        yield return ("QualifiedName", Variant.From(new QualifiedName(2, "Level")));
        yield return ("LocalizedText", Variant.From(new LocalizedText("fr", "Débit")));

        yield return ("BooleanArray", Variant.FromArray(new[] { true, false, true }));
        yield return ("ByteArray", Variant.FromArray(new byte[] { 1, 2, 3 }));
        yield return ("Int16Array", Variant.FromArray(new short[] { -1, 0, 1 }));
        yield return ("Int32Array", Variant.FromArray(new[] { 1, 2, 3, 4 }));
        yield return ("UInt64Array", Variant.FromArray(new ulong[] { 0, ulong.MaxValue }));
        yield return ("FloatArray", Variant.FromArray(new[] { 1.5f, float.NegativeInfinity }));
        yield return ("DoubleArray", Variant.FromArray(new[] { 0.1, -0.0, double.MaxValue }));
        yield return ("StringArray", Variant.FromArray(new string?[] { "a", "", "عربي" }));
        yield return ("DateTimeArray", Variant.FromArray(new[] { t, t.AddDays(1) }));
        yield return ("GuidArray", Variant.FromArray(new[] { g, Guid.Empty }));
        yield return ("ByteStringArray", Variant.FromArray(new byte[]?[] { [1, 2], [] }));
        yield return ("NodeIdArray", Variant.FromArray(new[] { new NodeId(85), new NodeId("x", 2) }));
        yield return ("StatusCodeArray", Variant.FromArray(new[] { StatusCodes.Good, StatusCodes.BadTimeout }));
        yield return ("LocalizedTextArray", Variant.FromArray(new[] { new LocalizedText("en", "Flow"), new LocalizedText("ar", "تدفق") }));
        yield return ("DoubleMatrix", Variant.Matrix(BuiltInType.Double, new[] { 1.0, 2, 3, 4, 5, 6 }, 2, 3));
    }

    public async ValueTask DisposeAsync() => await Server.DisposeAsync();
}

/// <summary>Collects log lines in memory so tests can assert on them.</summary>
public sealed class ListLoggerFactory : ILoggerFactory
{
    private readonly List<(LogLevel Level, string Category, string Message)> _lines = [];

    public IReadOnlyList<(LogLevel Level, string Category, string Message)> Lines
    {
        get
        {
            lock (_lines)
            {
                return _lines.ToArray();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new ListLogger(this, categoryName);

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    private sealed class ListLogger(ListLoggerFactory owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._lines)
            {
                owner._lines.Add((logLevel, category, formatter(state, exception)));
            }
        }
    }
}
