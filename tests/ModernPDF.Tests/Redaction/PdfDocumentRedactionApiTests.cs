using System.Text;

namespace ModernPDF.Tests.Redaction;

public sealed class PdfDocumentRedactionApiTests
{
    [Fact]
    public void SoftRedactTextWithDirectiveKeepsSuffixAndBoxesHiddenSpan()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddTextPage("Call 555-123-4567 now");

        int replacements = document.SoftRedactText(
            @"\b(\d{3})-(\d{3})-(\d{4})\b",
            static _ => PdfSoftRedactionDirective.KeepSuffix(3));
        string ascii = Encoding.ASCII.GetString(document.Save());

        Assert.Equal(1, replacements);
        Assert.Equal("Call 567 now", document.ExtractText());
        Assert.DoesNotContain("555-123-4567", ascii, StringComparison.Ordinal);
        Assert.Contains("TJ", ascii, StringComparison.Ordinal);
        Assert.Contains(" re ", ascii, StringComparison.Ordinal);
    }

    [Fact]
    public void SoftRedactTextWithLiteralReplacementTargetsTextOperatorsOnly()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddTextPage("Jane Doe");

        int replacements = document.SoftRedactText("Jane Doe", "J. Doe");

        Assert.Equal(1, replacements);
        Assert.Equal("J. Doe", document.ExtractText());
    }

    [Fact]
    public void HardRedactTextAddsBlackoutAndRemovesOriginalValue()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddTextPage("Customer SSN: 111-22-3333");

        int replacements = document.HardRedactText("111-22-3333");
        byte[] bytes = document.Save();
        string ascii = Encoding.ASCII.GetString(bytes);

        Assert.Equal(1, replacements);
        Assert.Equal("Customer SSN: ", document.ExtractText());
        Assert.DoesNotContain("111-22-3333", ascii, StringComparison.Ordinal);
        Assert.Contains("TJ", ascii, StringComparison.Ordinal);
        Assert.Contains(" re ", ascii, StringComparison.Ordinal);
    }

    [Fact]
    public void HardRedactBoundsAddsOpaqueRectangle()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddTextPage("Visible content");

        document.HardRedactBounds(0, 70, 706, 140, 18);
        byte[] bytes = document.Save();
        string ascii = Encoding.ASCII.GetString(bytes);

        Assert.Contains(" re ", ascii, StringComparison.Ordinal);
    }
}
