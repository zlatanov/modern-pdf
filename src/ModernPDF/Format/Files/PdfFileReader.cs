using System.Globalization;
using System.Text;
using ModernPDF.Format.Objects;
using ModernPDF.Primitives;

namespace ModernPDF.Format.Files;

internal static class PdfFileReader
{
    public static PdfFile Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            throw new PdfFormatException("PDF data cannot be empty.");
        }

        string text = Encoding.ASCII.GetString(bytes);
        string version = ParseVersion(text);
        int startXrefOffset = ParseStartXrefOffset(text);

        (Dictionary<int, PdfXrefEntry> xrefEntries, PdfDictionaryObject trailer) =
            ParseCrossReferenceChain(bytes, startXrefOffset);
        List<PdfIndirectObject> objects = ParseIndirectObjects(bytes, xrefEntries);

        return new PdfFile(
            version,
            objects,
            trailer,
            sourceBytes: bytes.ToArray(),
            startXrefOffset: startXrefOffset,
            xrefEntries: xrefEntries);
    }

    private static (Dictionary<int, PdfXrefEntry> Entries, PdfDictionaryObject Trailer) ParseCrossReferenceChain(
        ReadOnlySpan<byte> bytes,
        int startXrefOffset)
    {
        HashSet<int> visitedOffsets = [];
        List<(Dictionary<int, PdfXrefEntry> Entries, PdfDictionaryObject Trailer)> sections = [];

        int currentOffset = startXrefOffset;
        while (true)
        {
            if (currentOffset < 0 || currentOffset >= bytes.Length)
            {
                throw new PdfFormatException("Cross-reference offset is outside the PDF byte range.");
            }

            if (!visitedOffsets.Add(currentOffset))
            {
                throw new PdfFormatException("Cycle detected while traversing trailer /Prev chain.");
            }

            Dictionary<int, PdfXrefEntry> sectionEntries = ParseCrossReferenceEntries(bytes, currentOffset);
            PdfDictionaryObject sectionTrailer = ParseTrailer(bytes, currentOffset);
            sections.Add((sectionEntries, sectionTrailer));

            if (!TryGetPrevOffset(sectionTrailer, out int prevOffset))
            {
                break;
            }

            currentOffset = prevOffset;
        }

        sections.Reverse();
        Dictionary<int, PdfXrefEntry> mergedEntries = [];
        PdfDictionaryObject mergedTrailer = new([]);
        foreach ((Dictionary<int, PdfXrefEntry> sectionEntries, PdfDictionaryObject sectionTrailer) in sections)
        {
            foreach ((int objectNumber, PdfXrefEntry entry) in sectionEntries)
            {
                mergedEntries[objectNumber] = entry;
            }

            mergedTrailer = MergeDictionaries(mergedTrailer, sectionTrailer);
        }

        return (mergedEntries, mergedTrailer);
    }

    private static string ParseVersion(string text)
    {
        const string prefix = "%PDF-";
        if (!text.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new PdfFormatException("Missing PDF header.");
        }

        int lineEnd = text.IndexOf('\n');
        if (lineEnd < 0)
        {
            throw new PdfFormatException("Invalid PDF header line.");
        }

        return text.Substring(prefix.Length, lineEnd - prefix.Length).Trim();
    }

    private static int ParseStartXrefOffset(string text)
    {
        const string marker = "startxref";
        int markerIndex = text.LastIndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            throw new PdfFormatException("Missing startxref marker.");
        }

        int cursor = markerIndex + marker.Length;
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
        {
            cursor++;
        }

        int numberStart = cursor;
        while (cursor < text.Length && char.IsAsciiDigit(text[cursor]))
        {
            cursor++;
        }

        if (numberStart == cursor)
        {
            throw new PdfFormatException("Could not parse startxref offset.");
        }

        string numberText = text[numberStart..cursor];
        if (!int.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int startXrefOffset))
        {
            throw new PdfFormatException("Invalid startxref numeric value.");
        }

        return startXrefOffset;
    }

    private static Dictionary<int, PdfXrefEntry> ParseCrossReferenceEntries(ReadOnlySpan<byte> bytes, int startXrefOffset)
    {
        string tail = Encoding.ASCII.GetString(bytes[startXrefOffset..]);
        int cursor = 0;

        if (!tail.StartsWith("xref", StringComparison.Ordinal))
        {
            throw new PdfFormatException("Only classic xref tables are supported in the current reader slice.");
        }

        cursor += "xref".Length;
        SkipWhitespace(tail, ref cursor);

        Dictionary<int, PdfXrefEntry> entries = [];

        while (cursor < tail.Length && !tail.AsSpan(cursor).StartsWith("trailer".AsSpan(), StringComparison.Ordinal))
        {
            int firstObject = ReadInteger(tail, ref cursor);
            SkipWhitespace(tail, ref cursor);
            int count = ReadInteger(tail, ref cursor);
            SkipWhitespace(tail, ref cursor);

            for (int index = 0; index < count; index++)
            {
                int offset = ReadFixedWidthInteger(tail, ref cursor, 10);
                SkipSpaces(tail, ref cursor);
                int generation = ReadFixedWidthInteger(tail, ref cursor, 5);
                SkipSpaces(tail, ref cursor);

                if (cursor >= tail.Length)
                {
                    throw new PdfFormatException("Unexpected end of xref entry.");
                }

                char inUse = tail[cursor];
                cursor++;
                SkipLineEnding(tail, ref cursor);

                if (inUse == 'n')
                {
                    int objectNumber = firstObject + index;
                    entries[objectNumber] = new PdfXrefEntry(offset, generation);
                }
            }

            SkipWhitespace(tail, ref cursor);
        }

        return entries;
    }

    private static PdfDictionaryObject ParseTrailer(ReadOnlySpan<byte> bytes, int startXrefOffset)
    {
        string tail = Encoding.ASCII.GetString(bytes[startXrefOffset..]);
        int trailerIndex = tail.IndexOf("trailer", StringComparison.Ordinal);
        if (trailerIndex < 0)
        {
            throw new PdfFormatException("Missing trailer section.");
        }

        int dictionaryStart = tail.IndexOf("<<", trailerIndex, StringComparison.Ordinal);
        if (dictionaryStart < 0)
        {
            throw new PdfFormatException("Trailer dictionary start was not found.");
        }

        int dictionaryEnd = FindMatchingDictionaryEnd(tail, dictionaryStart);
        string dictionaryText = tail.Substring(dictionaryStart, dictionaryEnd - dictionaryStart);

        PdfObject parsed = PdfObjectParser.ParseAscii(dictionaryText);
        return parsed as PdfDictionaryObject
            ?? throw new PdfFormatException("Trailer did not parse as a dictionary.");
    }

    private static bool TryGetPrevOffset(PdfDictionaryObject trailer, out int prevOffset)
    {
        PdfDictionaryEntry? prevEntry = trailer.Entries.FirstOrDefault(static entry => entry.Key == "Prev");
        if (prevEntry is null)
        {
            prevOffset = 0;
            return false;
        }

        if (prevEntry.Value is not PdfNumberObject prevNumber
            || !prevNumber.IsInteger
            || !double.IsFinite(prevNumber.Value)
            || prevNumber.Value < 0)
        {
            throw new PdfFormatException("Trailer /Prev must be a non-negative integer.");
        }

        prevOffset = Convert.ToInt32(prevNumber.Value, CultureInfo.InvariantCulture);
        return true;
    }

    private static PdfDictionaryObject MergeDictionaries(PdfDictionaryObject baseDictionary, PdfDictionaryObject overrides)
    {
        List<PdfDictionaryEntry> merged = [];
        HashSet<string> overrideKeys = [.. overrides.Entries.Select(static entry => entry.Key)];

        foreach (PdfDictionaryEntry entry in baseDictionary.Entries)
        {
            if (!overrideKeys.Contains(entry.Key))
            {
                merged.Add(entry);
            }
        }

        merged.AddRange(overrides.Entries);
        return new PdfDictionaryObject(merged);
    }

    private static int FindMatchingDictionaryEnd(string text, int dictionaryStart)
    {
        int depth = 0;

        for (int index = dictionaryStart; index < text.Length - 1; index++)
        {
            if (text[index] == '<' && text[index + 1] == '<')
            {
                depth++;
                index++;
                continue;
            }

            if (text[index] == '>' && text[index + 1] == '>')
            {
                depth--;
                index++;
                if (depth == 0)
                {
                    return index + 1;
                }
            }
        }

        throw new PdfFormatException("Could not find dictionary terminator.");
    }

    private static List<PdfIndirectObject> ParseIndirectObjects(ReadOnlySpan<byte> bytes, IReadOnlyDictionary<int, PdfXrefEntry> xrefEntries)
    {
        List<PdfIndirectObject> objects = [];

        foreach ((int objectNumber, PdfXrefEntry entry) in xrefEntries.OrderBy(pair => pair.Key))
        {
            if (entry.Offset < 0 || entry.Offset >= bytes.Length)
            {
                throw new PdfFormatException($"Invalid xref offset for object {objectNumber}.");
            }

            string objectTail = Encoding.ASCII.GetString(bytes[entry.Offset..]);
            string header = $"{objectNumber} {entry.Generation} obj";
            if (!objectTail.StartsWith(header, StringComparison.Ordinal))
            {
                throw new PdfFormatException($"Object header mismatch for object {objectNumber}.");
            }

            int objectBodyStart = header.Length;
            while (objectBodyStart < objectTail.Length && char.IsWhiteSpace(objectTail[objectBodyStart]))
            {
                objectBodyStart++;
            }

            int endObject = objectTail.IndexOf("endobj", objectBodyStart, StringComparison.Ordinal);
            if (endObject < 0)
            {
                throw new PdfFormatException($"Missing endobj marker for object {objectNumber}.");
            }

            PdfObject parsedObject = ParseIndirectObjectValue(bytes, entry.Offset, objectTail, objectBodyStart, endObject);
            PdfObjectId id = new(objectNumber, entry.Generation);
            objects.Add(new PdfIndirectObject(id, parsedObject));
        }

        return objects;
    }

    private static PdfObject ParseIndirectObjectValue(
        ReadOnlySpan<byte> bytes,
        int objectOffset,
        string objectTail,
        int objectBodyStart,
        int endObject)
    {
        int streamKeywordIndex = FindStreamKeyword(objectTail, objectBodyStart, endObject);
        if (streamKeywordIndex < 0)
        {
            string objectBody = objectTail.Substring(objectBodyStart, endObject - objectBodyStart).Trim();
            return PdfObjectParser.ParseAscii(objectBody);
        }

        string dictionaryText = objectTail.Substring(objectBodyStart, streamKeywordIndex - objectBodyStart).Trim();
        PdfDictionaryObject dictionary = PdfObjectParser.ParseAscii(dictionaryText) as PdfDictionaryObject
            ?? throw new PdfFormatException("Stream object dictionary could not be parsed.");

        int length = GetRequiredStreamLength(dictionary);
        int streamDataStartInTail = streamKeywordIndex + "stream".Length;

        if (streamDataStartInTail < objectTail.Length && objectTail[streamDataStartInTail] == '\r')
        {
            streamDataStartInTail++;
        }

        if (streamDataStartInTail < objectTail.Length && objectTail[streamDataStartInTail] == '\n')
        {
            streamDataStartInTail++;
        }

        int absoluteStreamDataStart = objectOffset + streamDataStartInTail;
        if (absoluteStreamDataStart + length > bytes.Length)
        {
            throw new PdfFormatException("Stream data exceeds available PDF bytes.");
        }

        byte[] streamBytes = bytes.Slice(absoluteStreamDataStart, length).ToArray();
        return new PdfStreamObject(dictionary, streamBytes);
    }

    private static int FindStreamKeyword(string objectTail, int startIndex, int endObject)
    {
        int searchIndex = startIndex;
        while (searchIndex < endObject)
        {
            int match = objectTail.IndexOf("stream", searchIndex, StringComparison.Ordinal);
            if (match < 0 || match >= endObject)
            {
                return -1;
            }

            bool validPrefix = match == 0 || char.IsWhiteSpace(objectTail[match - 1]);
            bool validSuffix = match + "stream".Length >= objectTail.Length || char.IsWhiteSpace(objectTail[match + "stream".Length]);
            if (validPrefix && validSuffix)
            {
                return match;
            }

            searchIndex = match + 1;
        }

        return -1;
    }

    private static int GetRequiredStreamLength(PdfDictionaryObject dictionary)
    {
        PdfDictionaryEntry? lengthEntry = dictionary.Entries.FirstOrDefault(entry => entry.Key == "Length");
        if (lengthEntry is null)
        {
            throw new PdfFormatException("Stream dictionary is missing /Length.");
        }

        if (lengthEntry.Value is not PdfNumberObject numberValue || !numberValue.IsInteger)
        {
            throw new PdfFormatException("Stream /Length must be an integer number.");
        }

        if (numberValue.Value < 0)
        {
            throw new PdfFormatException("Stream /Length cannot be negative.");
        }

        return Convert.ToInt32(numberValue.Value, CultureInfo.InvariantCulture);
    }

    private static int ReadInteger(string text, ref int cursor)
    {
        int start = cursor;
        while (cursor < text.Length && char.IsAsciiDigit(text[cursor]))
        {
            cursor++;
        }

        if (start == cursor)
        {
            throw new PdfFormatException("Expected integer while parsing xref section.");
        }

        string value = text[start..cursor];
        return int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static int ReadFixedWidthInteger(string text, ref int cursor, int width)
    {
        if (cursor + width > text.Length)
        {
            throw new PdfFormatException("Unexpected end while parsing fixed-width integer.");
        }

        string value = text.Substring(cursor, width);
        cursor += width;
        return int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static void SkipWhitespace(string text, ref int cursor)
    {
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
        {
            cursor++;
        }
    }

    private static void SkipSpaces(string text, ref int cursor)
    {
        while (cursor < text.Length && text[cursor] == ' ')
        {
            cursor++;
        }
    }

    private static void SkipLineEnding(string text, ref int cursor)
    {
        while (cursor < text.Length && (text[cursor] == '\r' || text[cursor] == '\n' || text[cursor] == ' '))
        {
            cursor++;
        }
    }
}
