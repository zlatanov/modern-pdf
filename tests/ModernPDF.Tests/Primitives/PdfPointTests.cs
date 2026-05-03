using ModernPDF.Primitives;

namespace ModernPDF.Tests.Primitives;

public sealed class PdfPointTests
{
    [Fact]
    public void TranslateReturnsShiftedPoint()
    {
        PdfPoint point = new(10, -5);

        PdfPoint translated = point.Translate(dx: 2.5, dy: 7.5);

        Assert.Equal(12.5, translated.X);
        Assert.Equal(2.5, translated.Y);
    }

    [Fact]
    public void TranslateDoesNotMutateOriginalPoint()
    {
        PdfPoint point = new(1, 2);

        _ = point.Translate(3, 4);

        Assert.Equal(1, point.X);
        Assert.Equal(2, point.Y);
    }
}
