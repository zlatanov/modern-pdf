using System.Text;

namespace ModernPDF.Tests;

public sealed class PdfDocumentShapePathAndStyleTests
{
    [Fact]
    public void AddPageLineSupportsStrokeStyleOperators()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddPage();

        document.AddPageLine(
            0,
            10,
            10,
            80,
            40,
            new PdfShapeOptions
            {
                StrokeColor = new PdfRgbColor(0, 0, 0),
                StrokeWidth = 2,
                StrokeLineCap = PdfShapeLineCap.Round,
                StrokeLineJoin = PdfShapeLineJoin.Bevel,
                StrokeMiterLimit = 7.5,
                StrokeDashPattern = new PdfShapeDashPattern
                {
                    Segments = [3, 1],
                    Phase = 2,
                },
            });

        string text = Encoding.ASCII.GetString(document.Save());
        Assert.Contains(" 1 J ", text, StringComparison.Ordinal);
        Assert.Contains(" 2 j ", text, StringComparison.Ordinal);
        Assert.Contains(" 7.5 M ", text, StringComparison.Ordinal);
        Assert.Contains("[3 1] 2 d", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPageRectangleSupportsEvenOddFillRule()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddPage();

        document.AddPageRectangle(
            0,
            20,
            30,
            60,
            40,
            new PdfShapeOptions
            {
                StrokeColor = new PdfRgbColor(0, 0, 0),
                FillColor = new PdfRgbColor(1, 0, 0),
                FillRule = PdfShapeFillRule.EvenOdd,
            });

        string text = Encoding.ASCII.GetString(document.Save());
        Assert.Contains("20 30 60 40 re B* Q", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPageEllipseAppendsBezierPath()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddPage();

        document.AddPageEllipse(0, 100, 120, 40, 20);

        string text = Encoding.ASCII.GetString(document.Save());
        Assert.Contains("140 120 m", text, StringComparison.Ordinal);
        Assert.Contains(" c h S Q", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPagePolygonSupportsOpenPath()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddPage();

        document.AddPagePolygon(
            0,
            [new PdfShapePoint(10, 10), new PdfShapePoint(30, 10), new PdfShapePoint(30, 40)],
            closePath: false);

        string text = Encoding.ASCII.GetString(document.Save());
        Assert.Contains("10 10 m 30 10 l 30 40 l S Q", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" h S Q", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPagePathSupportsMoveLineCurveAndClose()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddTextPage("path-content");

        document.AddPagePath(
            0,
            [
                new PdfPathMoveTo(20, 20),
                new PdfPathLineTo(60, 20),
                new PdfPathCurveTo(70, 20, 70, 40, 60, 40),
                new PdfPathClosePath(),
            ],
            new PdfShapeOptions
            {
                StrokeColor = null,
                FillColor = new PdfRgbColor(0.2, 0.2, 0.8),
                FillRule = PdfShapeFillRule.EvenOdd,
            });

        byte[] saved = document.Save();
        string text = Encoding.ASCII.GetString(saved);

        Assert.Equal("path-content", PdfDocument.Open(saved).ExtractText());
        Assert.Contains("20 20 m 60 20 l 70 20 70 40 60 40 c h f* Q", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPagePathThrowsWhenMoveCommandIsMissing()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddPage();

        Assert.Throws<ArgumentException>(() => document.AddPagePath(0, [new PdfPathLineTo(10, 10)]));
    }
}
