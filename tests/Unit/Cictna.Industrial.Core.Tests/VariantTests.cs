namespace Cictna.Industrial.Core.Tests;

public sealed class VariantTests
{
    [Fact]
    public void ScalarsKeepTypeAndValue()
    {
        Variant v = 1450.5;
        Assert.Equal(BuiltInType.Double, v.Type);
        Assert.False(v.IsArray);
        Assert.Equal(1450.5, v.GetValue<double>());
        Assert.True(v.TryGetValue(out double d) && d == 1450.5);
        Assert.False(v.TryGetValue(out int _));
        Assert.Throws<InvalidCastException>(() => v.GetValue<string>());
    }

    [Fact]
    public void ByteStringAndByteArrayAreDistinct()
    {
        var bytes = new byte[] { 1, 2 };
        var byteString = Variant.From(bytes);
        var byteArray = Variant.FromArray(bytes);
        Assert.Equal(BuiltInType.ByteString, byteString.Type);
        Assert.False(byteString.IsArray);
        Assert.Equal(BuiltInType.Byte, byteArray.Type);
        Assert.True(byteArray.IsArray);
        Assert.NotEqual(byteString, byteArray);
    }

    [Fact]
    public void DateTimesAreStoredAsUtc()
    {
        var unspecified = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Unspecified);
        Assert.Equal(DateTimeKind.Utc, Variant.From(unspecified).GetValue<DateTime>().Kind);
        var local = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Local);
        Assert.Equal(local.ToUniversalTime(), Variant.From(local).GetValue<DateTime>());
    }

    [Fact]
    public void GenericFactoriesCheckTheClrType()
    {
        Assert.Equal(Variant.From(5), Variant.Scalar(BuiltInType.Int32, 5));
        Assert.Throws<ArgumentException>(() => Variant.Scalar(BuiltInType.Int32, 5L));
        Assert.Throws<ArgumentException>(() => Variant.Scalar(BuiltInType.NodeId, null));
        Assert.Throws<ArgumentException>(() => Variant.Scalar(BuiltInType.Variant, Variant.Null));
        Assert.Equal(BuiltInType.String, Variant.Scalar(BuiltInType.String, null).Type);
        Assert.Throws<ArgumentException>(() => Variant.Array(BuiltInType.Int32, new long[] { 1 }));
    }

    [Fact]
    public void MatricesAreFlatWithDimensions()
    {
        var m = Variant.Matrix(BuiltInType.Int16, new short[] { 1, 2, 3, 4, 5, 6 }, 2, 3);
        Assert.True(m.IsArray);
        Assert.True(m.IsMatrix);
        Assert.Equal(new[] { 2, 3 }, m.ArrayDimensions);
        Assert.Equal(6, m.ArrayLength);
        Assert.Throws<ArgumentException>(() => Variant.Matrix(BuiltInType.Int16, new short[] { 1, 2, 3 }, 2, 2));
        Assert.Throws<ArgumentException>(() => Variant.Matrix(BuiltInType.Int16, new short[] { 1 }, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Variant.Matrix(BuiltInType.Int16, Array.Empty<short>(), 2, 0));
    }

    [Fact]
    public void EqualityIsDeep()
    {
        Assert.Equal(Variant.FromArray(new[] { 1, 2, 3 }), Variant.FromArray(new[] { 1, 2, 3 }));
        Assert.NotEqual(Variant.FromArray(new[] { 1, 2, 3 }), Variant.FromArray(new[] { 1, 2, 4 }));
        Assert.Equal(Variant.FromArray(new byte[]?[] { [1], null }), Variant.FromArray(new byte[]?[] { [1], null }));
        Assert.Equal(
            Variant.FromArray(new Variant[] { 1, "a", Variant.FromArray(new[] { 1.5 }) }),
            Variant.FromArray(new Variant[] { 1, "a", Variant.FromArray(new[] { 1.5 }) }));
        Assert.NotEqual(Variant.From(1), Variant.From(1u));
        Assert.Equal(Variant.From(double.NaN), Variant.From(double.NaN));
        Assert.Equal(Variant.Null, default);
        Assert.NotEqual(Variant.Matrix(BuiltInType.Int32, new[] { 1, 2, 3, 4 }, 2, 2), Variant.FromArray(new[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public void ReadableToString()
    {
        Assert.Equal("1.5", Variant.From(1.5).ToString());
        Assert.Equal("Int32[3]", Variant.FromArray(new[] { 1, 2, 3 }).ToString());
        Assert.Equal("Int16[2,3]", Variant.Matrix(BuiltInType.Int16, new short[6], 2, 3).ToString());
        Assert.Equal("(null)", Variant.Null.ToString());
    }

    [Fact]
    public void DataValueDefaultsToGoodWithoutTimestamps()
    {
        var dv = new DataValue(42);
        Assert.True(dv.Status.IsGood);
        Assert.Null(dv.SourceTimestamp);
        Assert.Equal("42", dv.ToString());
        Assert.Equal("(null) [BadNodeIdUnknown]", DataValue.FromStatus(StatusCodes.BadNodeIdUnknown).ToString());
        Assert.Equal(new DataValue(1.5, StatusCodes.Good), new DataValue(1.5));
    }
}
