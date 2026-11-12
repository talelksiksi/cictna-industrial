namespace Cictna.Industrial.Core.Tests;

public sealed class StatusCodeTests
{
    [Theory]
    [InlineData(0x00000000u, StatusSeverity.Good)]
    [InlineData(0x40000000u, StatusSeverity.Uncertain)]
    [InlineData(0x80000000u, StatusSeverity.Bad)]
    [InlineData(0xC0000000u, StatusSeverity.Bad)] // reserved pattern is treated as Bad
    public void SeverityComesFromTheTopTwoBits(uint code, StatusSeverity expected)
    {
        var status = new StatusCode(code);
        Assert.Equal(expected, status.Severity);
        Assert.Equal(expected == StatusSeverity.Good, status.IsGood);
        Assert.Equal(expected == StatusSeverity.Uncertain, status.IsUncertain);
        Assert.Equal(expected == StatusSeverity.Bad, status.IsBad);
    }

    [Fact]
    public void NamesComeFromTheSpecificationTable()
    {
        Assert.Equal("BadTimeout", StatusCodes.BadTimeout.Name);
        Assert.Equal(0x800A0000u, StatusCodes.BadTimeout.Code);
        Assert.Equal("Good", default(StatusCode).Name);
        Assert.False(string.IsNullOrEmpty(StatusCodes.BadNodeIdUnknown.Description));
    }

    [Fact]
    public void FlagBitsDoNotHideTheName()
    {
        // Low 16 bits are flags (for example the overflow bit 0x0080); the name must still resolve.
        var withFlags = new StatusCode(StatusCodes.UncertainLastUsableValue.Code | 0x0080u);
        Assert.Equal("UncertainLastUsableValue", withFlags.Name);
        Assert.NotEqual(StatusCodes.UncertainLastUsableValue, withFlags);
        Assert.Equal(StatusCodes.UncertainLastUsableValue, withFlags.WithoutFlags);
    }

    [Fact]
    public void UnknownCodesShowTheirHexValue()
    {
        var unknown = new StatusCode(0x80FF0000u);
        Assert.Equal("0x80FF0000", unknown.Name);
        Assert.Equal(string.Empty, unknown.Description);
        Assert.Equal("0x80FF0000 (0x80FF0000)", unknown.ToString());
    }
}
