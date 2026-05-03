using ModernPDF.Format.Files;
using ModernPDF.Format.Objects;

namespace ModernPDF.Tests.Format.Files;

public sealed class PdfFileReaderTests
{
    [Fact]
    public void ReadParsesWrittenClassicPdfSlice()
    {
        PdfFile source = CreateMinimalFile();
        byte[] bytes = PdfFileWriter.Write(source);

        PdfFile parsed = PdfFileReader.Read(bytes);

        Assert.Equal("2.0", parsed.Version);
        Assert.Single(parsed.Objects);
        Assert.Equal(1, parsed.Objects[0].ObjectId.ObjectNumber);

        PdfDictionaryObject trailer = parsed.Trailer;
        PdfDictionaryEntry sizeEntry = Assert.Single(trailer.Entries, entry => entry.Key == "Size");
        PdfNumberObject size = Assert.IsType<PdfNumberObject>(sizeEntry.Value);
        Assert.Equal(2, size.Value);

        PdfDictionaryEntry rootEntry = Assert.Single(trailer.Entries, entry => entry.Key == "Root");
        PdfReferenceObject rootReference = Assert.IsType<PdfReferenceObject>(rootEntry.Value);
        Assert.Equal(1, rootReference.ObjectId.ObjectNumber);
    }

    private static PdfFile CreateMinimalFile()
    {
        PdfDictionaryObject catalog = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Catalog")),
        ]);

        PdfIndirectObject object1 = new(new ModernPDF.Primitives.PdfObjectId(1, 0), catalog);
        PdfDictionaryObject trailer = new(
        [
            new PdfDictionaryEntry("Root", new PdfReferenceObject(new ModernPDF.Primitives.PdfObjectId(1, 0))),
        ]);

        return new PdfFile("2.0", [object1], trailer);
    }
}
