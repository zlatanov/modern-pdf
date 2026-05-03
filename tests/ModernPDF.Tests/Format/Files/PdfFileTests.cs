using ModernPDF.Format.Files;
using ModernPDF.Format.Objects;
using ModernPDF.Primitives;

namespace ModernPDF.Tests.Format.Files;

public sealed class PdfFileTests
{
    [Fact]
    public void ConstructorStoresVersionObjectsAndTrailer()
    {
        PdfDictionaryObject trailer = new(
        [
            new PdfDictionaryEntry("Root", new PdfReferenceObject(new PdfObjectId(1, 0))),
        ]);
        PdfIndirectObject[] objects =
        [
            new(new PdfObjectId(1, 0), new PdfDictionaryObject([])),
        ];

        PdfFile file = new("2.0", objects, trailer);

        Assert.Equal("2.0", file.Version);
        Assert.Single(file.Objects);
        Assert.Same(trailer, file.Trailer);
    }

    [Fact]
    public void ConstructorRejectsInvalidInput()
    {
        PdfDictionaryObject trailer = new([]);
        PdfIndirectObject[] objects = [];

        Assert.Throws<ArgumentException>(() => _ = new PdfFile("", objects, trailer));
        Assert.Throws<ArgumentNullException>(() => _ = new PdfFile("2.0", null!, trailer));
        Assert.Throws<ArgumentNullException>(() => _ = new PdfFile("2.0", objects, null!));
    }
}
