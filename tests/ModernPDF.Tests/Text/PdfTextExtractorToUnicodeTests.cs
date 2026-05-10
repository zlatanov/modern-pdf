using System.Text;
using System.IO.Compression;
using ModernPDF.DocumentModel;
using ModernPDF.Format.Files;
using ModernPDF.Format.Objects;
using ModernPDF.Primitives;
using ModernPDF.Text;

namespace ModernPDF.Tests.Text;

public sealed class PdfTextExtractorToUnicodeTests
{
    [Fact]
    public void ExtractPageSupportsToUnicodeBfRangeMappings()
    {
        PdfStreamObject contentStream = CreateStream("BT /F1 12 Tf <000100020003> Tj ET");
        PdfStreamObject toUnicodeStream = CreateStream(
            "/CIDInit /ProcSet findresource begin\n" +
            "12 dict begin\n" +
            "begincmap\n" +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> def\n" +
            "/CMapName /Adobe-Identity-UCS def\n" +
            "/CMapType 2 def\n" +
            "1 begincodespacerange\n" +
            "<0000> <FFFF>\n" +
            "endcodespacerange\n" +
            "1 beginbfrange\n" +
            "<0001> <0003> <0041>\n" +
            "endbfrange\n" +
            "endcmap\n" +
            "CMapName currentdict /CMap defineresource pop\n" +
            "end\n" +
            "end");

        PdfDictionaryObject fontDictionary = new(
        [
            new PdfDictionaryEntry("ToUnicode", new PdfReferenceObject(new PdfObjectId(20, 0))),
        ]);

        PdfFile file = CreateFile(
        [
            new PdfIndirectObject(new PdfObjectId(10, 0), contentStream),
            new PdfIndirectObject(new PdfObjectId(20, 0), toUnicodeStream),
            new PdfIndirectObject(new PdfObjectId(21, 0), fontDictionary),
        ]);

        PdfDictionaryObject resources = new(
        [
            new PdfDictionaryEntry(
                "Font",
                new PdfDictionaryObject(
                [
                    new PdfDictionaryEntry("F1", new PdfReferenceObject(new PdfObjectId(21, 0))),
                ])),
        ]);

        PdfDocumentModel model = CreateModel(
        [
            new PdfPageModel(new PdfObjectId(3, 0), mediaBox: null, resources, new PdfReferenceObject(new PdfObjectId(10, 0))),
        ]);

        string text = PdfTextExtractor.ExtractPage(file, model, 0);

        Assert.Equal("ABC", text);
    }

    [Fact]
    public void ExtractPageSupportsFlateCompressedContentAndToUnicodeStreams()
    {
        PdfStreamObject contentStream = CreateFlateStream("BT /F1 12 Tf <000100020003> Tj ET");
        PdfStreamObject toUnicodeStream = CreateFlateStream(
            "/CIDInit /ProcSet findresource begin\n" +
            "12 dict begin\n" +
            "begincmap\n" +
            "1 begincodespacerange\n" +
            "<0000> <FFFF>\n" +
            "endcodespacerange\n" +
            "1 beginbfrange\n" +
            "<0001> <0003> <0041>\n" +
            "endbfrange\n" +
            "endcmap\n" +
            "CMapName currentdict /CMap defineresource pop\n" +
            "end\n" +
            "end");

        PdfDictionaryObject fontDictionary = new(
        [
            new PdfDictionaryEntry("ToUnicode", new PdfReferenceObject(new PdfObjectId(20, 0))),
        ]);

        PdfFile file = CreateFile(
        [
            new PdfIndirectObject(new PdfObjectId(10, 0), contentStream),
            new PdfIndirectObject(new PdfObjectId(20, 0), toUnicodeStream),
            new PdfIndirectObject(new PdfObjectId(21, 0), fontDictionary),
        ]);

        PdfDictionaryObject resources = new(
        [
            new PdfDictionaryEntry(
                "Font",
                new PdfDictionaryObject(
                [
                    new PdfDictionaryEntry("F1", new PdfReferenceObject(new PdfObjectId(21, 0))),
                ])),
        ]);

        PdfDocumentModel model = CreateModel(
        [
            new PdfPageModel(new PdfObjectId(3, 0), mediaBox: null, resources, new PdfReferenceObject(new PdfObjectId(10, 0))),
        ]);

        string text = PdfTextExtractor.ExtractPage(file, model, 0);

        Assert.Equal("ABC", text);
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

    private static PdfStreamObject CreateFlateStream(string content)
    {
        using MemoryStream buffer = new();
        using (ZLibStream zlib = new(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            byte[] bytes = Encoding.ASCII.GetBytes(content);
            zlib.Write(bytes, 0, bytes.Length);
        }

        PdfDictionaryObject dictionary = new(
        [
            new PdfDictionaryEntry("Filter", new PdfNameObject("FlateDecode")),
        ]);

        return new PdfStreamObject(dictionary, buffer.ToArray());
    }
}
