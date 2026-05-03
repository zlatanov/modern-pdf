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
}
