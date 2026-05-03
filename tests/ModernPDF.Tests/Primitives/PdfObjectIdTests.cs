using ModernPDF.Primitives;

namespace ModernPDF.Tests.Primitives;

public sealed class PdfObjectIdTests
{
    [Fact]
    public void ConstructorStoresObjectAndGenerationNumbers()
    {
        PdfObjectId objectId = new(15, 7);

        Assert.Equal(15, objectId.ObjectNumber);
        Assert.Equal((ushort)7, objectId.GenerationNumber);
        Assert.Equal("15 7 R", objectId.ToString());
    }

    [Fact]
    public void ConstructorRejectsInvalidNumbers()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new PdfObjectId(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new PdfObjectId(1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new PdfObjectId(1, ushort.MaxValue + 1));
    }

    [Fact]
    public void EqualityOperatorsAndHashCodeReflectIdentifierValue()
    {
        PdfObjectId first = new(42, 3);
        PdfObjectId second = new(42, 3);
        PdfObjectId third = new(42, 4);

        Assert.True(first == second);
        Assert.False(first != second);
        Assert.False(first.Equals((object?)null));
        Assert.False(first.Equals((object?)"42 3 R"));
        Assert.True(first != third);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }
}
