using System.Text;

namespace ModernPDF.Tests;

public sealed class PdfDocumentShapeAdvancedFeaturesTests
{
    [Fact]
    public void AddPageShapeSupportsOpacityAndBlendMode()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddPage();

        document.AddPageRectangle(
            0,
            20,
            20,
            80,
            40,
            new PdfShapeOptions
            {
                FillColor = new PdfRgbColor(1, 0, 0),
                StrokeOpacity = 0.5,
                FillOpacity = 0.25,
                BlendMode = PdfBlendMode.Multiply,
            });

        string text = Encoding.ASCII.GetString(document.Save());
        Assert.Contains("/ExtGState", text, StringComparison.Ordinal);
        Assert.Contains("/CA 0.5", text, StringComparison.Ordinal);
        Assert.Contains("/ca 0.25", text, StringComparison.Ordinal);
        Assert.Contains("/BM /Multiply", text, StringComparison.Ordinal);
        Assert.Contains("/GS1 gs", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPageShapeSupportsLinearGradientFill()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddPage();

        document.AddPageRectangle(
            0,
            10,
            10,
            120,
            40,
            new PdfShapeOptions
            {
                StrokeColor = null,
                FillLinearGradient = new PdfShapeLinearGradient
                {
                    StartX = 10,
                    StartY = 10,
                    EndX = 130,
                    EndY = 50,
                    StartColor = new PdfRgbColor(1, 0, 0),
                    EndColor = new PdfRgbColor(0, 0, 1),
                },
            });

        string text = Encoding.ASCII.GetString(document.Save());
        Assert.Contains("/PatternType 2", text, StringComparison.Ordinal);
        Assert.Contains("/ShadingType 2", text, StringComparison.Ordinal);
        Assert.Contains("/Pattern cs /Pt1 scn", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPagePathSupportsTransformAndClippingHelpers()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddPage();

        document.AddPagePathTransformed(
            0,
            [
                new PdfPathMoveTo(0, 0),
                new PdfPathLineTo(20, 0),
                new PdfPathLineTo(20, 20),
                new PdfPathClosePath(),
            ],
            PdfShapeTransform.Translate(12, 34),
            new PdfShapeOptions { FillColor = new PdfRgbColor(0.1, 0.4, 0.8), StrokeColor = null });

        document.AddPagePathClipped(
            0,
            [
                new PdfPathMoveTo(5, 5),
                new PdfPathLineTo(45, 5),
                new PdfPathLineTo(45, 45),
                new PdfPathLineTo(5, 45),
                new PdfPathClosePath(),
            ],
            [
                new PdfPathMoveTo(0, 0),
                new PdfPathLineTo(60, 60),
            ]);

        string text = Encoding.ASCII.GetString(document.Save());
        Assert.Contains("1 0 0 1 12 34 cm", text, StringComparison.Ordinal);
        Assert.Contains(" W n ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPageRoundedRectangleArcAndSectorEmitCurves()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddPage();

        document.AddPageRoundedRectangle(0, 10, 10, 100, 60, 12, 12);
        document.AddPageArc(0, 140, 80, 24, 0, 180);
        document.AddPageSector(
            0,
            200,
            80,
            24,
            30,
            150,
            new PdfShapeOptions
            {
                StrokeColor = null,
                FillColor = new PdfRgbColor(0.8, 0.7, 0.2),
            });

        string text = Encoding.ASCII.GetString(document.Save());
        Assert.Contains(" c h S Q", text, StringComparison.Ordinal);
        Assert.Contains(" c S Q", text, StringComparison.Ordinal);
        Assert.Contains(" l h f Q", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ShapeIdsCanBeListedRemovedAndReplaced()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddPage();

        document.AddPageRectangle(
            0,
            10,
            10,
            40,
            20,
            new PdfShapeOptions
            {
                ShapeId = "shape-a",
                FillColor = new PdfRgbColor(1, 0, 0),
            });
        document.AddPagePath(
            0,
            [
                new PdfPathMoveTo(80, 20),
                new PdfPathLineTo(120, 20),
                new PdfPathLineTo(120, 40),
                new PdfPathClosePath(),
            ],
            new PdfShapeOptions
            {
                ShapeId = "shape-b",
                FillColor = new PdfRgbColor(0, 0, 1),
            });

        IReadOnlyList<string> ids = document.GetPageShapeIds(0);
        Assert.Contains("shape-a", ids);
        Assert.Contains("shape-b", ids);

        document.RemovePageShape(0, "shape-a");
        Assert.DoesNotContain("shape-a", document.GetPageShapeIds(0));

        document.ReplacePageShape(
            0,
            "shape-b",
            [
                new PdfPathMoveTo(90, 30),
                new PdfPathLineTo(130, 30),
                new PdfPathLineTo(130, 50),
                new PdfPathClosePath(),
            ],
            new PdfShapeOptions
            {
                FillColor = new PdfRgbColor(0, 1, 0),
            });

        byte[] saved = document.Save();
        string text = Encoding.ASCII.GetString(saved);
        Assert.Contains("%MP_SHAPE_BEGIN:shape-b", text, StringComparison.Ordinal);
        Assert.DoesNotContain("%MP_SHAPE_BEGIN:shape-a", text, StringComparison.Ordinal);
    }
}
