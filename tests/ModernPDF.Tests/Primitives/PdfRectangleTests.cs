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
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new PdfRectangle(double.NaN, 0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new PdfRectangle(0, 0, double.PositiveInfinity, 1));
    }

    [Fact]
    public void FromDimensionsRejectsNegativeAndNonFiniteValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = PdfRectangle.FromDimensions(0, 0, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = PdfRectangle.FromDimensions(0, 0, 1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = PdfRectangle.FromDimensions(0, 0, double.NaN, 1));
    }

    [Fact]
    public void EqualityAndInequalityOperatorsCompareEdges()
    {
        PdfRectangle first = new(0, 0, 1, 1);
        PdfRectangle second = new(0, 0, 1, 1);
        PdfRectangle third = new(0, 0, 2, 1);

        Assert.True(first == second);
        Assert.False(first != second);
        Assert.True(first != third);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }
}
