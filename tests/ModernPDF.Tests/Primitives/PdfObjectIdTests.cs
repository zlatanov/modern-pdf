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
}
