namespace Cictna.Industrial.Core.Tests;

public sealed class NodeIdTests
{
    [Theory]
    [InlineData("i=2258")]
    [InlineData("ns=2;i=4294967295")]
    [InlineData("ns=2;s=Pump01.Speed")]
    [InlineData("ns=65535;s=")]
    [InlineData("ns=1;s=with;semicolons=and equals")]
    [InlineData("ns=3;g=09087e75-8e5e-499b-954f-f2a9603db28a")]
    [InlineData("ns=4;b=M/RbKBsRVkePCePcx24oRA==")]
    public void TextFormRoundTrips(string text)
    {
        var id = NodeId.Parse(text);
        Assert.Equal(text, id.ToString());
        Assert.Equal(id, NodeId.Parse(id.ToString()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("x=1")]
    [InlineData("i=-1")]
    [InlineData("i=4294967296")]
    [InlineData("ns=70000;i=1")]
    [InlineData("ns=1")]
    [InlineData("ns=1;g=not-a-guid")]
    [InlineData("ns=1;b=***")]
    public void InvalidTextIsRejected(string text)
    {
        Assert.False(NodeId.TryParse(text, out _));
        Assert.Throws<FormatException>(() => NodeId.Parse(text));
    }

    [Fact]
    public void DefaultIsTheNullNodeId()
    {
        Assert.True(default(NodeId).IsNull);
        Assert.Equal("i=0", NodeId.Null.ToString());
        Assert.False(new NodeId(0, 1).IsNull);
    }

    [Fact]
    public void EqualityIsByValueForEveryIdentifierType()
    {
        Assert.Equal(new NodeId(85), NodeId.Parse("i=85"));
        Assert.Equal(new NodeId("A", 2), new NodeId("A", 2));
        Assert.NotEqual(new NodeId("A", 2), new NodeId("a", 2));
        Assert.NotEqual(new NodeId("A", 2), new NodeId("A", 3));
        Assert.Equal(new NodeId([1, 2, 3], 1), new NodeId([1, 2, 3], 1));
        Assert.Equal(new NodeId([1, 2, 3], 1).GetHashCode(), new NodeId([1, 2, 3], 1).GetHashCode());
        Assert.NotEqual(new NodeId(1, 0), new NodeId("1", 0));
    }

    [Fact]
    public void OpaqueIdentifierIsCopied()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var id = new NodeId(bytes, 1);
        bytes[0] = 99;
        Assert.Equal(1, id.OpaqueIdentifier.Span[0]);
    }

    [Fact]
    public void AccessingTheWrongIdentifierKindThrows()
    {
        var id = new NodeId("x", 1);
        Assert.Throws<InvalidOperationException>(() => id.NumericIdentifier);
        Assert.Equal("x", id.StringIdentifier);
    }

    [Fact]
    public void ExpandedNodeIdTextForm()
    {
        Assert.Equal("ns=2;s=A", new ExpandedNodeId(new NodeId("A", 2)).ToString());
        Assert.Equal("nsu=urn:plant;s=A", new ExpandedNodeId(new NodeId("A", 2), "urn:plant").ToString());
        Assert.Equal("svr=1;i=85", new ExpandedNodeId(new NodeId(85), null, 1).ToString());
        Assert.True(((ExpandedNodeId)new NodeId(85)).IsLocal);
        // Normalisation required by Part 4: a URI forces namespace index 0, an empty URI is none.
        Assert.Equal(0, new ExpandedNodeId(new NodeId("A", 2), "urn:plant").NodeId.NamespaceIndex);
        Assert.Null(new ExpandedNodeId(new NodeId("A", 2), "").NamespaceUri);
        Assert.Equal(2, new ExpandedNodeId(new NodeId("A", 2), "").NodeId.NamespaceIndex);
    }
}
