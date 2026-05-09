using System.Text;

namespace ModernPDF.Tests.Redaction;

public sealed class PdfDocumentLocationRedactionTests
{
    [Fact]
    public void ExtractTextRegionsReturnsCoordinates()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddTextPage("Sofia is city");

        IReadOnlyList<PdfTextRegion> regions = document.ExtractTextRegions(0);

        PdfTextRegion region = Assert.Single(regions);
        Assert.Equal(0, region.PageIndex);
        Assert.Equal("Sofia is city", region.Text);
        Assert.True(region.Width > 0);
        Assert.True(region.Height > 0);
    }

    [Fact]
    public void FindTextAndHardRedactTextMatchesTargetsOnlySelectedOccurrence()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddTextPage("Sofia is city");
        document.AddTextPage("Sofia is person");

        IReadOnlyList<PdfTextMatch> matches = document.FindText(@"\bSofia\b");
        PdfTextMatch pageOneMatch = Assert.Single(matches, match => match.PageIndex == 1);

        int replacements = document.HardRedactText([pageOneMatch]);
        string extracted = document.ExtractText();
        string ascii = Encoding.ASCII.GetString(document.Save());

        Assert.Equal(1, replacements);
        Assert.Equal("Sofia is city\n is person", extracted);
        Assert.Contains(" re ", ascii, StringComparison.Ordinal);
    }
}
