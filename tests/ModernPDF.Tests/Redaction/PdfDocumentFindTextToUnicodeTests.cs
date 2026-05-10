using System.Globalization;
using System.Text;

namespace ModernPDF.Tests.Redaction;

public sealed class PdfDocumentFindTextToUnicodeTests
{
    [Fact]
    public void FindTextAndHardRedactTextWorkForHexEncodedToUnicodeText()
    {
        const string personName = "Примерен Тестов Потребител";
        const string identifier = "1234509876";
        byte[] pdf = CreateHexEncodedToUnicodePdf(personName, identifier);

        PdfDocument document = PdfDocument.Open(pdf);
        string extracted = document.ExtractText();

        Assert.Contains(personName, extracted, StringComparison.Ordinal);
        Assert.Contains(identifier, extracted, StringComparison.Ordinal);
        Assert.NotEmpty(document.FindText(personName));
        Assert.NotEmpty(document.FindText(identifier));

        int replacements = document.HardRedactText(identifier);
        PdfDocument reopened = PdfDocument.Open(document.Save());
        string redactedText = reopened.ExtractText();

        Assert.True(replacements > 0);
        Assert.DoesNotContain(identifier, redactedText, StringComparison.Ordinal);
        Assert.Contains(personName, redactedText, StringComparison.Ordinal);
    }

    private static byte[] CreateHexEncodedToUnicodePdf(string name, string identifier)
    {
        Dictionary<char, int> cidByCharacter = [];
        string nameHex = EncodeTextToCidHex(name, cidByCharacter);
        string identifierHex = EncodeTextToCidHex(identifier, cidByCharacter);
        string toUnicode = BuildToUnicodeCMap(cidByCharacter);
        string content = $"BT /F1 12 Tf 72 720 Td <{nameHex}> Tj ET\nBT /F1 12 Tf 72 700 Td <{identifierHex}> Tj ET";

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
        AppendAscii(stream, "4 0 obj\n<< /ToUnicode 6 0 R >>\nendobj\n");

        objectOffsets.Add(stream.Position);
        AppendAscii(stream, $"5 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

        objectOffsets.Add(stream.Position);
        AppendAscii(stream, $"6 0 obj\n<< /Length {toUnicode.Length} >>\nstream\n{toUnicode}\nendstream\nendobj\n");

        long xrefOffset = stream.Position;
        AppendAscii(stream, "xref\n0 7\n0000000000 65535 f \n");
        foreach (long objectOffset in objectOffsets)
        {
            AppendAscii(stream, $"{objectOffset:D10} 00000 n \n");
        }

        AppendAscii(stream, "trailer\n<< /Size 7 /Root 1 0 R >>\n");
        AppendAscii(stream, $"startxref\n{xrefOffset}\n%%EOF");
        return stream.ToArray();
    }

    private static string EncodeTextToCidHex(string text, Dictionary<char, int> cidByCharacter)
    {
        StringBuilder builder = new();
        int nextCid = cidByCharacter.Count + 1;
        foreach (char character in text)
        {
            if (!cidByCharacter.TryGetValue(character, out int cid))
            {
                cid = nextCid++;
                cidByCharacter[character] = cid;
            }

            builder.Append(cid.ToString("X4", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string BuildToUnicodeCMap(Dictionary<char, int> cidByCharacter)
    {
        StringBuilder builder = new();
        builder.AppendLine("/CIDInit /ProcSet findresource begin");
        builder.AppendLine("12 dict begin");
        builder.AppendLine("begincmap");
        builder.AppendLine("/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> def");
        builder.AppendLine("/CMapName /Adobe-Identity-UCS def");
        builder.AppendLine("/CMapType 2 def");
        builder.AppendLine("1 begincodespacerange");
        builder.AppendLine("<0000> <FFFF>");
        builder.AppendLine("endcodespacerange");
        builder.Append(cidByCharacter.Count.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine(" beginbfchar");

        foreach ((char character, int cid) in cidByCharacter.OrderBy(entry => entry.Value))
        {
            string unicodeHex = Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(character.ToString()));
            builder.Append('<');
            builder.Append(cid.ToString("X4", CultureInfo.InvariantCulture));
            builder.Append("> <");
            builder.Append(unicodeHex);
            builder.AppendLine(">");
        }

        builder.AppendLine("endbfchar");
        builder.AppendLine("endcmap");
        builder.AppendLine("CMapName currentdict /CMap defineresource pop");
        builder.AppendLine("end");
        builder.Append("end");
        return builder.ToString();
    }

    private static void AppendAscii(Stream stream, string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
    }
}
