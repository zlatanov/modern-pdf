using System.Text;
using ModernPDF.DocumentModel;
using ModernPDF.Format;
using ModernPDF.Format.Files;
using ModernPDF.Format.Objects;
using ModernPDF.Primitives;
using ModernPDF.Text;

namespace ModernPDF.Tests.Text;

public sealed class PdfTextExtractorTests
{
    [Fact]
    public void ExtractAllRejectsNullArguments()
    {
        PdfFile file = CreateFile([]);
        PdfDocumentModel model = CreateModel([]);

        Assert.Throws<ArgumentNullException>(() => PdfTextExtractor.ExtractAll(null!, model));
        Assert.Throws<ArgumentNullException>(() => PdfTextExtractor.ExtractAll(file, null!));
    }

    [Fact]
    public void ExtractPageRejectsInvalidPageIndex()
    {
        PdfPageModel page = new(new PdfObjectId(3, 0), null, null, null);
        PdfFile file = CreateFile([]);
        PdfDocumentModel model = CreateModel([page]);

        Assert.Throws<ArgumentOutOfRangeException>(() => PdfTextExtractor.ExtractPage(file, model, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfTextExtractor.ExtractPage(file, model, 1));
    }

    [Fact]
    public void ExtractAllSkipsPagesWithoutTextAndAddsSeparator()
    {
        PdfStreamObject textStream = CreateStream("BT (First) Tj ET");
        PdfStreamObject emptyStream = CreateStream("q Q");
        PdfFile file = CreateFile(
        [
            new PdfIndirectObject(new PdfObjectId(10, 0), textStream),
            new PdfIndirectObject(new PdfObjectId(11, 0), emptyStream),
        ]);

        PdfDocumentModel model = CreateModel(
        [
            new PdfPageModel(new PdfObjectId(3, 0), null, null, new PdfReferenceObject(new PdfObjectId(10, 0))),
            new PdfPageModel(new PdfObjectId(4, 0), null, null, new PdfReferenceObject(new PdfObjectId(11, 0))),
            new PdfPageModel(new PdfObjectId(5, 0), null, null, new PdfReferenceObject(new PdfObjectId(10, 0))),
        ]);

        string text = PdfTextExtractor.ExtractAll(file, model);

        Assert.Equal("First\nFirst", text);
    }

    [Fact]
    public void ExtractPageSupportsSingleStringTextOperators()
    {
        PdfStreamObject stream = CreateStream("BT (A) Tj (B) ' (C) \" ET");
        PdfFile file = CreateFile([new PdfIndirectObject(new PdfObjectId(10, 0), stream)]);
        PdfDocumentModel model = CreateModel(
        [
            new PdfPageModel(new PdfObjectId(3, 0), null, null, new PdfReferenceObject(new PdfObjectId(10, 0))),
        ]);

        string text = PdfTextExtractor.ExtractPage(file, model, 0);

        Assert.Equal("ABC", text);
    }

    [Fact]
    public void ExtractPageSupportsTjArrayAndNestedArrayDepth()
    {
        PdfStreamObject stream = CreateStream("BT [(A) 20 [(Ignored)] (B)] TJ ET");
        PdfFile file = CreateFile([new PdfIndirectObject(new PdfObjectId(10, 0), stream)]);
        PdfDocumentModel model = CreateModel(
        [
            new PdfPageModel(new PdfObjectId(3, 0), null, null, new PdfReferenceObject(new PdfObjectId(10, 0))),
        ]);

        string text = PdfTextExtractor.ExtractPage(file, model, 0);

        Assert.Equal("AB", text);
    }

    [Fact]
    public void ExtractPageRejectsInvalidContentsShapes()
    {
        PdfFile file = CreateFile([]);
        PdfDocumentModel directStreamModel = CreateModel(
        [
            new PdfPageModel(new PdfObjectId(3, 0), null, null, new PdfStringObject("invalid")),
        ]);
        PdfDocumentModel invalidArrayModel = CreateModel(
        [
            new PdfPageModel(new PdfObjectId(3, 0), null, null, new PdfArrayObject([new PdfStringObject("invalid")])),
        ]);

        Assert.Throws<PdfFormatException>(() => PdfTextExtractor.ExtractPage(file, directStreamModel, 0));
        Assert.Throws<PdfFormatException>(() => PdfTextExtractor.ExtractPage(file, invalidArrayModel, 0));
    }

    [Fact]
    public void ExtractPageRejectsMissingOrNonStreamReferencedContents()
    {
        PdfDocumentModel model = CreateModel(
        [
            new PdfPageModel(new PdfObjectId(3, 0), null, null, new PdfReferenceObject(new PdfObjectId(10, 0))),
        ]);

        PdfFile missingFile = CreateFile([]);
        PdfFile nonStreamFile = CreateFile([new PdfIndirectObject(new PdfObjectId(10, 0), new PdfStringObject("x"))]);

        Assert.Throws<PdfFormatException>(() => PdfTextExtractor.ExtractPage(missingFile, model, 0));
        Assert.Throws<PdfFormatException>(() => PdfTextExtractor.ExtractPage(nonStreamFile, model, 0));
    }

    [Fact]
    public void ExtractPageRejectsUnterminatedArrayInContentStream()
    {
        PdfStreamObject stream = CreateStream("BT [(A) (B) TJ ET");
        PdfFile file = CreateFile([new PdfIndirectObject(new PdfObjectId(10, 0), stream)]);
        PdfDocumentModel model = CreateModel(
        [
            new PdfPageModel(new PdfObjectId(3, 0), null, null, new PdfReferenceObject(new PdfObjectId(10, 0))),
        ]);

        Assert.Throws<PdfFormatException>(() => PdfTextExtractor.ExtractPage(file, model, 0));
    }

    private static PdfFile CreateFile(IEnumerable<PdfIndirectObject> objects)
    {
        PdfDictionaryObject trailer = new(
        [
            new PdfDictionaryEntry("Root", new PdfReferenceObject(new PdfObjectId(1, 0))),
        ]);

        return new PdfFile("2.0", objects, trailer);
    }

    private static PdfDocumentModel CreateModel(IEnumerable<PdfPageModel> pages)
    {
        return new PdfDocumentModel(
            new PdfObjectId(1, 0),
            new PdfObjectId(2, 0),
            metadataObjectId: null,
            infoObjectId: null,
            pages);
    }

    private static PdfStreamObject CreateStream(string content)
    {
        return new PdfStreamObject(new PdfDictionaryObject([]), Encoding.ASCII.GetBytes(content));
    }
}
