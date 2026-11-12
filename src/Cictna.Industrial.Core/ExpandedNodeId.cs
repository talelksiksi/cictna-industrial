using System.Globalization;

namespace Cictna.Industrial.Core;

/// <summary>
/// A <see cref="Core.NodeId"/> that may also name its namespace by URI and point to another server.
/// Used where a reference can leave the current server's address space (browse results, some
/// type ids). When <see cref="NamespaceUri"/> is set it takes precedence over the namespace index.
/// </summary>
public readonly struct ExpandedNodeId : IEquatable<ExpandedNodeId>
{
    /// <summary>Creates an ExpandedNodeId.</summary>
    /// <param name="nodeId">The local NodeId.</param>
    /// <param name="namespaceUri">Optional namespace URI, overriding the NodeId's namespace index.</param>
    /// <param name="serverIndex">Index into the server's server array; 0 means the local server.</param>
    public ExpandedNodeId(NodeId nodeId, string? namespaceUri = null, uint serverIndex = 0)
    {
        NodeId = nodeId;
        NamespaceUri = namespaceUri;
        ServerIndex = serverIndex;
    }

    /// <summary>The local NodeId.</summary>
    public NodeId NodeId { get; }

    /// <summary>The namespace URI, or null when the namespace index applies.</summary>
    public string? NamespaceUri { get; }

    /// <summary>The server index; 0 is the local server.</summary>
    public uint ServerIndex { get; }

    /// <summary>True when this points into the local server and has no namespace URI.</summary>
    public bool IsLocal => ServerIndex == 0 && NamespaceUri is null;

    /// <summary>Converts a NodeId into a local ExpandedNodeId.</summary>
    /// <param name="nodeId">The NodeId.</param>
    public static implicit operator ExpandedNodeId(NodeId nodeId) => new(nodeId);

    /// <summary>Converts a NodeId into a local ExpandedNodeId.</summary>
    /// <param name="nodeId">The NodeId.</param>
    public static ExpandedNodeId FromNodeId(NodeId nodeId) => new(nodeId);

    /// <summary>Text form: <c>svr=1;nsu=urn:x;s=Name</c>, with the parts present.</summary>
    public override string ToString()
    {
        var prefix = ServerIndex != 0 ? string.Create(CultureInfo.InvariantCulture, $"svr={ServerIndex};") : string.Empty;
        if (NamespaceUri is null)
        {
            return prefix + NodeId;
        }
        var local = NodeId.ToString();
        if (local.StartsWith("ns=", StringComparison.Ordinal))
        {
            local = local[(local.IndexOf(';', StringComparison.Ordinal) + 1)..];
        }
        return $"{prefix}nsu={NamespaceUri};{local}";
    }

    /// <inheritdoc />
    public bool Equals(ExpandedNodeId other) =>
        NodeId.Equals(other.NodeId) && string.Equals(NamespaceUri, other.NamespaceUri, StringComparison.Ordinal) && ServerIndex == other.ServerIndex;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ExpandedNodeId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(NodeId, NamespaceUri, ServerIndex);

    /// <summary>Value equality.</summary>
    public static bool operator ==(ExpandedNodeId left, ExpandedNodeId right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(ExpandedNodeId left, ExpandedNodeId right) => !left.Equals(right);
}
