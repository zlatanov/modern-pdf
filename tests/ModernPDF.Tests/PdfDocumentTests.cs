using System.Text;
using ModernPDF.DocumentModel;
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
    public void OpenWithPasswordAcceptsUnencryptedPdf()
    {
        PdfDocument original = PdfDocument.Create();
        byte[] bytes = original.Save();

        PdfDocument opened = PdfDocument.Open(bytes, "unused-password");

        Assert.Equal(0, opened.PageCount);
    }

    [Fact]
    public void SaveWithIncrementalModeThrowsUntilImplemented()
    {
        PdfDocument document = PdfDocument.Create();

        Assert.Throws<NotSupportedException>(() => document.Save(new PdfSaveOptions { Mode = PdfSaveMode.Incremental }));
    }

    [Fact]
    public void SaveWithSecurityOptionsThrowsUntilImplemented()
    {
        PdfDocument document = PdfDocument.Create();

        Assert.Throws<NotSupportedException>(
            () => document.Save(new PdfSaveOptions
            {
                Security = new PdfSecurityOptions
                {
                    UserPassword = "user-pass",
                    OwnerPassword = "owner-pass",
                    Permissions = PdfPermissions.Print | PdfPermissions.Copy,
                },
            }));
    }

    [Fact]
    public void SaveWithSecurityOptionsRejectsMissingUserPassword()
    {
        PdfDocument document = PdfDocument.Create();

        Assert.Throws<ArgumentException>(
            () => document.Save(new PdfSaveOptions
            {
                Security = new PdfSecurityOptions
                {
                    UserPassword = "",
                    Permissions = PdfPermissions.All,
                },
            }));
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
    public void OpenThrowsForEncryptedPdfUntilSecuritySupportArrives()
    {
        byte[] encryptedPdfBytes = CreateEncryptedPdf();

        Assert.Throws<NotSupportedException>(() => PdfDocument.Open(encryptedPdfBytes));
    }

    [Fact]
    public void OpenWithPasswordThrowsForEncryptedPdfUntilSecuritySupportArrives()
    {
        byte[] encryptedPdfBytes = CreateEncryptedPdf();

        Assert.Throws<NotSupportedException>(() => PdfDocument.Open(encryptedPdfBytes, "password"));
    }

    [Fact]
    public void AddPageIncreasesPageCountAndRoundTrips()
    {
        PdfDocument document = PdfDocument.Create();

        int pageIndex = document.AddPage();

        Assert.Equal(0, pageIndex);
        Assert.Equal(1, document.PageCount);

        byte[] bytes = document.Save();
        PdfDocument reopened = PdfDocument.Open(bytes);
        Assert.Equal(1, reopened.PageCount);
    }

    [Fact]
    public void AddTextPageMakesTextExtractable()
    {
        PdfDocument document = PdfDocument.Create();

        int pageIndex = document.AddTextPage("Hello ModernPDF");

        Assert.Equal(0, pageIndex);
        Assert.Equal("Hello ModernPDF", document.ExtractText());
    }

    [Fact]
    public void AddTextPageEscapesLiteralStringCharacters()
    {
        PdfDocument document = PdfDocument.Create();
        string expected = "Text (with) \\ characters";

        document.AddTextPage(expected);

        Assert.Equal(expected, document.ExtractText());
    }

    [Fact]
    public void AddPageUsesProvidedDimensions()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddPage(new PdfPageOptions { Width = 300, Height = 400 });

        byte[] bytes = document.Save();
        PdfFile file = PdfFileReader.Read(bytes);
        PdfDocumentModel model = PdfDocumentModelBuilder.Build(file);

        Assert.Single(model.Pages);
        Assert.NotNull(model.Pages[0].MediaBox);
        Assert.Equal(300, model.Pages[0].MediaBox?.Right);
        Assert.Equal(400, model.Pages[0].MediaBox?.Top);
    }

    [Fact]
    public void AddPageRejectsInvalidPageOptions()
    {
        PdfDocument document = PdfDocument.Create();

        Assert.Throws<ArgumentOutOfRangeException>(() => document.AddPage(new PdfPageOptions { Width = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.AddPage(new PdfPageOptions { Height = double.PositiveInfinity }));
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

    [Fact]
    public void ReplacePageTextUpdatesExtractedText()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddTextPage("Old text");

        document.ReplacePageText(0, "New text");

        Assert.Equal("New text", document.ExtractText());
    }

    [Fact]
    public void ReplacePageContentsRejectsInvalidPageIndex()
    {
        PdfDocument document = PdfDocument.Create();

        Assert.Throws<ArgumentOutOfRangeException>(() => document.ReplacePageContents(0, "BT (X) Tj ET"));
    }

    [Fact]
    public void SetInfoProducerPersistsThroughSaveAndOpen()
    {
        PdfDocument document = PdfDocument.Create();

        document.SetInfoProducer("ModernPDF Unit Test");
        Assert.Equal("ModernPDF Unit Test", document.GetInfoProducer());

        byte[] bytes = document.Save();
        PdfDocument reopened = PdfDocument.Open(bytes);
        Assert.Equal("ModernPDF Unit Test", reopened.GetInfoProducer());
    }

    [Fact]
    public void RedactTextRemovesOccurrencesFromExtractedText()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddTextPage("secret token secret");

        int count = document.RedactText("secret");

        Assert.Equal(2, count);
        Assert.Equal(" token ", document.ExtractText());
    }

    [Fact]
    public void RedactTextPersistsThroughSaveAndOpen()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddTextPage("hello world");
        document.RedactText("world", "[REDACTED]");

        byte[] bytes = document.Save();
        PdfDocument reopened = PdfDocument.Open(bytes);

        Assert.Equal("hello [REDACTED]", reopened.ExtractText());
    }

    [Fact]
    public void RedactTextReturnsZeroWhenTargetIsMissing()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddTextPage("alpha");

        int count = document.RedactText("beta");

        Assert.Equal(0, count);
        Assert.Equal("alpha", document.ExtractText());
    }

    [Fact]
    public void RedactTextRejectsEmptyTarget()
    {
        PdfDocument document = PdfDocument.Create();

        Assert.Throws<ArgumentException>(() => document.RedactText(""));
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

    private static byte[] CreateEncryptedPdf()
    {
        PdfDictionaryObject catalog = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Catalog")),
            new PdfDictionaryEntry("Pages", new PdfReferenceObject(new PdfObjectId(2, 0))),
        ]);

        PdfDictionaryObject pages = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Pages")),
            new PdfDictionaryEntry("Kids", new PdfArrayObject([])),
            new PdfDictionaryEntry("Count", new PdfNumberObject(0, isInteger: true)),
        ]);

        PdfDictionaryObject encryptDictionary = new(
        [
            new PdfDictionaryEntry("Filter", new PdfNameObject("Standard")),
            new PdfDictionaryEntry("V", new PdfNumberObject(4, isInteger: true)),
        ]);

        PdfDictionaryObject trailer = new(
        [
            new PdfDictionaryEntry("Root", new PdfReferenceObject(new PdfObjectId(1, 0))),
            new PdfDictionaryEntry("Encrypt", new PdfReferenceObject(new PdfObjectId(3, 0))),
        ]);

        PdfFile file = new(
            "2.0",
            [
                new PdfIndirectObject(new PdfObjectId(1, 0), catalog),
                new PdfIndirectObject(new PdfObjectId(2, 0), pages),
                new PdfIndirectObject(new PdfObjectId(3, 0), encryptDictionary),
            ],
            trailer);

        return PdfFileWriter.Write(file);
    }
}
