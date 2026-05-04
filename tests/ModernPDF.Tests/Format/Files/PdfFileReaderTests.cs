using ModernPDF.Format;
using ModernPDF.Format.Files;
using ModernPDF.Format.Objects;

namespace ModernPDF.Tests.Format.Files;

public sealed class PdfFileReaderTests
{
    [Fact]
    public void ReadRejectsEmptyData()
    {
        Assert.Throws<PdfFormatException>(() => PdfFileReader.Read(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void ReadRejectsMissingPdfHeader()
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes("NOTPDF\nstartxref\n0\n%%EOF");

        Assert.Throws<PdfFormatException>(() => PdfFileReader.Read(bytes));
    }

    [Fact]
    public void ReadRejectsHeaderWithoutLineEnding()
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes("%PDF-2.0");

        Assert.Throws<PdfFormatException>(() => PdfFileReader.Read(bytes));
    }

    [Fact]
    public void ReadRejectsMissingStartXrefMarker()
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes("%PDF-2.0\n1 0 obj\n<<>>\nendobj\n");

        Assert.Throws<PdfFormatException>(() => PdfFileReader.Read(bytes));
    }

    [Fact]
    public void ReadRejectsInvalidStartXrefOffsetText()
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes("%PDF-2.0\nstartxref\nX\n%%EOF");

        Assert.Throws<PdfFormatException>(() => PdfFileReader.Read(bytes));
    }

    [Fact]
    public void ReadRejectsStartXrefOffsetThatOverflowsInt32()
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes("%PDF-2.0\nstartxref\n999999999999\n%%EOF");

        Assert.Throws<PdfFormatException>(() => PdfFileReader.Read(bytes));
    }

    [Fact]
    public void ReadRejectsNonClassicXrefSection()
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes("%PDF-2.0\n1234567890\nstartxref\n9\n%%EOF");

        Assert.Throws<PdfFormatException>(() => PdfFileReader.Read(bytes));
    }

    [Fact]
    public void ReadRejectsMalformedXrefSubsectionHeader()
    {
        const string text = "%PDF-2.0\nxref\nX\ntrailer\n<< /Size 1 >>\nstartxref\n9\n%%EOF";
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(text);

        Assert.Throws<PdfFormatException>(() => PdfFileReader.Read(bytes));
    }

    [Fact]
    public void ReadRejectsUnexpectedEndOfXrefEntry()
    {
        const string tail = "xref\n0 1\n0000000000 65535";
        string text = BuildPdfWithTailOnlyAfterStartXref(tail);
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(text);

        Assert.Throws<PdfFormatException>(() => PdfFileReader.Read(bytes));
    }

    [Fact]
    public void ReadRejectsTruncatedFixedWidthIntegerInXref()
    {
        const string tail = "xref\n0 1\n12345";
        string text = BuildPdfWithTailOnlyAfterStartXref(tail);
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(text);

        Assert.Throws<PdfFormatException>(() => PdfFileReader.Read(bytes));
    }

    [Fact]
    public void ReadRejectsMissingTrailerSection()
    {
        const string text = "%PDF-2.0\nxref\n0 1\n0000000000 65535 f\nstartxref\n9\n%%EOF";
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(text);

        Assert.Throws<PdfFormatException>(() => PdfFileReader.Read(bytes));
    }

    [Fact]
    public void ReadRejectsMissingTrailerSectionWhenXrefHasNoEntries()
    {
        string text = BuildPdfWithTailOnlyAfterStartXref("xref\n0 0\n");
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(text);

        Assert.Throws<PdfFormatException>(() => PdfFileReader.Read(bytes));
    }

    [Fact]
    public void ReadRejectsMissingTrailerDictionaryStart()
    {
        const string text = "%PDF-2.0\nxref\n0 1\n0000000000 65535 f\ntrailer\nstartxref\n9\n%%EOF";
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(text);

        Assert.Throws<PdfFormatException>(() => PdfFileReader.Read(bytes));
    }

    [Fact]
    public void ReadRejectsUnterminatedTrailerDictionary()
    {
        const string text = "%PDF-2.0\nxref\n0 1\n0000000000 65535 f\ntrailer\n<< /Size 1\nstartxref\n9\n%%EOF";
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(text);

        Assert.Throws<PdfFormatException>(() => PdfFileReader.Read(bytes));
    }

    [Fact]
    public void ReadRejectsInvalidXrefObjectOffset()
    {
        PdfFile source = CreateMinimalFile();
        byte[] bytes = PdfFileWriter.Write(source);
        byte[] tampered = ReplaceFirstInUseXrefEntryOffset(bytes, "2147483647");

        Assert.Throws<PdfFormatException>(() => PdfFileReader.Read(tampered));
    }

    [Fact]
    public void ReadRejectsObjectHeaderMismatch()
    {
        PdfFile source = CreateMinimalFile();
        byte[] bytes = PdfFileWriter.Write(source);
        byte[] tampered = ReplaceAscii(bytes, "1 0 obj", "2 0 obj");

        Assert.Throws<PdfFormatException>(() => PdfFileReader.Read(tampered));
    }

    [Fact]
    public void ReadRejectsMissingEndObjectMarker()
    {
        PdfFile source = CreateMinimalFile();
        byte[] bytes = PdfFileWriter.Write(source);
        byte[] tampered = ReplaceAscii(bytes, "endobj", "ENDOBJ");

        Assert.Throws<PdfFormatException>(() => PdfFileReader.Read(tampered));
    }

    [Fact]
    public void ReadRejectsStreamWithoutLength()
    {
        PdfFile source = CreateStreamFile();
        byte[] bytes = PdfFileWriter.Write(source);
        byte[] tampered = ReplaceAscii(bytes, "/Length 5", "/Type /Demo");

        Assert.Throws<PdfFormatException>(() => PdfFileReader.Read(tampered));
    }

    [Fact]
    public void ReadRejectsStreamWithoutLengthUsingManualPdf()
    {
        byte[] bytes = CreateSingleObjectPdf("<< /Type /Demo >>\nstream\nDATA\nendstream");

        Assert.Throws<PdfFormatException>(() => PdfFileReader.Read(bytes));
    }

    [Fact]
    public void ReadRejectsStreamLengthThatIsNotInteger()
    {
        PdfFile source = CreateStreamFile();
        byte[] bytes = PdfFileWriter.Write(source);
        byte[] tampered = ReplaceAscii(bytes, "/Length 5", "/Length (5)");

        Assert.Throws<PdfFormatException>(() => PdfFileReader.Read(tampered));
    }

    [Fact]
    public void ReadRejectsStreamLengthThatIsNotIntegerUsingManualPdf()
    {
        byte[] bytes = CreateSingleObjectPdf("<< /Length (5) >>\nstream\nDATA\nendstream");

        Assert.Throws<PdfFormatException>(() => PdfFileReader.Read(bytes));
    }

    [Fact]
    public void ReadRejectsNegativeStreamLength()
    {
        PdfFile source = CreateStreamFile();
        byte[] bytes = PdfFileWriter.Write(source);
        byte[] tampered = ReplaceAscii(bytes, "/Length 5", "/Length -1");

        Assert.Throws<PdfFormatException>(() => PdfFileReader.Read(tampered));
    }

    [Fact]
    public void ReadRejectsNegativeStreamLengthUsingManualPdf()
    {
        byte[] bytes = CreateSingleObjectPdf("<< /Length -1 >>\nstream\nDATA\nendstream");

        Assert.Throws<PdfFormatException>(() => PdfFileReader.Read(bytes));
    }

    [Fact]
    public void ReadRejectsStreamLengthPastEndOfFile()
    {
        PdfFile source = CreateStreamFile();
        byte[] bytes = PdfFileWriter.Write(source);
        byte[] tampered = ReplaceAscii(bytes, "/Length 5", "/Length 9999");

        Assert.Throws<PdfFormatException>(() => PdfFileReader.Read(tampered));
    }

    [Fact]
    public void ReadRejectsStreamLengthPastEndOfFileUsingManualPdf()
    {
        byte[] bytes = CreateSingleObjectPdf("<< /Length 100000 >>\nstream\nDATA\nendstream");

        Assert.Throws<PdfFormatException>(() => PdfFileReader.Read(bytes));
    }

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

    [Fact]
    public void ReadParsesWrittenStreamObject()
    {
        PdfFile source = CreateStreamFile();
        byte[] bytes = PdfFileWriter.Write(source);

        PdfFile parsed = PdfFileReader.Read(bytes);

        PdfStreamObject stream = Assert.IsType<PdfStreamObject>(parsed.Objects[0].Value);
        Assert.Equal("DATA!", System.Text.Encoding.ASCII.GetString(stream.Data.Span));
        PdfDictionaryEntry lengthEntry = Assert.Single(stream.Dictionary.Entries, entry => entry.Key == "Length");
        PdfNumberObject length = Assert.IsType<PdfNumberObject>(lengthEntry.Value);
        Assert.Equal(5, length.Value);
    }

    [Fact]
    public void ReadCapturesSourceAndXrefMetadata()
    {
        PdfFile source = CreateMinimalFile();
        byte[] bytes = PdfFileWriter.Write(source);

        PdfFile parsed = PdfFileReader.Read(bytes);

        Assert.NotNull(parsed.SourceBytes);
        Assert.Equal(bytes, parsed.SourceBytes);
        Assert.True(parsed.StartXrefOffset.HasValue);
        Assert.NotEmpty(parsed.XrefEntries);
        Assert.True(parsed.XrefEntries.ContainsKey(1));
    }

    [Fact]
    public void ReadTreatsNonDelimitedStreamKeywordAsRegularObjectContent()
    {
        byte[] bytes = CreateSingleObjectPdf("<< /Type /streaming >>");

        PdfFile parsed = PdfFileReader.Read(bytes);

        PdfDictionaryObject dictionary = Assert.IsType<PdfDictionaryObject>(parsed.Objects[0].Value);
        PdfNameObject type = Assert.IsType<PdfNameObject>(Assert.Single(dictionary.Entries, x => x.Key == "Type").Value);
        Assert.Equal("streaming", type.Value);
    }

    [Fact]
    public void ReadParsesStreamObjectWithCarriageReturnLineFeedDelimiter()
    {
        byte[] bytes = CreateStreamPdfWithCrLfDelimiter();

        PdfFile parsed = PdfFileReader.Read(bytes);

        PdfStreamObject stream = Assert.IsType<PdfStreamObject>(parsed.Objects[0].Value);
        Assert.Equal("DATA", System.Text.Encoding.ASCII.GetString(stream.Data.Span));
    }

    [Fact]
    public void ReadMergesHistoricalXrefEntriesViaPrevChain()
    {
        byte[] bytes = CreateSparseIncrementalPdf();

        PdfFile parsed = PdfFileReader.Read(bytes);

        Assert.Equal(2, parsed.Objects.Count);
        Assert.True(parsed.XrefEntries.ContainsKey(1));
        Assert.True(parsed.XrefEntries.ContainsKey(2));
        PdfDictionaryEntry rootEntry = Assert.Single(parsed.Trailer.Entries, static entry => entry.Key == "Root");
        PdfReferenceObject rootReference = Assert.IsType<PdfReferenceObject>(rootEntry.Value);
        Assert.Equal(1, rootReference.ObjectId.ObjectNumber);

        PdfDictionaryObject pagesDictionary = Assert.IsType<PdfDictionaryObject>(parsed.Objects.Single(static objectItem => objectItem.ObjectId.ObjectNumber == 2).Value);
        PdfDictionaryEntry countEntry = Assert.Single(pagesDictionary.Entries, static entry => entry.Key == "Count");
        PdfNumberObject count = Assert.IsType<PdfNumberObject>(countEntry.Value);
        Assert.Equal(1, count.Value);
    }

    [Fact]
    public void ReadRejectsTrailerPrevThatIsNotInteger()
    {
        byte[] bytes = CreateSparseIncrementalPdf();
        string text = System.Text.Encoding.ASCII.GetString(bytes);
        int prevIndex = text.LastIndexOf("/Prev ", StringComparison.Ordinal);
        Assert.True(prevIndex >= 0, "Expected /Prev in incremental trailer.");
        int valueStart = prevIndex + "/Prev ".Length;
        int valueEnd = valueStart;
        while (valueEnd < text.Length && char.IsAsciiDigit(text[valueEnd]))
        {
            valueEnd++;
        }

        string tampered = text[..valueStart] + "(bad)" + text[valueEnd..];
        Assert.Throws<PdfFormatException>(() => PdfFileReader.Read(System.Text.Encoding.ASCII.GetBytes(tampered)));
    }

    [Fact]
    public void ReadRejectsPrevCycles()
    {
        byte[] bytes = CreateSparseIncrementalPdf();
        string text = System.Text.Encoding.ASCII.GetString(bytes);
        const string marker = "startxref\n";
        int markerIndex = text.LastIndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, "Expected last startxref marker.");
        int numberStart = markerIndex + marker.Length;
        int numberEnd = numberStart;
        while (numberEnd < text.Length && char.IsAsciiDigit(text[numberEnd]))
        {
            numberEnd++;
        }

        string latestStartXref = text[numberStart..numberEnd];

        int prevIndex = text.LastIndexOf("/Prev ", StringComparison.Ordinal);
        Assert.True(prevIndex >= 0, "Expected /Prev in incremental trailer.");
        int prevStart = prevIndex + "/Prev ".Length;
        int prevEnd = prevStart;
        while (prevEnd < text.Length && char.IsAsciiDigit(text[prevEnd]))
        {
            prevEnd++;
        }

        string tampered = text[..prevStart] + latestStartXref + text[prevEnd..];
        Assert.Throws<PdfFormatException>(() => PdfFileReader.Read(System.Text.Encoding.ASCII.GetBytes(tampered)));
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

    private static PdfFile CreateStreamFile()
    {
        PdfDictionaryObject streamDictionary = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("DemoStream")),
        ]);

        PdfStreamObject stream = new(streamDictionary, System.Text.Encoding.ASCII.GetBytes("DATA!"));
        PdfIndirectObject object1 = new(new ModernPDF.Primitives.PdfObjectId(1, 0), stream);
        PdfDictionaryObject trailer = new(
        [
            new PdfDictionaryEntry("Root", new PdfReferenceObject(new ModernPDF.Primitives.PdfObjectId(1, 0))),
        ]);

        return new PdfFile("2.0", [object1], trailer);
    }

    private static byte[] ReplaceAscii(byte[] source, string oldValue, string newValue)
    {
        string text = System.Text.Encoding.ASCII.GetString(source);
        string replaced = text.Replace(oldValue, newValue, StringComparison.Ordinal);
        if (string.Equals(text, replaced, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Could not find text '{oldValue}' to replace.");
        }

        return System.Text.Encoding.ASCII.GetBytes(replaced);
    }

    private static byte[] ReplaceFirstInUseXrefEntryOffset(byte[] source, string replacementOffset)
    {
        string text = System.Text.Encoding.ASCII.GetString(source);
        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
            text,
            @"\d{10}\s\d{5}\sn",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new InvalidOperationException("Could not find an in-use xref entry to replace.");
        }

        string replaced = text.Remove(match.Index, 10).Insert(match.Index, replacementOffset);
        return System.Text.Encoding.ASCII.GetBytes(replaced);
    }

    private static byte[] CreateStreamPdfWithCrLfDelimiter()
    {
        const string prefix = "%PDF-2.0\n";
        const string objectSection = "1 0 obj\n<< /Length 4 >>\nstream\r\nDATA\nendstream\nendobj\n";
        int objectOffset = prefix.Length;
        int xrefOffset = prefix.Length + objectSection.Length;
        string xrefSection =
            "xref\n0 2\n0000000000 65535 f \n"
            + $"{objectOffset:D10} 00000 n \n"
            + "trailer\n<< /Root 1 0 R /Size 2 >>\n"
            + "startxref\n"
            + $"{xrefOffset}\n"
            + "%%EOF\n";

        return System.Text.Encoding.ASCII.GetBytes(prefix + objectSection + xrefSection);
    }

    private static byte[] CreateSingleObjectPdf(string objectBody)
    {
        const string prefix = "%PDF-2.0\n";
        int objectOffset = System.Text.Encoding.ASCII.GetByteCount(prefix);
        string objectSection = $"1 0 obj\n{objectBody}\nendobj\n";
        int xrefOffset = objectOffset + System.Text.Encoding.ASCII.GetByteCount(objectSection);
        string xrefSection =
            "xref\n0 2\n0000000000 65535 f \n"
            + $"{objectOffset:D10} 00000 n \n"
            + "trailer\n<< /Root 1 0 R /Size 2 >>\n"
            + "startxref\n"
            + $"{xrefOffset}\n"
            + "%%EOF\n";

        return System.Text.Encoding.ASCII.GetBytes(prefix + objectSection + xrefSection);
    }

    private static byte[] CreateSparseIncrementalPdf()
    {
        const string header = "%PDF-2.0\n";
        const string object1 = "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n";
        const string object2V1 = "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n";

        int object1Offset = System.Text.Encoding.ASCII.GetByteCount(header);
        int object2V1Offset = object1Offset + System.Text.Encoding.ASCII.GetByteCount(object1);
        int firstXrefOffset = object2V1Offset + System.Text.Encoding.ASCII.GetByteCount(object2V1);
        string firstRevisionXref =
            "xref\n0 3\n0000000000 65535 f \n"
            + $"{object1Offset:D10} 00000 n \n"
            + $"{object2V1Offset:D10} 00000 n \n"
            + "trailer\n<< /Root 1 0 R /Size 3 >>\n"
            + "startxref\n"
            + $"{firstXrefOffset}\n"
            + "%%EOF\n";

        string firstRevision = header + object1 + object2V1 + firstRevisionXref;
        const string object2V2 = "2 0 obj\n<< /Type /Pages /Kids [] /Count 1 >>\nendobj\n";
        int object2V2Offset = System.Text.Encoding.ASCII.GetByteCount(firstRevision);
        int secondXrefOffset = object2V2Offset + System.Text.Encoding.ASCII.GetByteCount(object2V2);
        string secondRevisionXref =
            "xref\n2 1\n"
            + $"{object2V2Offset:D10} 00000 n \n"
            + $"trailer\n<< /Size 3 /Prev {firstXrefOffset} >>\n"
            + "startxref\n"
            + $"{secondXrefOffset}\n"
            + "%%EOF\n";

        return System.Text.Encoding.ASCII.GetBytes(firstRevision + object2V2 + secondRevisionXref);
    }

    private static string BuildPdfWithTailOnlyAfterStartXref(string tail)
    {
        int offset = 0;
        while (true)
        {
            string prefix = $"%PDF-2.0\nstartxref\n{offset}\n%%EOF\n";
            int recalculatedOffset = System.Text.Encoding.ASCII.GetByteCount(prefix);
            if (recalculatedOffset == offset)
            {
                return prefix + tail;
            }

            offset = recalculatedOffset;
        }
    }
}
