using System.IO.Compression;
using System.Text;

namespace ModernPDF.Tests.Redaction;

public sealed class PdfDocumentCompressedContentRedactionTests
{
    [Fact]
    public void ExtractTextRegionsAndFindTextSupportFlateCompressedContentStream()
    {
        PdfDocument document = PdfDocument.Open(CreateSinglePageFlateContentPdf("Sofia is city"));

        PdfTextRegion region = Assert.Single(document.ExtractTextRegions());
        PdfTextMatch match = Assert.Single(document.FindText(@"\bSofia\b"));

        Assert.Equal("Sofia is city", region.Text);
        Assert.Equal(0, match.PageIndex);
    }

    [Fact]
    public void HardRedactTextRewritesFlateCompressedContentStream()
    {
        PdfDocument document = PdfDocument.Open(CreateSinglePageFlateContentPdf("Sofia is city"));

        int replacements = document.HardRedactText("Sofia");
        byte[] saved = document.Save();
        PdfDocument reopened = PdfDocument.Open(saved);

        Assert.Equal(1, replacements);
        Assert.Equal(" is city", reopened.ExtractText());
    }

    private static byte[] CreateSinglePageFlateContentPdf(string text)
    {
        string escapedText = EscapePdfLiteralString(text);
        byte[] compressedContent = CompressZlib(Encoding.ASCII.GetBytes($"BT /F1 12 Tf 72 720 Td ({escapedText}) Tj ET"));

        using MemoryStream stream = new();
        List<long> objectOffsets = [];

        AppendAscii(stream, "%PDF-1.4\n");

        objectOffsets.Add(stream.Position);
        AppendAscii(stream, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        objectOffsets.Add(stream.Position);
        AppendAscii(stream, "2 0 obj\n<< /Type /Pages /Count 1 /Kids [3 0 R] >>\nendobj\n");

        objectOffsets.Add(stream.Position);
        AppendAscii(stream, "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");

        objectOffsets.Add(stream.Position);
        AppendAscii(stream, "4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        objectOffsets.Add(stream.Position);
        AppendAscii(stream, $"5 0 obj\n<< /Length {compressedContent.Length} /Filter /FlateDecode >>\nstream\n");
        stream.Write(compressedContent, 0, compressedContent.Length);
        AppendAscii(stream, "\nendstream\nendobj\n");

        long xrefOffset = stream.Position;
        AppendAscii(stream, "xref\n0 6\n0000000000 65535 f \n");
        foreach (long objectOffset in objectOffsets)
        {
            AppendAscii(stream, $"{objectOffset:D10} 00000 n \n");
        }

        AppendAscii(stream, "trailer\n<< /Size 6 /Root 1 0 R >>\n");
        AppendAscii(stream, $"startxref\n{xrefOffset}\n%%EOF");
        return stream.ToArray();
    }

    private static byte[] CompressZlib(byte[] input)
    {
        using MemoryStream output = new();
        using (ZLibStream stream = new(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            stream.Write(input, 0, input.Length);
        }

        return output.ToArray();
    }

    private static void AppendAscii(Stream stream, string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string EscapePdfLiteralString(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
    }
}
