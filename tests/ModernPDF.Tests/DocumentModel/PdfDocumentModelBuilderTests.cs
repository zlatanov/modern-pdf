using ModernPDF.DocumentModel;
using ModernPDF.Format;
using ModernPDF.Format.Files;
using ModernPDF.Format.Objects;
using ModernPDF.Primitives;

namespace ModernPDF.Tests.DocumentModel;

public sealed class PdfDocumentModelBuilderTests
{
    [Fact]
    public void BuildMapsCatalogPagesAndMediaBoxes()
    {
        PdfFile file = CreateSimplePageTreeFile();

        PdfDocumentModel model = PdfDocumentModelBuilder.Build(file);

        Assert.Equal(1, model.CatalogObjectId.ObjectNumber);
        Assert.Equal(2, model.PagesRootObjectId.ObjectNumber);
        Assert.Equal(2, model.Pages.Count);
        Assert.Equal(3, model.Pages[0].ObjectId.ObjectNumber);
        Assert.Equal(4, model.Pages[1].ObjectId.ObjectNumber);

        Assert.NotNull(model.Pages[0].MediaBox);
        Assert.Equal(200, model.Pages[0].MediaBox?.Right);
        Assert.Equal(700, model.Pages[1].MediaBox?.Top);
    }

    [Fact]
    public void BuildFailsWhenTrailerHasNoRootReference()
    {
        PdfFile file = new("2.0", [], new PdfDictionaryObject([]));

        Assert.Throws<PdfFormatException>(() => PdfDocumentModelBuilder.Build(file));
    }

    private static PdfFile CreateSimplePageTreeFile()
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
                    new PdfReferenceObject(new PdfObjectId(4, 0)),
                ])),
            new PdfDictionaryEntry("Count", new PdfNumberObject(2, isInteger: true)),
        ]);

        PdfDictionaryObject page1 = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Page")),
            new PdfDictionaryEntry("Parent", new PdfReferenceObject(new PdfObjectId(2, 0))),
            new PdfDictionaryEntry(
                "MediaBox",
                new PdfArrayObject(
                [
                    new PdfNumberObject(0, isInteger: true),
                    new PdfNumberObject(0, isInteger: true),
                    new PdfNumberObject(200, isInteger: true),
                    new PdfNumberObject(300, isInteger: true),
                ])),
        ]);

        PdfDictionaryObject page2 = new(
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
        ]);

        PdfDictionaryObject trailer = new(
        [
            new PdfDictionaryEntry("Root", new PdfReferenceObject(new PdfObjectId(1, 0))),
        ]);

        return new PdfFile(
            "2.0",
            [
                new PdfIndirectObject(new PdfObjectId(1, 0), catalog),
                new PdfIndirectObject(new PdfObjectId(2, 0), pages),
                new PdfIndirectObject(new PdfObjectId(3, 0), page1),
                new PdfIndirectObject(new PdfObjectId(4, 0), page2),
            ],
            trailer);
    }
}
