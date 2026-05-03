using ModernPDF.Primitives;

namespace ModernPDF.Tests.Primitives;

public sealed class PdfRectangleTests
{
    [Fact]
    public void FromDimensionsBuildsExpectedEdges()
    {
        PdfRectangle rectangle = PdfRectangle.FromDimensions(10, 20, 50, 30);

        Assert.Equal(10, rectangle.Left);
        Assert.Equal(20, rectangle.Bottom);
        Assert.Equal(60, rectangle.Right);
        Assert.Equal(50, rectangle.Top);
        Assert.Equal(50, rectangle.Width);
        Assert.Equal(30, rectangle.Height);
    }

    [Fact]
    public void ContainsMatchesPointInclusion()
    {
        PdfRectangle rectangle = new(0, 0, 10, 10);

        Assert.True(rectangle.Contains(new PdfPoint(0, 0)));
        Assert.True(rectangle.Contains(new PdfPoint(5, 9)));
        Assert.False(rectangle.Contains(new PdfPoint(-1, 2)));
        Assert.False(rectangle.Contains(new PdfPoint(2, 11)));
    }

    [Fact]
    public void ConstructorRejectsInvalidEdges()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new PdfRectangle(10, 0, 5, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new PdfRectangle(0, 10, 5, 1));
    }
}
