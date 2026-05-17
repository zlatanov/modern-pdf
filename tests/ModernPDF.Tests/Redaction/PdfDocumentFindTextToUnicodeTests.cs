using System.Globalization;
using System.Text;
using ModernPDF.Format;
using ModernPDF.Format.Files;
using ModernPDF.Format.Objects;
using ModernPDF.Primitives;

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
        byte[] saved = document.Save();
        PdfFile parsedFile = PdfFileReader.Read(saved);
        PdfStreamObject pageContent = Assert.IsType<PdfStreamObject>(
            parsedFile.Objects.Single(static objectItem => objectItem.ObjectId == new PdfObjectId(5, 0)).Value);
        IReadOnlyList<PdfToken> contentTokens = PdfTokenizer.Tokenize(
            PdfFileReader.DecodeStreamDataForExtraction(pageContent, context: "test content stream"));
        PdfDocument reopened = PdfDocument.Open(saved);
        string redactedText = reopened.ExtractText();

        Assert.True(replacements > 0);
        Assert.Contains(contentTokens, static token => token.Kind == PdfTokenKind.HexString);
        Assert.DoesNotContain(contentTokens, static token => token.Kind == PdfTokenKind.String);
        Assert.DoesNotContain(identifier, redactedText, StringComparison.Ordinal);
        Assert.Contains(personName, redactedText, StringComparison.Ordinal);
    }

    [Fact]
    public void HardRedactTextMergesFragmentedMatchRectangles()
    {
        const string personName = "Примерен Тестов Потребител";
        byte[] pdf = CreateFragmentedHexEncodedToUnicodePdf(personName);
        PdfDocument document = PdfDocument.Open(pdf);

        int replacements = document.HardRedactText(personName);
        byte[] saved = document.Save();
        string ascii = Encoding.ASCII.GetString(saved);

        Assert.True(replacements > 0);
        Assert.Equal(1, CountOccurrences(ascii, " re "));
    }

    [Fact]
    public void HardRedactTextKeepsFragmentedWrappedMatchesCompact()
    {
        const string value = "Демонстрационен Текст";
        byte[] pdf = CreateWrappedFragmentedHexEncodedToUnicodePdf(value);
        PdfDocument document = PdfDocument.Open(pdf);

        int replacements = document.HardRedactText(value);
        byte[] saved = document.Save();
        string ascii = Encoding.ASCII.GetString(saved);
        string extracted = PdfDocument.Open(saved).ExtractText();

        Assert.True(replacements > 0);
        Assert.DoesNotContain(value, extracted, StringComparison.Ordinal);
        Assert.True(CountOccurrences(ascii, " re ") <= 4);
    }

    [Fact]
    public void HardRedactTextUsesReasonableWidthForUnicodeGlyphs()
    {
        const string value = "ДемонстрационенТекст";
        byte[] pdf = CreateHexEncodedToUnicodePdf(value, "9999");
        PdfDocument document = PdfDocument.Open(pdf);

        int replacements = document.HardRedactText(value);
        byte[] saved = document.Save();
        PdfFile parsedFile = PdfFileReader.Read(saved);
        double maxRectWidth = GetMaximumRectangleWidth(parsedFile);

        Assert.True(replacements > 0);
        Assert.True(maxRectWidth > 0);
        Assert.True(maxRectWidth < 200);
    }

    [Fact]
    public void HardRedactTextUsesType0FontWidthsForHexText()
    {
        const string text = "ABAC";
        byte[] pdf = CreateType0WidthMappedHexPdf(text);
        PdfDocument document = PdfDocument.Open(pdf);

        int replacements = document.HardRedactText("B", new PdfHardRedactionOptions
        {
            HorizontalPadding = 0,
            VerticalPadding = 0,
        });

        PdfFile parsedFile = PdfFileReader.Read(document.Save());
        double maxRectWidth = GetMaximumRectangleWidth(parsedFile);

        Assert.True(replacements > 0);
        Assert.InRange(maxRectWidth, 21.0, 22.2);
    }

    [Fact]
    public void HardRedactTextForMultiWordTargetCoversInterWordGap()
    {
        const string phrase = "Alpha Beta";
        byte[] pdf = CreateLiteralWordGapPdf();
        PdfDocument document = PdfDocument.Open(pdf);

        int replacements = document.HardRedactText(phrase);
        byte[] saved = document.Save();
        string ascii = Encoding.ASCII.GetString(saved);
        string extracted = PdfDocument.Open(saved).ExtractText();

        Assert.True(replacements > 0);
        Assert.Equal(1, CountOccurrences(ascii, " re "));
        Assert.DoesNotContain(phrase, extracted, StringComparison.Ordinal);
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

    private static byte[] CreateFragmentedHexEncodedToUnicodePdf(string text)
    {
        Dictionary<char, int> cidByCharacter = [];
        string textHex = EncodeTextToCidHex(text, cidByCharacter);
        string toUnicode = BuildToUnicodeCMap(cidByCharacter);
        StringBuilder parts = new();
        for (int offset = 0; offset < textHex.Length; offset += 4)
        {
            if (parts.Length > 0)
            {
                parts.Append(' ');
            }

            parts.Append('<');
            parts.Append(textHex.AsSpan(offset, 4));
            parts.Append("> Tj");
        }

        string content = $"BT /F1 12 Tf 72 720 Td {parts} ET";

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

    private static byte[] CreateWrappedFragmentedHexEncodedToUnicodePdf(string text)
    {
        Dictionary<char, int> cidByCharacter = [];
        string textHex = EncodeTextToCidHex(text, cidByCharacter);
        string toUnicode = BuildToUnicodeCMap(cidByCharacter);

        List<string> chunks = [];
        for (int offset = 0; offset < textHex.Length; offset += 4)
        {
            chunks.Add(textHex.Substring(offset, 4));
        }

        int firstBreak = chunks.Count / 2;
        int secondBreak = chunks.Count / 3;
        StringBuilder contentBuilder = new();
        contentBuilder.Append("BT /F1 12 Tf 72 720 Td ");
        for (int index = 0; index < chunks.Count; index++)
        {
            if (index > 0)
            {
                contentBuilder.Append(' ');
            }

            contentBuilder.Append('<');
            contentBuilder.Append(chunks[index]);
            contentBuilder.Append("> Tj");
        }

        contentBuilder.Append(" ET\nBT /F1 12 Tf 72 700 Td ");
        for (int index = 0; index < firstBreak; index++)
        {
            if (index > 0)
            {
                contentBuilder.Append(' ');
            }

            contentBuilder.Append('<');
            contentBuilder.Append(chunks[index]);
            contentBuilder.Append("> Tj");
        }

        contentBuilder.Append(" T* ");
        for (int index = firstBreak; index < chunks.Count; index++)
        {
            if (index > firstBreak)
            {
                contentBuilder.Append(' ');
            }

            if (index == secondBreak)
            {
                contentBuilder.Append("4 0 Td ");
            }

            contentBuilder.Append('<');
            contentBuilder.Append(chunks[index]);
            contentBuilder.Append("> Tj");
        }

        contentBuilder.Append(" ET");
        string content = contentBuilder.ToString();

        using MemoryStream stream = new();
        List<long> objectOffsets = [];

        AppendAscii(stream, "%PDF-1.4\n");
        objectOffsets.Add(stream.Position);
        AppendAscii(stream, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        objectOffsets.Add(stream.Position);
        AppendAscii(stream, "2 0 obj\n<< /Type /Pages /Count 1 /Kids [3 0 R] >>\nendobj\n");
        objectOffsets.Add(stream.Position);
        AppendAscii(stream, "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");
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

    private static byte[] CreateType0WidthMappedHexPdf(string text)
    {
        Dictionary<char, int> cidByCharacter = new()
        {
            ['A'] = 1,
            ['B'] = 2,
            ['C'] = 3,
        };

        string textHex = EncodeTextToCidHex(text, cidByCharacter);
        string toUnicode = BuildToUnicodeCMap(cidByCharacter);
        string content = $"BT /F1 12 Tf 72 720 Td <{textHex}> Tj ET";

        using MemoryStream stream = new();
        List<long> objectOffsets = [];

        AppendAscii(stream, "%PDF-1.4\n");
        objectOffsets.Add(stream.Position);
        AppendAscii(stream, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        objectOffsets.Add(stream.Position);
        AppendAscii(stream, "2 0 obj\n<< /Type /Pages /Count 1 /Kids [3 0 R] >>\nendobj\n");
        objectOffsets.Add(stream.Position);
        AppendAscii(stream, "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");
        objectOffsets.Add(stream.Position);
        AppendAscii(stream, "4 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /WidthTest /Encoding /Identity-H /DescendantFonts [7 0 R] /ToUnicode 6 0 R >>\nendobj\n");
        objectOffsets.Add(stream.Position);
        AppendAscii(stream, $"5 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        objectOffsets.Add(stream.Position);
        AppendAscii(stream, $"6 0 obj\n<< /Length {toUnicode.Length} >>\nstream\n{toUnicode}\nendstream\nendobj\n");
        objectOffsets.Add(stream.Position);
        AppendAscii(stream, "7 0 obj\n<< /Type /Font /Subtype /CIDFontType2 /BaseFont /WidthTest /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 500 /W [1 [500 1800 500]] >>\nendobj\n");

        long xrefOffset = stream.Position;
        AppendAscii(stream, "xref\n0 8\n0000000000 65535 f \n");
        foreach (long objectOffset in objectOffsets)
        {
            AppendAscii(stream, $"{objectOffset:D10} 00000 n \n");
        }

        AppendAscii(stream, "trailer\n<< /Size 8 /Root 1 0 R >>\n");
        AppendAscii(stream, $"startxref\n{xrefOffset}\n%%EOF");
        return stream.ToArray();
    }

    private static byte[] CreateLiteralWordGapPdf()
    {
        const string first = "Alpha ";
        const string second = "Beta";
        string content = $"BT /F1 12 Tf 72 720 Td ({first}) Tj 120 0 Td ({second}) Tj ET";

        using MemoryStream stream = new();
        List<long> objectOffsets = [];

        AppendAscii(stream, "%PDF-1.4\n");
        objectOffsets.Add(stream.Position);
        AppendAscii(stream, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        objectOffsets.Add(stream.Position);
        AppendAscii(stream, "2 0 obj\n<< /Type /Pages /Count 1 /Kids [3 0 R] >>\nendobj\n");
        objectOffsets.Add(stream.Position);
        AppendAscii(stream, "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 300] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");
        objectOffsets.Add(stream.Position);
        AppendAscii(stream, "4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
        objectOffsets.Add(stream.Position);
        AppendAscii(stream, $"5 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

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

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while (true)
        {
            int found = text.IndexOf(value, index, StringComparison.Ordinal);
            if (found < 0)
            {
                return count;
            }

            count++;
            index = found + value.Length;
        }
    }

    private static double GetMaximumRectangleWidth(IReadOnlyList<PdfToken> tokens)
    {
        double maxWidth = 0;
        for (int index = 4; index < tokens.Count; index++)
        {
            if (tokens[index].Kind != PdfTokenKind.Keyword
                || !string.Equals(tokens[index].Lexeme, "re", StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryParseNumber(tokens[index - 2], out double width))
            {
                continue;
            }

            if (width > maxWidth)
            {
                maxWidth = width;
            }
        }

        return maxWidth;
    }

    private static double GetMaximumRectangleWidth(PdfFile file)
    {
        double maxWidth = 0;
        foreach (PdfIndirectObject objectItem in file.Objects)
        {
            if (objectItem.Value is not PdfStreamObject streamObject)
            {
                continue;
            }

            IReadOnlyList<PdfToken> tokens = PdfTokenizer.Tokenize(
                PdfFileReader.DecodeStreamDataForExtraction(streamObject, context: "rectangle width stream"));
            double streamMax = GetMaximumRectangleWidth(tokens);
            if (streamMax > maxWidth)
            {
                maxWidth = streamMax;
            }
        }

        return maxWidth;
    }

    private static bool TryParseNumber(PdfToken token, out double value)
    {
        if (token.Kind is PdfTokenKind.Integer or PdfTokenKind.Real
            && double.TryParse(token.Lexeme, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = 0;
        return false;
    }
}
