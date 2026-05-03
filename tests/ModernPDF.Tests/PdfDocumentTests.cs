using System.Text;
using ModernPDF.Format.Files;
using ModernPDF.Format.Objects;
using ModernPDF.Primitives;

namespace ModernPDF.Tests;

public sealed class PdfDocumentTests
{
    [Fact]
    public void CreateReturnsNewDocument()
    {
        PdfDocument document = PdfDocument.Create();

        Assert.NotNull(document);
        Assert.Equal(0, document.PageCount);
        Assert.Equal("2.0", document.Version);
    }

    [Fact]
    public void SaveAndOpenRoundTripMinimalDocument()
    {
        PdfDocument original = PdfDocument.Create();
        byte[] bytes = original.Save();

        PdfDocument opened = PdfDocument.Open(bytes);

        Assert.Equal(0, opened.PageCount);
        Assert.Equal("2.0", opened.Version);
    }

    [Fact]
    public void SaveWithIncrementalModeThrowsUntilImplemented()
    {
        PdfDocument document = PdfDocument.Create();

        Assert.Throws<NotSupportedException>(() => document.Save(new PdfSaveOptions { Mode = PdfSaveMode.Incremental }));
    }

    [Fact]
    public void SaveToPathAndOpenFromPathWorks()
    {
        PdfDocument document = PdfDocument.Create();
        string path = Path.GetTempFileName();

        try
        {
            document.Save(path);
            PdfDocument reopened = PdfDocument.Open(path);
            Assert.Equal(0, reopened.PageCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExtractTextReturnsTextFromTjOperators()
    {
        byte[] pdfBytes = CreateSinglePageTextPdf("BT (Hello) Tj ( World) Tj ET");
        PdfDocument document = PdfDocument.Open(pdfBytes);

        Assert.Equal("Hello World", document.ExtractText());
        Assert.Equal("Hello World", document.ExtractText(0));
    }

    [Fact]
    public void ExtractTextReturnsTextFromTjArrayOperator()
    {
        byte[] pdfBytes = CreateSinglePageTextPdf("BT [(A) -120 (B)] TJ ET");
        PdfDocument document = PdfDocument.Open(pdfBytes);

        Assert.Equal("AB", document.ExtractText());
    }

    [Fact]
    public void ExtractTextThrowsForInvalidPageIndex()
    {
        PdfDocument document = PdfDocument.Create();

        Assert.Throws<ArgumentOutOfRangeException>(() => document.ExtractText(1));
    }

    private static byte[] CreateSinglePageTextPdf(string contentStream)
    {
        PdfDictionaryObject catalog = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Catalog")),
            new PdfDictionaryEntry("Pages", new PdfReferenceObject(new PdfObjectId(2, 0))),
        ]);

        PdfDictionaryObject pages = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Pages")),
            new PdfDictionaryEntry(
                "Kids",
                new PdfArrayObject(
                [
                    new PdfReferenceObject(new PdfObjectId(3, 0)),
                ])),
            new PdfDictionaryEntry("Count", new PdfNumberObject(1, isInteger: true)),
        ]);

        PdfDictionaryObject page = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Page")),
            new PdfDictionaryEntry("Parent", new PdfReferenceObject(new PdfObjectId(2, 0))),
            new PdfDictionaryEntry(
                "MediaBox",
                new PdfArrayObject(
                [
                    new PdfNumberObject(0, isInteger: true),
                    new PdfNumberObject(0, isInteger: true),
                    new PdfNumberObject(500, isInteger: true),
                    new PdfNumberObject(700, isInteger: true),
                ])),
            new PdfDictionaryEntry("Contents", new PdfReferenceObject(new PdfObjectId(4, 0))),
        ]);

        PdfStreamObject stream = new(
            new PdfDictionaryObject([]),
            Encoding.ASCII.GetBytes(contentStream));

        PdfDictionaryObject trailer = new(
        [
            new PdfDictionaryEntry("Root", new PdfReferenceObject(new PdfObjectId(1, 0))),
        ]);

        PdfFile file = new(
            "2.0",
            [
                new PdfIndirectObject(new PdfObjectId(1, 0), catalog),
                new PdfIndirectObject(new PdfObjectId(2, 0), pages),
                new PdfIndirectObject(new PdfObjectId(3, 0), page),
                new PdfIndirectObject(new PdfObjectId(4, 0), stream),
            ],
            trailer);

        return PdfFileWriter.Write(file);
    }
}
