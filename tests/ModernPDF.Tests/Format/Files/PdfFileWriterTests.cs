using System.Text;
using ModernPDF;
using ModernPDF.Format;
using ModernPDF.Format.Files;
using ModernPDF.Format.Objects;
using ModernPDF.Primitives;

namespace ModernPDF.Tests.Format.Files;

public sealed class PdfFileWriterTests
{
    [Fact]
    public void WriteRejectsNullFile()
    {
        Assert.Throws<ArgumentNullException>(() => PdfFileWriter.Write(null!));
    }

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

    [Fact]
    public void WriteEmitsStreamObjectMarkersAndLength()
    {
        PdfFile file = CreateStreamFile();
        byte[] bytes = PdfFileWriter.Write(file);
        string text = Encoding.ASCII.GetString(bytes);

        Assert.Contains("stream", text, StringComparison.Ordinal);
        Assert.Contains("endstream", text, StringComparison.Ordinal);
        Assert.Contains("/Length 5", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteReplacesExistingStreamLengthEntry()
    {
        PdfDictionaryObject streamDictionary = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("DemoStream")),
            new PdfDictionaryEntry("Length", new PdfNumberObject(999, isInteger: true)),
        ]);
        PdfStreamObject stream = new(streamDictionary, Encoding.ASCII.GetBytes("Hi"));
        PdfFile file = new(
            "2.0",
            [new PdfIndirectObject(new PdfObjectId(1, 0), stream)],
            new PdfDictionaryObject([new PdfDictionaryEntry("Root", new PdfReferenceObject(new PdfObjectId(1, 0)))]));

        string text = Encoding.ASCII.GetString(PdfFileWriter.Write(file));

        Assert.Contains("/Length 2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/Length 999", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteEmitsFreeXrefEntriesForMissingObjectNumbers()
    {
        PdfFile file = new(
            "2.0",
            [
                new PdfIndirectObject(new PdfObjectId(1, 0), new PdfDictionaryObject([new PdfDictionaryEntry("Type", new PdfNameObject("Catalog"))])),
                new PdfIndirectObject(new PdfObjectId(3, 0), new PdfDictionaryObject([new PdfDictionaryEntry("Type", new PdfNameObject("Page"))])),
            ],
            new PdfDictionaryObject([new PdfDictionaryEntry("Root", new PdfReferenceObject(new PdfObjectId(1, 0)))]));

        string text = Encoding.ASCII.GetString(PdfFileWriter.Write(file));

        Assert.Contains("0 4", text, StringComparison.Ordinal);
        Assert.Contains("0000000000 00000 f", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteAddsOrReplacesTrailerSizeEntry()
    {
        PdfFile file = new(
            "2.0",
            [new PdfIndirectObject(new PdfObjectId(1, 0), new PdfDictionaryObject([new PdfDictionaryEntry("Type", new PdfNameObject("Catalog"))]))],
            new PdfDictionaryObject(
            [
                new PdfDictionaryEntry("Root", new PdfReferenceObject(new PdfObjectId(1, 0))),
                new PdfDictionaryEntry("Size", new PdfNumberObject(999, isInteger: true)),
            ]));

        string text = Encoding.ASCII.GetString(PdfFileWriter.Write(file));

        Assert.Contains("/Size 2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/Size 999", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteWithXrefStreamAndObjectStreamRoundTrips()
    {
        PdfFile file = CreateTwoObjectFile();
        byte[] bytes = PdfFileWriter.Write(file, PdfCrossReferenceStyle.Stream);
        string text = Encoding.ASCII.GetString(bytes);

        Assert.Contains("/Type /XRef", text, StringComparison.Ordinal);
        Assert.Contains("/Type /ObjStm", text, StringComparison.Ordinal);

        PdfFile parsed = PdfFileReader.Read(bytes);
        Assert.True(parsed.Objects.Count >= 2);
        PdfDictionaryObject pages = Assert.IsType<PdfDictionaryObject>(
            parsed.Objects.Single(static objectItem => objectItem.ObjectId.ObjectNumber == 2).Value);
        PdfNameObject pagesType = Assert.IsType<PdfNameObject>(
            Assert.Single(pages.Entries, static entry => entry.Key == "Type").Value);
        Assert.Equal("Pages", pagesType.Value);
    }

    [Fact]
    public void WriteIncrementalAppendsUpdatedObjectAndPrevTrailerEntry()
    {
        PdfFile original = CreateMinimalFile();
        byte[] fullBytes = PdfFileWriter.Write(original);
        PdfFile parsed = PdfFileReader.Read(fullBytes);

        PdfDictionaryObject updatedCatalog = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Catalog")),
            new PdfDictionaryEntry("Version", new PdfNameObject("2.0")),
        ]);
        PdfFile updated = new(
            parsed.Version,
            [new PdfIndirectObject(new PdfObjectId(1, 0), updatedCatalog)],
            parsed.Trailer,
            parsed.SourceBytes,
            parsed.StartXrefOffset,
            parsed.XrefEntries);

        byte[] incrementalBytes = PdfFileWriter.WriteIncremental(updated, [new PdfObjectId(1, 0)]);
        string text = Encoding.ASCII.GetString(incrementalBytes);
        PdfFile reparsed = PdfFileReader.Read(incrementalBytes);
        PdfDictionaryObject dictionary = Assert.IsType<PdfDictionaryObject>(reparsed.Objects[0].Value);

        Assert.True(incrementalBytes.Length > fullBytes.Length);
        Assert.Equal(fullBytes, incrementalBytes.Take(fullBytes.Length).ToArray());
        Assert.Contains("/Prev", text, StringComparison.Ordinal);
        Assert.Contains(dictionary.Entries, static entry => entry.Key == "Version");
    }

    [Fact]
    public void WriteIncrementalWithXrefStreamAppendsUpdatedObjectAndPrevEntry()
    {
        PdfFile original = CreateMinimalFile();
        byte[] fullBytes = PdfFileWriter.Write(original);
        PdfFile parsed = PdfFileReader.Read(fullBytes);

        PdfDictionaryObject updatedCatalog = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Catalog")),
            new PdfDictionaryEntry("Version", new PdfNameObject("2.0")),
        ]);
        PdfFile updated = new(
            parsed.Version,
            [new PdfIndirectObject(new PdfObjectId(1, 0), updatedCatalog)],
            parsed.Trailer,
            parsed.SourceBytes,
            parsed.StartXrefOffset,
            parsed.XrefEntries);

        byte[] incrementalBytes = PdfFileWriter.WriteIncremental(updated, [new PdfObjectId(1, 0)], PdfCrossReferenceStyle.Stream);
        string text = Encoding.ASCII.GetString(incrementalBytes);
        PdfFile reparsed = PdfFileReader.Read(incrementalBytes);
        PdfDictionaryObject dictionary = Assert.IsType<PdfDictionaryObject>(reparsed.Objects[0].Value);

        Assert.True(incrementalBytes.Length > fullBytes.Length);
        Assert.Equal(fullBytes, incrementalBytes.Take(fullBytes.Length).ToArray());
        Assert.Contains("/Type /XRef", text, StringComparison.Ordinal);
        Assert.Contains("/Prev", text, StringComparison.Ordinal);
        Assert.Contains(dictionary.Entries, static entry => entry.Key == "Version");
    }

    [Fact]
    public void WriteIncrementalWithXrefStreamFallsBackToFullStreamWhenNoSourceMetadataExists()
    {
        PdfFile file = CreateMinimalFile();
        byte[] fullStreamBytes = PdfFileWriter.Write(file, PdfCrossReferenceStyle.Stream);
        byte[] incrementalBytes = PdfFileWriter.WriteIncremental(file, [new PdfObjectId(1, 0)], PdfCrossReferenceStyle.Stream);

        Assert.Equal(fullStreamBytes, incrementalBytes);
        Assert.Contains("/Type /XRef", Encoding.ASCII.GetString(incrementalBytes), StringComparison.Ordinal);
    }

    [Fact]
    public void WriteIncrementalClassicAfterStreamFullSaveRoundTrips()
    {
        PdfFile original = CreateMinimalFile();
        byte[] fullStreamBytes = PdfFileWriter.Write(original, PdfCrossReferenceStyle.Stream);
        PdfFile parsed = PdfFileReader.Read(fullStreamBytes);
        PdfFile updated = CreateUpdatedCatalogFile(parsed);

        byte[] incrementalBytes = PdfFileWriter.WriteIncremental(updated, [new PdfObjectId(1, 0)], PdfCrossReferenceStyle.Classic);
        string text = Encoding.ASCII.GetString(incrementalBytes);
        PdfFile reparsed = PdfFileReader.Read(incrementalBytes);
        PdfDictionaryObject dictionary = Assert.IsType<PdfDictionaryObject>(reparsed.Objects[0].Value);

        Assert.True(incrementalBytes.Length > fullStreamBytes.Length);
        Assert.Equal(fullStreamBytes, incrementalBytes.Take(fullStreamBytes.Length).ToArray());
        Assert.Contains("\nxref\n", text, StringComparison.Ordinal);
        Assert.Contains("/Prev", text, StringComparison.Ordinal);
        Assert.Contains(dictionary.Entries, static entry => entry.Key == "Version");
    }

    [Fact]
    public void WriteIncrementalStreamAfterStreamFullSaveRoundTrips()
    {
        PdfFile original = CreateMinimalFile();
        byte[] fullStreamBytes = PdfFileWriter.Write(original, PdfCrossReferenceStyle.Stream);
        PdfFile parsed = PdfFileReader.Read(fullStreamBytes);
        PdfFile updated = CreateUpdatedCatalogFile(parsed);

        byte[] incrementalBytes = PdfFileWriter.WriteIncremental(updated, [new PdfObjectId(1, 0)], PdfCrossReferenceStyle.Stream);
        string text = Encoding.ASCII.GetString(incrementalBytes);
        PdfFile reparsed = PdfFileReader.Read(incrementalBytes);
        PdfDictionaryObject dictionary = Assert.IsType<PdfDictionaryObject>(reparsed.Objects[0].Value);

        Assert.True(incrementalBytes.Length > fullStreamBytes.Length);
        Assert.Equal(fullStreamBytes, incrementalBytes.Take(fullStreamBytes.Length).ToArray());
        Assert.True(text.Split("/Type /XRef", StringSplitOptions.None).Length - 1 >= 2);
        Assert.Contains("/Prev", text, StringComparison.Ordinal);
        Assert.Contains(dictionary.Entries, static entry => entry.Key == "Version");
    }

    [Fact]
    public void WriteIncrementalFallsBackToFullWhenNoSourceMetadataExists()
    {
        PdfFile file = CreateMinimalFile();
        byte[] fullBytes = PdfFileWriter.Write(file);
        byte[] incrementalBytes = PdfFileWriter.WriteIncremental(file, [new PdfObjectId(1, 0)]);

        Assert.Equal(fullBytes, incrementalBytes);
    }

    [Fact]
    public void WriteIncrementalFallsBackToFullWhenXrefMetadataIsOutOfRange()
    {
        PdfFile original = CreateMinimalFile();
        byte[] fullBytes = PdfFileWriter.Write(original);
        PdfFile parsed = PdfFileReader.Read(fullBytes);
        Dictionary<int, PdfXrefEntry> invalidEntries = new(parsed.XrefEntries)
        {
            [1] = new PdfXrefEntry(999_999, 0),
        };

        PdfFile invalidMetadataFile = new(
            parsed.Version,
            parsed.Objects,
            parsed.Trailer,
            parsed.SourceBytes,
            parsed.StartXrefOffset,
            invalidEntries);

        byte[] incrementalBytes = PdfFileWriter.WriteIncremental(invalidMetadataFile, [new PdfObjectId(1, 0)]);
        Assert.Equal(fullBytes, incrementalBytes);
    }

    [Fact]
    public void WriteIncrementalRejectsDirtyObjectThatDoesNotExistInDocument()
    {
        PdfFile original = CreateMinimalFile();
        byte[] fullBytes = PdfFileWriter.Write(original);
        PdfFile parsed = PdfFileReader.Read(fullBytes);

        Assert.Throws<PdfFormatException>(() => PdfFileWriter.WriteIncremental(parsed, [new PdfObjectId(999, 0)]));
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

    private static PdfFile CreateStreamFile()
    {
        PdfDictionaryObject streamDictionary = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("DemoStream")),
        ]);

        PdfStreamObject streamObject = new(streamDictionary, Encoding.ASCII.GetBytes("Hello"));
        PdfIndirectObject object1 = new(new PdfObjectId(1, 0), streamObject);
        PdfDictionaryObject trailer = new(
        [
            new PdfDictionaryEntry("Root", new PdfReferenceObject(new PdfObjectId(1, 0))),
        ]);

        return new PdfFile("2.0", [object1], trailer);
    }

    private static PdfFile CreateTwoObjectFile()
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

        PdfDictionaryObject trailer = new(
        [
            new PdfDictionaryEntry("Root", new PdfReferenceObject(new PdfObjectId(1, 0))),
        ]);

        return new PdfFile(
            "2.0",
            [
                new PdfIndirectObject(new PdfObjectId(1, 0), catalog),
                new PdfIndirectObject(new PdfObjectId(2, 0), pages),
            ],
            trailer);
    }

    private static PdfFile CreateUpdatedCatalogFile(PdfFile parsed)
    {
        PdfDictionaryObject updatedCatalog = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Catalog")),
            new PdfDictionaryEntry("Version", new PdfNameObject("2.0")),
        ]);
        return new PdfFile(
            parsed.Version,
            [new PdfIndirectObject(new PdfObjectId(1, 0), updatedCatalog)],
            parsed.Trailer,
            parsed.SourceBytes,
            parsed.StartXrefOffset,
            parsed.XrefEntries);
    }
}
