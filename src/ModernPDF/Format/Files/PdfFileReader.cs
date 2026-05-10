using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Text;
using ModernPDF.Format.Objects;
using ModernPDF.Primitives;

namespace ModernPDF.Format.Files;

internal static class PdfFileReader
{
    private static readonly HashSet<string> XrefStreamOnlyKeys =
    [
        "Type",
        "Length",
        "Filter",
        "DecodeParms",
        "W",
        "Index",
    ];

    private sealed record CompressedXrefEntry(int ObjectStreamNumber, int ObjectIndex);

    private sealed record XrefSection(
        Dictionary<int, PdfXrefEntry> InUseEntries,
        Dictionary<int, CompressedXrefEntry> CompressedEntries,
        HashSet<int> FreedObjectNumbers,
        PdfDictionaryObject Trailer);

    private sealed record ObjectStreamItem(int ObjectNumber, PdfObject Value);

    public static PdfFile Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            throw new PdfFormatException("PDF data cannot be empty.");
        }

        string text = Encoding.ASCII.GetString(bytes);
        string version = ParseVersion(text);
        int startXrefOffset = ParseStartXrefOffset(text);

        (Dictionary<int, PdfXrefEntry> xrefEntries, Dictionary<int, CompressedXrefEntry> compressedEntries, PdfDictionaryObject trailer) =
            ParseCrossReferenceChain(bytes, startXrefOffset);
        List<PdfIndirectObject> objects = ParseIndirectObjects(bytes, xrefEntries, compressedEntries);

        return new PdfFile(
            version,
            objects,
            trailer,
            sourceBytes: bytes.ToArray(),
            startXrefOffset: startXrefOffset,
            xrefEntries: xrefEntries);
    }

    private static (Dictionary<int, PdfXrefEntry> Entries, Dictionary<int, CompressedXrefEntry> CompressedEntries, PdfDictionaryObject Trailer) ParseCrossReferenceChain(
        ReadOnlySpan<byte> bytes,
        int startXrefOffset)
    {
        HashSet<int> visitedOffsets = [];
        List<XrefSection> sections = [];

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

            XrefSection section = ParseCrossReferenceSection(bytes, currentOffset);
            sections.Add(section);

            if (!TryGetPrevOffset(section.Trailer, out int prevOffset))
            {
                break;
            }

            currentOffset = prevOffset;
        }

        sections.Reverse();
        Dictionary<int, PdfXrefEntry> mergedEntries = [];
        Dictionary<int, CompressedXrefEntry> mergedCompressedEntries = [];
        PdfDictionaryObject mergedTrailer = new([]);
        foreach (XrefSection section in sections)
        {
            foreach (int freedObjectNumber in section.FreedObjectNumbers)
            {
                mergedEntries.Remove(freedObjectNumber);
                mergedCompressedEntries.Remove(freedObjectNumber);
            }

            foreach ((int objectNumber, PdfXrefEntry entry) in section.InUseEntries)
            {
                mergedEntries[objectNumber] = entry;
                mergedCompressedEntries.Remove(objectNumber);
            }

            foreach ((int objectNumber, CompressedXrefEntry compressedEntry) in section.CompressedEntries)
            {
                mergedCompressedEntries[objectNumber] = compressedEntry;
                mergedEntries.Remove(objectNumber);
            }

            mergedTrailer = MergeDictionaries(mergedTrailer, section.Trailer);
        }

        return (mergedEntries, mergedCompressedEntries, mergedTrailer);
    }

    private static XrefSection ParseCrossReferenceSection(ReadOnlySpan<byte> bytes, int startXrefOffset)
    {
        int sectionOffset = SkipPdfWhitespace(bytes, startXrefOffset);
        if (sectionOffset >= bytes.Length)
        {
            throw new PdfFormatException("Cross-reference section offset is outside the PDF byte range.");
        }

        if (StartsWithAscii(bytes, sectionOffset, "xref"))
        {
            (Dictionary<int, PdfXrefEntry> entries, HashSet<int> freedObjectNumbers) = ParseCrossReferenceEntries(bytes, sectionOffset);
            PdfDictionaryObject trailer = ParseTrailer(bytes, sectionOffset);
            return new XrefSection(entries, [], freedObjectNumbers, trailer);
        }

        return ParseXrefStreamSection(bytes, sectionOffset);
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

    private static (Dictionary<int, PdfXrefEntry> Entries, HashSet<int> FreedObjectNumbers) ParseCrossReferenceEntries(ReadOnlySpan<byte> bytes, int startXrefOffset)
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
        HashSet<int> freedObjectNumbers = [];

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
                    freedObjectNumbers.Remove(objectNumber);
                }
                else if (inUse == 'f')
                {
                    int objectNumber = firstObject + index;
                    entries.Remove(objectNumber);
                    freedObjectNumbers.Add(objectNumber);
                }
            }

            SkipWhitespace(tail, ref cursor);
        }

        return (entries, freedObjectNumbers);
    }

    private static XrefSection ParseXrefStreamSection(ReadOnlySpan<byte> bytes, int startXrefOffset)
    {
        PdfIndirectObject xrefIndirectObject = ParseIndirectObjectAtOffset(bytes, startXrefOffset);
        if (xrefIndirectObject.Value is not PdfStreamObject xrefStream)
        {
            throw new PdfFormatException("startxref did not point to a classic xref table or an xref stream object.");
        }

        PdfDictionaryObject dictionary = xrefStream.Dictionary;
        if (TryGetDictionaryEntry(dictionary, "Type", out PdfObject? typeValue))
        {
            if (typeValue is not PdfNameObject typeName || !string.Equals(typeName.Value, "XRef", StringComparison.Ordinal))
            {
                throw new PdfFormatException("Cross-reference stream '/Type' must be '/XRef' when present.");
            }
        }

        int size = GetRequiredNonNegativeIntegerValue(
            RequireDictionaryEntry(dictionary, "Size"),
            "Cross-reference stream '/Size' must be a non-negative integer.");

        PdfObject widthObject = RequireDictionaryEntry(dictionary, "W");
        if (widthObject is not PdfArrayObject widthArray || widthArray.Items.Count != 3)
        {
            throw new PdfFormatException("Cross-reference stream '/W' must be an array of three integers.");
        }

        int fieldTypeWidth = GetRequiredNonNegativeIntegerValue(
            widthArray.Items[0],
            "Cross-reference stream '/W' entries must be non-negative integers.");
        int field2Width = GetRequiredNonNegativeIntegerValue(
            widthArray.Items[1],
            "Cross-reference stream '/W' entries must be non-negative integers.");
        int field3Width = GetRequiredNonNegativeIntegerValue(
            widthArray.Items[2],
            "Cross-reference stream '/W' entries must be non-negative integers.");

        int entryWidth = fieldTypeWidth + field2Width + field3Width;
        if (entryWidth == 0)
        {
            throw new PdfFormatException("Cross-reference stream '/W' cannot describe zero-width entries.");
        }

        List<(int StartObjectNumber, int Count)> indexRanges = [];
        if (TryGetDictionaryEntry(dictionary, "Index", out PdfObject? indexValue))
        {
            if (indexValue is not PdfArrayObject indexArray || (indexArray.Items.Count & 1) == 1)
            {
                throw new PdfFormatException("Cross-reference stream '/Index' must be an even-length integer array.");
            }

            for (int index = 0; index < indexArray.Items.Count; index += 2)
            {
                int firstObjectNumber = GetRequiredNonNegativeIntegerValue(
                    indexArray.Items[index],
                    "Cross-reference stream '/Index' entries must be non-negative integers.");
                int count = GetRequiredNonNegativeIntegerValue(
                    indexArray.Items[index + 1],
                    "Cross-reference stream '/Index' entries must be non-negative integers.");
                indexRanges.Add((firstObjectNumber, count));
            }
        }
        else
        {
            indexRanges.Add((0, size));
        }

        ValidateDecodeParameters(dictionary, context: "Cross-reference stream");
        byte[] decodedStreamData = DecodeStreamData(xrefStream, context: "cross-reference stream");

        Dictionary<int, PdfXrefEntry> inUseEntries = [];
        Dictionary<int, CompressedXrefEntry> compressedEntries = [];
        HashSet<int> freedObjectNumbers = [];
        int cursor = 0;

        foreach ((int startObjectNumber, int count) in indexRanges)
        {
            for (int index = 0; index < count; index++)
            {
                if (cursor + entryWidth > decodedStreamData.Length)
                {
                    throw new PdfFormatException("Cross-reference stream data ended unexpectedly.");
                }

                long entryType = fieldTypeWidth == 0
                    ? 1
                    : ReadBigEndianInteger(decodedStreamData, cursor, fieldTypeWidth);
                cursor += fieldTypeWidth;

                long field2 = ReadBigEndianInteger(decodedStreamData, cursor, field2Width);
                cursor += field2Width;

                long field3 = ReadBigEndianInteger(decodedStreamData, cursor, field3Width);
                cursor += field3Width;

                int objectNumber = startObjectNumber + index;
                switch (entryType)
                {
                    case 0:
                        inUseEntries.Remove(objectNumber);
                        compressedEntries.Remove(objectNumber);
                        freedObjectNumbers.Add(objectNumber);
                        break;
                    case 1:
                    {
                        if (field2 < 0 || field2 > int.MaxValue || field3 < 0 || field3 > int.MaxValue)
                        {
                            throw new PdfFormatException("Cross-reference stream type-1 entry values are out of supported range.");
                        }

                        inUseEntries[objectNumber] = new PdfXrefEntry((int)field2, (int)field3);
                        compressedEntries.Remove(objectNumber);
                        freedObjectNumbers.Remove(objectNumber);
                        break;
                    }
                    case 2:
                    {
                        if (field2 < 0 || field2 > int.MaxValue || field3 < 0 || field3 > int.MaxValue)
                        {
                            throw new PdfFormatException("Cross-reference stream type-2 entry values are out of supported range.");
                        }

                        compressedEntries[objectNumber] = new CompressedXrefEntry((int)field2, (int)field3);
                        inUseEntries.Remove(objectNumber);
                        freedObjectNumbers.Remove(objectNumber);
                        break;
                    }
                    default:
                        throw new PdfFormatException($"Unsupported cross-reference stream entry type '{entryType}'.");
                }
            }
        }

        PdfDictionaryObject trailer = BuildTrailerFromXrefStreamDictionary(dictionary);
        return new XrefSection(inUseEntries, compressedEntries, freedObjectNumbers, trailer);
    }

    private static PdfDictionaryObject BuildTrailerFromXrefStreamDictionary(PdfDictionaryObject xrefStreamDictionary)
    {
        List<PdfDictionaryEntry> entries = [];
        foreach (PdfDictionaryEntry entry in xrefStreamDictionary.Entries)
        {
            if (!XrefStreamOnlyKeys.Contains(entry.Key))
            {
                entries.Add(entry);
            }
        }

        return new PdfDictionaryObject(entries);
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

    private static List<PdfIndirectObject> ParseIndirectObjects(
        ReadOnlySpan<byte> bytes,
        IReadOnlyDictionary<int, PdfXrefEntry> xrefEntries,
        IReadOnlyDictionary<int, CompressedXrefEntry> compressedEntries)
    {
        List<PdfIndirectObject> objects = [];
        Dictionary<int, PdfIndirectObject> parsedByObjectNumber = [];

        foreach ((int objectNumber, PdfXrefEntry entry) in xrefEntries.OrderBy(pair => pair.Key))
        {
            if (entry.Offset < 0 || entry.Offset >= bytes.Length)
            {
                throw new PdfFormatException($"Invalid xref offset for object {objectNumber}.");
            }

            PdfIndirectObject parsed;
            try
            {
                parsed = ParseIndirectObjectAtOffset(bytes, entry.Offset);
            }
            catch (PdfFormatException ex)
            {
                throw new PdfFormatException(
                    $"Failed to parse object {objectNumber} at xref offset {entry.Offset}: {ex.Message}");
            }
            if (parsed.ObjectId.ObjectNumber != objectNumber || parsed.ObjectId.GenerationNumber != entry.Generation)
            {
                throw new PdfFormatException($"Object header mismatch for object {objectNumber}.");
            }

            objects.Add(parsed);
            parsedByObjectNumber[objectNumber] = parsed;
        }

        if (compressedEntries.Count > 0)
        {
            Dictionary<int, List<ObjectStreamItem>> objectStreamCache = [];
            foreach ((int objectNumber, CompressedXrefEntry compressedEntry) in compressedEntries.OrderBy(pair => pair.Key))
            {
                if (!objectStreamCache.TryGetValue(compressedEntry.ObjectStreamNumber, out List<ObjectStreamItem>? parsedObjectStream))
                {
                    parsedObjectStream = ParseObjectStream(compressedEntry.ObjectStreamNumber, parsedByObjectNumber);
                    objectStreamCache[compressedEntry.ObjectStreamNumber] = parsedObjectStream;
                }

                if (compressedEntry.ObjectIndex < 0 || compressedEntry.ObjectIndex >= parsedObjectStream.Count)
                {
                    throw new PdfFormatException(
                        $"Cross-reference stream points to object-stream index {compressedEntry.ObjectIndex} that does not exist.");
                }

                ObjectStreamItem objectStreamItem = parsedObjectStream[compressedEntry.ObjectIndex];
                if (objectStreamItem.ObjectNumber != objectNumber)
                {
                    throw new PdfFormatException(
                        $"Cross-reference stream/object-stream mismatch for compressed object {objectNumber}.");
                }

                PdfObjectId objectId = new(objectNumber, 0);
                PdfIndirectObject compressedObject = new(objectId, objectStreamItem.Value);
                objects.Add(compressedObject);
                parsedByObjectNumber[objectNumber] = compressedObject;
            }
        }

        return objects.OrderBy(static objectItem => objectItem.ObjectId.ObjectNumber).ToList();
    }

    private static PdfIndirectObject ParseIndirectObjectAtOffset(ReadOnlySpan<byte> bytes, int objectOffset)
    {
        string objectTail = Encoding.ASCII.GetString(bytes[objectOffset..]);
        int cursor = 0;

        int objectNumber = ReadInteger(objectTail, ref cursor);
        SkipWhitespace(objectTail, ref cursor);
        int generation = ReadInteger(objectTail, ref cursor);
        SkipWhitespace(objectTail, ref cursor);

        if (!objectTail.AsSpan(cursor).StartsWith("obj".AsSpan(), StringComparison.Ordinal))
        {
            throw new PdfFormatException($"Missing obj keyword for object {objectNumber}.");
        }

        cursor += "obj".Length;
        while (cursor < objectTail.Length && char.IsWhiteSpace(objectTail[cursor]))
        {
            cursor++;
        }

        int endObject = objectTail.IndexOf("endobj", cursor, StringComparison.Ordinal);
        if (endObject < 0)
        {
            throw new PdfFormatException($"Missing endobj marker for object {objectNumber}.");
        }

        PdfObject value = ParseIndirectObjectValue(bytes, objectOffset, objectTail, cursor, endObject);
        return new PdfIndirectObject(new PdfObjectId(objectNumber, generation), value);
    }

    private static List<ObjectStreamItem> ParseObjectStream(
        int objectStreamNumber,
        Dictionary<int, PdfIndirectObject> parsedByObjectNumber)
    {
        if (!parsedByObjectNumber.TryGetValue(objectStreamNumber, out PdfIndirectObject? indirectObject))
        {
            throw new PdfFormatException($"Missing object stream object {objectStreamNumber} required by compressed xref entry.");
        }

        if (indirectObject.Value is not PdfStreamObject objectStream)
        {
            throw new PdfFormatException($"Object stream object {objectStreamNumber} is not a stream.");
        }

        ValidateDecodeParameters(objectStream.Dictionary, context: "Object stream");
        byte[] decodedData = DecodeStreamData(objectStream, context: "object stream");

        int objectCount = GetRequiredNonNegativeIntegerValue(
            RequireDictionaryEntry(objectStream.Dictionary, "N"),
            "Object stream '/N' must be a non-negative integer.");
        int firstObjectOffset = GetRequiredNonNegativeIntegerValue(
            RequireDictionaryEntry(objectStream.Dictionary, "First"),
            "Object stream '/First' must be a non-negative integer.");
        if (firstObjectOffset > decodedData.Length)
        {
            throw new PdfFormatException("Object stream '/First' offset exceeds decoded stream length.");
        }

        ReadOnlySpan<byte> headerData = decodedData.AsSpan(0, firstObjectOffset);
        int headerCursor = 0;
        List<(int ObjectNumber, int RelativeOffset)> descriptors = [];
        for (int index = 0; index < objectCount; index++)
        {
            int objectNumber = ReadAsciiInteger(
                headerData,
                ref headerCursor,
                "Object stream descriptor object number must be an integer.");
            int relativeOffset = ReadAsciiInteger(
                headerData,
                ref headerCursor,
                "Object stream descriptor offset must be an integer.");

            if (objectNumber < 0)
            {
                throw new PdfFormatException("Object stream descriptor object numbers must be non-negative.");
            }

            if (relativeOffset < 0)
            {
                throw new PdfFormatException("Object stream descriptor offsets must be non-negative.");
            }

            descriptors.Add((objectNumber, relativeOffset));
        }

        List<ObjectStreamItem> items = [];
        for (int index = 0; index < descriptors.Count; index++)
        {
            int objectStart = firstObjectOffset + descriptors[index].RelativeOffset;
            int objectEnd = index + 1 < descriptors.Count
                ? firstObjectOffset + descriptors[index + 1].RelativeOffset
                : decodedData.Length;

            if (objectStart < firstObjectOffset || objectStart > decodedData.Length || objectEnd < objectStart || objectEnd > decodedData.Length)
            {
                throw new PdfFormatException("Object stream descriptor offsets are inconsistent with stream boundaries.");
            }

            PdfObject parsedObject = PdfObjectParser.Parse(decodedData.AsSpan(objectStart, objectEnd - objectStart));
            items.Add(new ObjectStreamItem(descriptors[index].ObjectNumber, parsedObject));
        }

        return items;
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

            bool validPrefix = match == 0
                || char.IsWhiteSpace(objectTail[match - 1])
                || objectTail[match - 1] == '>';
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

    private static byte[] DecodeStreamData(PdfStreamObject streamObject, string context)
    {
        List<string> filters = GetFilterNames(streamObject.Dictionary);
        if (filters.Count == 0)
        {
            return streamObject.Data.ToArray();
        }

        byte[] data = streamObject.Data.ToArray();
        foreach (string filter in filters)
        {
            if (string.Equals(filter, "FlateDecode", StringComparison.Ordinal)
                || string.Equals(filter, "Fl", StringComparison.Ordinal))
            {
                data = DecodeFlateData(data, context);
                continue;
            }

            throw new PdfFormatException($"Unsupported stream filter '/{filter}' in {context}.");
        }

        return data;
    }

    internal static byte[] DecodeStreamDataForExtraction(PdfStreamObject streamObject, string context)
    {
        ArgumentNullException.ThrowIfNull(streamObject);
        ValidateDecodeParameters(streamObject.Dictionary, context);
        return DecodeStreamData(streamObject, context);
    }

    private static byte[] DecodeFlateData(byte[] data, string context)
    {
        try
        {
            using MemoryStream compressed = new(data, writable: false);
            using ZLibStream zlibStream = new(compressed, CompressionMode.Decompress);
            using MemoryStream output = new();
            zlibStream.CopyTo(output);
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            throw new PdfFormatException($"Could not decode Flate stream data in {context}.");
        }
    }

    private static List<string> GetFilterNames(PdfDictionaryObject dictionary)
    {
        if (!TryGetDictionaryEntry(dictionary, "Filter", out PdfObject? filterObject))
        {
            return [];
        }

        switch (filterObject)
        {
            case PdfNameObject filterName:
                return [filterName.Value];
            case PdfArrayObject filterArray:
            {
                List<string> names = [];
                foreach (PdfObject item in filterArray.Items)
                {
                    if (item is not PdfNameObject nameObject)
                    {
                        throw new PdfFormatException("Stream '/Filter' array entries must be names.");
                    }

                    names.Add(nameObject.Value);
                }

                return names;
            }
            default:
                throw new PdfFormatException("Stream '/Filter' must be a name or an array of names.");
        }
    }

    private static void ValidateDecodeParameters(PdfDictionaryObject dictionary, string context)
    {
        if (!TryGetDictionaryEntry(dictionary, "DecodeParms", out PdfObject? decodeParmsObject))
        {
            return;
        }

        List<PdfObject> parameterItems = [];
        if (decodeParmsObject is PdfArrayObject parameterArray)
        {
            parameterItems.AddRange(parameterArray.Items);
        }
        else
        {
            parameterItems.Add(decodeParmsObject);
        }

        foreach (PdfObject parameterItem in parameterItems)
        {
            if (parameterItem is PdfNullObject)
            {
                continue;
            }

            if (parameterItem is not PdfDictionaryObject parameterDictionary)
            {
                throw new PdfFormatException($"{context} '/DecodeParms' must be a dictionary, null, or an array of those values.");
            }

            if (!TryGetDictionaryEntry(parameterDictionary, "Predictor", out PdfObject? predictorValue))
            {
                continue;
            }

            int predictor = GetRequiredNonNegativeIntegerValue(
                predictorValue,
                $"{context} '/DecodeParms /Predictor' must be a non-negative integer.");
            if (predictor != 1)
            {
                throw new PdfFormatException($"{context} '/DecodeParms /Predictor' values other than 1 are not supported.");
            }
        }
    }

    private static bool TryGetDictionaryEntry(PdfDictionaryObject dictionary, string key, [NotNullWhen(true)] out PdfObject? value)
    {
        foreach (PdfDictionaryEntry entry in dictionary.Entries)
        {
            if (string.Equals(entry.Key, key, StringComparison.Ordinal))
            {
                value = entry.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static PdfObject RequireDictionaryEntry(PdfDictionaryObject dictionary, string key)
    {
        if (!TryGetDictionaryEntry(dictionary, key, out PdfObject? value))
        {
            throw new PdfFormatException($"Required dictionary entry '/{key}' was not found.");
        }

        return value;
    }

    private static int GetRequiredNonNegativeIntegerValue(PdfObject value, string errorMessage)
    {
        if (value is not PdfNumberObject numberValue
            || !numberValue.IsInteger
            || !double.IsFinite(numberValue.Value)
            || numberValue.Value < 0
            || numberValue.Value > int.MaxValue)
        {
            throw new PdfFormatException(errorMessage);
        }

        return Convert.ToInt32(numberValue.Value, CultureInfo.InvariantCulture);
    }

    private static int ReadAsciiInteger(ReadOnlySpan<byte> data, ref int cursor, string errorMessage)
    {
        SkipWhitespace(data, ref cursor);
        if (cursor >= data.Length)
        {
            throw new PdfFormatException(errorMessage);
        }

        int start = cursor;
        if (data[cursor] is (byte)'+' or (byte)'-')
        {
            cursor++;
        }

        int digitStart = cursor;
        while (cursor < data.Length && char.IsAsciiDigit((char)data[cursor]))
        {
            cursor++;
        }

        if (digitStart == cursor)
        {
            throw new PdfFormatException(errorMessage);
        }

        string token = Encoding.ASCII.GetString(data.Slice(start, cursor - start));
        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            throw new PdfFormatException(errorMessage);
        }

        return value;
    }

    private static long ReadBigEndianInteger(byte[] data, int start, int width)
    {
        if (width == 0)
        {
            return 0;
        }

        if (width < 0 || width > 8)
        {
            throw new PdfFormatException("Cross-reference stream field widths above 8 bytes are not supported.");
        }

        ulong value = 0;
        for (int index = 0; index < width; index++)
        {
            value = (value << 8) | data[start + index];
        }

        if (value > long.MaxValue)
        {
            throw new PdfFormatException("Cross-reference stream integer field exceeded supported range.");
        }

        return (long)value;
    }

    private static int SkipPdfWhitespace(ReadOnlySpan<byte> bytes, int offset)
    {
        int cursor = offset;
        while (cursor < bytes.Length && IsPdfWhitespace(bytes[cursor]))
        {
            cursor++;
        }

        return cursor;
    }

    private static bool StartsWithAscii(ReadOnlySpan<byte> bytes, int offset, string value)
    {
        if (offset < 0 || offset + value.Length > bytes.Length)
        {
            return false;
        }

        for (int index = 0; index < value.Length; index++)
        {
            if (bytes[offset + index] != (byte)value[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPdfWhitespace(byte value)
    {
        return value is 0x00 or 0x09 or 0x0A or 0x0C or 0x0D or 0x20;
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

    private static void SkipWhitespace(ReadOnlySpan<byte> data, ref int cursor)
    {
        while (cursor < data.Length && IsPdfWhitespace(data[cursor]))
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
