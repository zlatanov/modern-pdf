using System.Text;

namespace ModernPDF.Tests;

public sealed class PdfDocumentShapeSupportTests
{
    [Fact]
    public void AddPageLinePreservesExistingPageText()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddTextPage("before-shape");

        document.AddPageLine(0, 10, 20, 60, 80);
        byte[] saved = document.Save();
        string text = Encoding.ASCII.GetString(saved);

        Assert.Equal("before-shape", PdfDocument.Open(saved).ExtractText());
        Assert.Contains("10 20 m 60 80 l S Q", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPageRectangleSupportsStrokeAndFill()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddPage(new PdfPageOptions { Width = 200, Height = 200 });

        document.AddPageRectangle(
            0,
            5,
            6,
            40,
            20,
            new PdfShapeOptions
            {
                StrokeColor = new PdfRgbColor(1, 0, 0),
                FillColor = new PdfRgbColor(0, 0, 1),
                StrokeWidth = 2,
            });

        string text = Encoding.ASCII.GetString(document.Save());

        Assert.Contains("1 0 0 RG", text, StringComparison.Ordinal);
        Assert.Contains("0 0 1 rg", text, StringComparison.Ordinal);
        Assert.Contains("5 6 40 20 re B Q", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPageCircleSupportsFillOnly()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddPage();

        document.AddPageCircle(
            0,
            100,
            120,
            30,
            new PdfShapeOptions
            {
                StrokeColor = null,
                FillColor = new PdfRgbColor(0.2, 0.4, 0.6),
            });

        string text = Encoding.ASCII.GetString(document.Save());

        Assert.Contains("0.2 0.4 0.6 rg", text, StringComparison.Ordinal);
        Assert.Contains(" c h f Q", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" RG ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPageShapesCanBeComposedOnOnePage()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddPage(new PdfPageOptions { Width = 240, Height = 160 });

        document.AddPageLine(0, 0, 0, 50, 50);
        document.AddPageRectangle(0, 10, 10, 20, 15);
        document.AddPageCircle(0, 70, 40, 12);

        string text = Encoding.ASCII.GetString(document.Save());
        Assert.Equal(3, CountOccurrences(text, " S Q"));
    }

    [Fact]
    public void AddPageRectangleThrowsForInvalidDimensions()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddPage();

        Assert.Throws<ArgumentOutOfRangeException>(() => document.AddPageRectangle(0, 10, 10, 0, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.AddPageRectangle(0, 10, 10, 5, -1));
    }

    [Fact]
    public void AddPageLineThrowsWhenStrokeIsDisabled()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddPage();

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => document.AddPageLine(
                0,
                0,
                0,
                10,
                10,
                new PdfShapeOptions
                {
                    StrokeColor = null,
                    FillColor = new PdfRgbColor(1, 0, 0),
                }));

        Assert.Contains("StrokeColor", exception.Message, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string token)
    {
        int count = 0;
        int index = 0;
        while (true)
        {
            int found = source.IndexOf(token, index, StringComparison.Ordinal);
            if (found < 0)
            {
                return count;
            }

            count++;
            index = found + token.Length;
        }
    }
}
