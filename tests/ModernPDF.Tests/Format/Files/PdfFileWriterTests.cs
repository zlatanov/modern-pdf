using System.Text;
using ModernPDF.Format.Files;
using ModernPDF.Format.Objects;
using ModernPDF.Primitives;

namespace ModernPDF.Tests.Format.Files;

public sealed class PdfFileWriterTests
{
    [Fact]
    public void WriteEmitsClassicXrefAndTrailer()
    {
        PdfFile file = CreateMinimalFile();
        byte[] bytes = PdfFileWriter.Write(file);
        string text = Encoding.ASCII.GetString(bytes);

        Assert.Contains("%PDF-2.0", text, StringComparison.Ordinal);
        Assert.Contains("xref", text, StringComparison.Ordinal);
        Assert.Contains("trailer", text, StringComparison.Ordinal);
        Assert.Contains("startxref", text, StringComparison.Ordinal);
        Assert.Contains("%%EOF", text, StringComparison.Ordinal);
    }

    private static PdfFile CreateMinimalFile()
    {
        PdfDictionaryObject catalog = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Catalog")),
        ]);

        PdfIndirectObject object1 = new(new PdfObjectId(1, 0), catalog);
        PdfDictionaryObject trailer = new(
        [
            new PdfDictionaryEntry("Root", new PdfReferenceObject(new PdfObjectId(1, 0))),
        ]);

        return new PdfFile("2.0", [object1], trailer);
    }
}
