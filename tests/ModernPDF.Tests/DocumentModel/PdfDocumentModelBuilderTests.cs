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
        Assert.Equal(6, model.MetadataObjectId?.ObjectNumber);
        Assert.Equal(7, model.InfoObjectId?.ObjectNumber);
        Assert.Equal(2, model.Pages.Count);
        Assert.Equal(3, model.Pages[0].ObjectId.ObjectNumber);
        Assert.Equal(4, model.Pages[1].ObjectId.ObjectNumber);

        Assert.NotNull(model.Pages[0].MediaBox);
        Assert.Equal(200, model.Pages[0].MediaBox?.Right);
        Assert.Equal(700, model.Pages[1].MediaBox?.Top);
        PdfReferenceObject inheritedResources = Assert.IsType<PdfReferenceObject>(model.Pages[0].Resources);
        Assert.Equal(5, inheritedResources.ObjectId.ObjectNumber);
        PdfReferenceObject contents = Assert.IsType<PdfReferenceObject>(model.Pages[0].Contents);
        Assert.Equal(8, contents.ObjectId.ObjectNumber);

        PdfObjectId pageId = model.Pages[0].ObjectId;
        Assert.False(model.Mutations.IsDirty(pageId));
        model.Mutations.MarkDirty(pageId);
        Assert.True(model.Mutations.IsDirty(pageId));
        Assert.Single(model.Mutations.DirtyObjectIds);
        model.Mutations.MarkClean(pageId);
        Assert.False(model.Mutations.IsDirty(pageId));
    }

    [Fact]
    public void BuildFailsWhenTrailerHasNoRootReference()
    {
        PdfFile file = new("2.0", [], new PdfDictionaryObject([]));

        Assert.Throws<PdfFormatException>(() => PdfDocumentModelBuilder.Build(file));
    }

    [Fact]
    public void BuildFailsWhenRootEntryIsNotReference()
    {
        PdfDictionaryObject trailer = new(
        [
            new PdfDictionaryEntry("Root", new PdfStringObject("not-a-reference")),
        ]);
        PdfFile file = new("2.0", [], trailer);

        Assert.Throws<PdfFormatException>(() => PdfDocumentModelBuilder.Build(file));
    }

    [Fact]
    public void BuildFailsWhenCatalogPagesEntryIsNotReference()
    {
        PdfDictionaryObject catalog = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Catalog")),
            new PdfDictionaryEntry("Pages", new PdfStringObject("invalid")),
        ]);
        PdfFile file = CreateMinimalTreeFile(catalog, CreatePagesNodeWithSinglePage(), CreatePageNode(), trailerInfo: null);

        Assert.Throws<PdfFormatException>(() => PdfDocumentModelBuilder.Build(file));
    }

    [Fact]
    public void BuildFailsWhenPagesKidsIsNotArray()
    {
        PdfDictionaryObject pages = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Pages")),
            new PdfDictionaryEntry("Kids", new PdfStringObject("invalid")),
            new PdfDictionaryEntry("Count", new PdfNumberObject(1, isInteger: true)),
        ]);
        PdfFile file = CreateMinimalTreeFile(CreateCatalogNode(), pages, CreatePageNode(), trailerInfo: null);

        Assert.Throws<PdfFormatException>(() => PdfDocumentModelBuilder.Build(file));
    }

    [Fact]
    public void BuildFailsWhenPageTreeContainsCycle()
    {
        PdfDictionaryObject pages = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Pages")),
            new PdfDictionaryEntry("Kids", new PdfArrayObject([new PdfReferenceObject(new PdfObjectId(2, 0))])),
            new PdfDictionaryEntry("Count", new PdfNumberObject(1, isInteger: true)),
        ]);
        PdfFile file = CreateMinimalTreeFile(CreateCatalogNode(), pages, CreatePageNode(), trailerInfo: null);

        Assert.Throws<PdfFormatException>(() => PdfDocumentModelBuilder.Build(file));
    }

    [Fact]
    public void BuildFailsWhenOptionalInfoIsNotReference()
    {
        PdfDictionaryObject trailer = new(
        [
            new PdfDictionaryEntry("Root", new PdfReferenceObject(new PdfObjectId(1, 0))),
            new PdfDictionaryEntry("Info", new PdfStringObject("invalid")),
        ]);
        PdfFile file = new(
            "2.0",
            [
                new PdfIndirectObject(new PdfObjectId(1, 0), CreateCatalogNode()),
                new PdfIndirectObject(new PdfObjectId(2, 0), CreatePagesNodeWithSinglePage()),
                new PdfIndirectObject(new PdfObjectId(3, 0), CreatePageNode()),
            ],
            trailer);

        Assert.Throws<PdfFormatException>(() => PdfDocumentModelBuilder.Build(file));
    }

    [Fact]
    public void BuildFailsWhenPageMediaBoxContainsNonNumericEntry()
    {
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
                    new PdfStringObject("bad"),
                    new PdfNumberObject(100, isInteger: true),
                ])),
        ]);
        PdfFile file = CreateMinimalTreeFile(CreateCatalogNode(), CreatePagesNodeWithSinglePage(), page, trailerInfo: null);

        Assert.Throws<PdfFormatException>(() => PdfDocumentModelBuilder.Build(file));
    }

    [Fact]
    public void BuildFailsWhenRootReferencePointsToMissingObject()
    {
        PdfDictionaryObject trailer = new(
        [
            new PdfDictionaryEntry("Root", new PdfReferenceObject(new PdfObjectId(99, 0))),
        ]);
        PdfFile file = new("2.0", [], trailer);

        Assert.Throws<PdfFormatException>(() => PdfDocumentModelBuilder.Build(file));
    }

    [Fact]
    public void BuildAllowsPageWithoutMediaBox()
    {
        PdfDictionaryObject page = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Page")),
            new PdfDictionaryEntry("Parent", new PdfReferenceObject(new PdfObjectId(2, 0))),
        ]);
        PdfFile file = CreateMinimalTreeFile(CreateCatalogNode(), CreatePagesNodeWithSinglePage(), page, trailerInfo: null);

        PdfDocumentModel model = PdfDocumentModelBuilder.Build(file);

        Assert.Single(model.Pages);
        Assert.Null(model.Pages[0].MediaBox);
    }

    [Fact]
    public void BuildFailsWhenPageTreeNodeTypeIsUnsupported()
    {
        PdfDictionaryObject pages = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("UnknownNode")),
            new PdfDictionaryEntry("Kids", new PdfArrayObject([new PdfReferenceObject(new PdfObjectId(3, 0))])),
        ]);
        PdfFile file = CreateMinimalTreeFile(CreateCatalogNode(), pages, CreatePageNode(), trailerInfo: null);

        Assert.Throws<PdfFormatException>(() => PdfDocumentModelBuilder.Build(file));
    }

    [Fact]
    public void BuildFailsWhenPageTreeKidsContainsNonReference()
    {
        PdfDictionaryObject pages = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Pages")),
            new PdfDictionaryEntry("Kids", new PdfArrayObject([new PdfStringObject("invalid")])),
            new PdfDictionaryEntry("Count", new PdfNumberObject(1, isInteger: true)),
        ]);
        PdfFile file = CreateMinimalTreeFile(CreateCatalogNode(), pages, CreatePageNode(), trailerInfo: null);

        Assert.Throws<PdfFormatException>(() => PdfDocumentModelBuilder.Build(file));
    }

    [Fact]
    public void BuildFailsWhenPageMediaBoxIsNotFourNumbers()
    {
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
                    new PdfNumberObject(100, isInteger: true),
                ])),
        ]);
        PdfFile file = CreateMinimalTreeFile(CreateCatalogNode(), CreatePagesNodeWithSinglePage(), page, trailerInfo: null);

        Assert.Throws<PdfFormatException>(() => PdfDocumentModelBuilder.Build(file));
    }

    [Fact]
    public void BuildFailsWhenTypeEntryIsMissingOrNotName()
    {
        PdfDictionaryObject missingTypePage = new(
        [
            new PdfDictionaryEntry("Parent", new PdfReferenceObject(new PdfObjectId(2, 0))),
        ]);
        PdfFile missingTypeFile = CreateMinimalTreeFile(CreateCatalogNode(), CreatePagesNodeWithSinglePage(), missingTypePage, trailerInfo: null);

        PdfDictionaryObject nonNameTypePage = new(
        [
            new PdfDictionaryEntry("Type", new PdfStringObject("Page")),
            new PdfDictionaryEntry("Parent", new PdfReferenceObject(new PdfObjectId(2, 0))),
        ]);
        PdfFile nonNameTypeFile = CreateMinimalTreeFile(CreateCatalogNode(), CreatePagesNodeWithSinglePage(), nonNameTypePage, trailerInfo: null);

        Assert.Throws<PdfFormatException>(() => PdfDocumentModelBuilder.Build(missingTypeFile));
        Assert.Throws<PdfFormatException>(() => PdfDocumentModelBuilder.Build(nonNameTypeFile));
    }

    private static PdfFile CreateSimplePageTreeFile()
    {
        PdfDictionaryObject catalog = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Catalog")),
            new PdfDictionaryEntry("Pages", new PdfReferenceObject(new PdfObjectId(2, 0))),
            new PdfDictionaryEntry("Metadata", new PdfReferenceObject(new PdfObjectId(6, 0))),
        ]);

        PdfDictionaryObject pages = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Pages")),
            new PdfDictionaryEntry("Resources", new PdfReferenceObject(new PdfObjectId(5, 0))),
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
            new PdfDictionaryEntry("Contents", new PdfReferenceObject(new PdfObjectId(8, 0))),
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
            new PdfDictionaryEntry("Contents", new PdfArrayObject([new PdfReferenceObject(new PdfObjectId(8, 0))])),
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
            new PdfDictionaryEntry("Info", new PdfReferenceObject(new PdfObjectId(7, 0))),
        ]);

        PdfDictionaryObject resources = new(
        [
            new PdfDictionaryEntry("ProcSet", new PdfArrayObject([new PdfNameObject("PDF")])),
        ]);

        PdfDictionaryObject metadata = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Metadata")),
        ]);

        PdfDictionaryObject info = new(
        [
            new PdfDictionaryEntry("Producer", new PdfStringObject("ModernPDF")),
        ]);

        PdfStreamObject contents = new(
            new PdfDictionaryObject([]),
            System.Text.Encoding.ASCII.GetBytes("BT ET"));

        return new PdfFile(
            "2.0",
            [
                new PdfIndirectObject(new PdfObjectId(1, 0), catalog),
                new PdfIndirectObject(new PdfObjectId(2, 0), pages),
                new PdfIndirectObject(new PdfObjectId(3, 0), page1),
                new PdfIndirectObject(new PdfObjectId(4, 0), page2),
                new PdfIndirectObject(new PdfObjectId(5, 0), resources),
                new PdfIndirectObject(new PdfObjectId(6, 0), metadata),
                new PdfIndirectObject(new PdfObjectId(7, 0), info),
                new PdfIndirectObject(new PdfObjectId(8, 0), contents),
            ],
            trailer);
    }

    private static PdfFile CreateMinimalTreeFile(
        PdfDictionaryObject catalog,
        PdfDictionaryObject pages,
        PdfDictionaryObject page,
        PdfObject? trailerInfo)
    {
        List<PdfDictionaryEntry> trailerEntries =
        [
            new PdfDictionaryEntry("Root", new PdfReferenceObject(new PdfObjectId(1, 0))),
        ];
        if (trailerInfo is not null)
        {
            trailerEntries.Add(new PdfDictionaryEntry("Info", trailerInfo));
        }

        PdfDictionaryObject trailer = new(trailerEntries);
        return new PdfFile(
            "2.0",
            [
                new PdfIndirectObject(new PdfObjectId(1, 0), catalog),
                new PdfIndirectObject(new PdfObjectId(2, 0), pages),
                new PdfIndirectObject(new PdfObjectId(3, 0), page),
            ],
            trailer);
    }

    private static PdfDictionaryObject CreateCatalogNode()
    {
        return new PdfDictionaryObject(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Catalog")),
            new PdfDictionaryEntry("Pages", new PdfReferenceObject(new PdfObjectId(2, 0))),
        ]);
    }

    private static PdfDictionaryObject CreatePagesNodeWithSinglePage()
    {
        return new PdfDictionaryObject(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Pages")),
            new PdfDictionaryEntry("Kids", new PdfArrayObject([new PdfReferenceObject(new PdfObjectId(3, 0))])),
            new PdfDictionaryEntry("Count", new PdfNumberObject(1, isInteger: true)),
        ]);
    }

    private static PdfDictionaryObject CreatePageNode()
    {
        return new PdfDictionaryObject(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Page")),
            new PdfDictionaryEntry("Parent", new PdfReferenceObject(new PdfObjectId(2, 0))),
            new PdfDictionaryEntry(
                "MediaBox",
                new PdfArrayObject(
                [
                    new PdfNumberObject(0, isInteger: true),
                    new PdfNumberObject(0, isInteger: true),
                    new PdfNumberObject(100, isInteger: true),
                    new PdfNumberObject(100, isInteger: true),
                ])),
        ]);
    }
}
