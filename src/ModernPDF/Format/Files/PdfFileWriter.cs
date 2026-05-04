using System.Globalization;
using System.Text;
using ModernPDF.Format.Objects;
using ModernPDF.Primitives;

namespace ModernPDF.Format.Files;

internal static class PdfFileWriter
{
    private const int XrefStreamField2Width = 4;
    private const int XrefStreamField3Width = 2;

    private static readonly HashSet<string> XrefStreamOnlyKeys =
    [
        "Type",
        "Length",
        "Filter",
        "DecodeParms",
        "W",
        "Index",
        "XRefStm",
    ];

    private readonly record struct CompressedEntryInfo(int ObjectStreamNumber, int ObjectIndex);

    private readonly record struct XrefStreamEntry(byte Type, int Field2, int Field3);

    private readonly record struct IndexRange(int StartObjectNumber, int Count);

    public static byte[] Write(PdfFile file, PdfCrossReferenceStyle crossReferenceStyle = PdfCrossReferenceStyle.Classic)
    {
        ArgumentNullException.ThrowIfNull(file);

        return crossReferenceStyle switch
        {
            PdfCrossReferenceStyle.Classic => WriteClassic(file),
            PdfCrossReferenceStyle.Stream => WriteWithXrefStream(file),
            _ => throw new ArgumentOutOfRangeException(nameof(crossReferenceStyle), "Unsupported cross-reference style."),
        };
    }

    public static byte[] WriteIncremental(
        PdfFile file,
        IReadOnlyCollection<PdfObjectId> dirtyObjectIds,
        PdfCrossReferenceStyle crossReferenceStyle = PdfCrossReferenceStyle.Classic)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(dirtyObjectIds);

        if (!HasUsableIncrementalMetadata(file))
        {
            return Write(file, crossReferenceStyle);
        }

        byte[] sourceBytes = file.SourceBytes!;
        int previousXrefOffset = file.StartXrefOffset!.Value;

        if (dirtyObjectIds.Count == 0)
        {
            return sourceBytes.ToArray();
        }

        HashSet<PdfObjectId> currentObjectIds = [.. file.Objects.Select(static objectItem => objectItem.ObjectId)];
        foreach (PdfObjectId dirtyObjectId in dirtyObjectIds)
        {
            if (!currentObjectIds.Contains(dirtyObjectId))
            {
                throw new PdfFormatException($"Dirty object {dirtyObjectId} is not present in the current document object set.");
            }
        }

        HashSet<PdfObjectId> dirtyIds = [.. dirtyObjectIds];
        List<PdfIndirectObject> objectsToRewrite = file.Objects
            .Where(objectItem => dirtyIds.Contains(objectItem.ObjectId) || !file.XrefEntries.ContainsKey(objectItem.ObjectId.ObjectNumber))
            .OrderBy(objectItem => objectItem.ObjectId.ObjectNumber)
            .ToList();
        if (objectsToRewrite.Count == 0)
        {
            return sourceBytes.ToArray();
        }

        return crossReferenceStyle switch
        {
            PdfCrossReferenceStyle.Classic => WriteIncrementalClassic(file, sourceBytes, previousXrefOffset, objectsToRewrite),
            PdfCrossReferenceStyle.Stream => WriteIncrementalWithXrefStream(file, sourceBytes, previousXrefOffset, objectsToRewrite),
            _ => throw new ArgumentOutOfRangeException(nameof(crossReferenceStyle), "Unsupported cross-reference style."),
        };
    }

    private static byte[] WriteClassic(PdfFile file)
    {
        ByteBufferWriter writer = new();
        Dictionary<int, PdfObjectId> objectIds = [];
        Dictionary<int, int> objectOffsets = [];

        WriteHeader(writer, file.Version);

        foreach (PdfIndirectObject indirectObject in file.Objects.OrderBy(item => item.ObjectId.ObjectNumber))
        {
            int objectNumber = indirectObject.ObjectId.ObjectNumber;
            objectIds[objectNumber] = indirectObject.ObjectId;
            objectOffsets[objectNumber] = writer.WrittenCount;

            WriteIndirectObject(writer, indirectObject.ObjectId, indirectObject.Value);
        }

        int xrefOffset = writer.WrittenCount;
        Dictionary<int, PdfXrefEntry> xrefEntries = BuildInUseXrefEntries(objectOffsets, objectIds);
        WriteCrossReferenceTable(writer, xrefEntries, objectIds);

        PdfDictionaryObject trailer = BuildTrailerWithSize(file.Trailer, objectOffsets);
        WriteAscii(writer, "trailer\n");
        writer.Write(PdfObjectWriter.Write(trailer));
        WriteAscii(writer, "\nstartxref\n");
        WriteAscii(writer, xrefOffset.ToString(CultureInfo.InvariantCulture));
        WriteAscii(writer, "\n%%EOF\n");

        return writer.ToArray();
    }

    private static byte[] WriteWithXrefStream(PdfFile file)
    {
        List<PdfIndirectObject> sortedObjects = file.Objects
            .OrderBy(static objectItem => objectItem.ObjectId.ObjectNumber)
            .ToList();
        HashSet<int> forcedUncompressedObjectNumbers = GetTrailerReferenceObjectNumbers(file.Trailer);

        List<PdfIndirectObject> uncompressedObjects = [];
        List<PdfIndirectObject> objectsForObjectStream = [];
        foreach (PdfIndirectObject indirectObject in sortedObjects)
        {
            bool mustBeUncompressed = indirectObject.Value is PdfStreamObject
                || indirectObject.ObjectId.GenerationNumber != 0
                || forcedUncompressedObjectNumbers.Contains(indirectObject.ObjectId.ObjectNumber);
            if (mustBeUncompressed)
            {
                uncompressedObjects.Add(indirectObject);
            }
            else
            {
                objectsForObjectStream.Add(indirectObject);
            }
        }

        Dictionary<int, CompressedEntryInfo> compressedEntries = [];
        int nextObjectNumber = GetNextObjectNumber(sortedObjects.Select(static objectItem => objectItem.ObjectId.ObjectNumber));
        if (objectsForObjectStream.Count > 0)
        {
            PdfIndirectObject objectStream = BuildObjectStreamObject(objectsForObjectStream, nextObjectNumber, compressedEntries);
            uncompressedObjects.Add(objectStream);
            nextObjectNumber++;
        }

        int xrefObjectNumber = nextObjectNumber;
        ByteBufferWriter writer = new();
        WriteHeader(writer, file.Version);

        Dictionary<int, PdfXrefEntry> uncompressedOffsets = [];
        foreach (PdfIndirectObject indirectObject in uncompressedObjects.OrderBy(static objectItem => objectItem.ObjectId.ObjectNumber))
        {
            int objectNumber = indirectObject.ObjectId.ObjectNumber;
            uncompressedOffsets[objectNumber] = new PdfXrefEntry(writer.WrittenCount, indirectObject.ObjectId.GenerationNumber);
            WriteIndirectObject(writer, indirectObject.ObjectId, indirectObject.Value);
        }

        int xrefOffset = writer.WrittenCount;
        int maxObjectNumber = Math.Max(GetMaxObjectNumber(sortedObjects.Select(static objectItem => objectItem.ObjectId.ObjectNumber)), xrefObjectNumber);
        int size = maxObjectNumber + 1;

        Dictionary<int, XrefStreamEntry> xrefEntries = [];
        xrefEntries[0] = new XrefStreamEntry(0, 0, 65535);
        foreach ((int objectNumber, PdfXrefEntry entry) in uncompressedOffsets)
        {
            xrefEntries[objectNumber] = new XrefStreamEntry(1, entry.Offset, entry.Generation);
        }

        foreach ((int objectNumber, CompressedEntryInfo compressedEntry) in compressedEntries)
        {
            xrefEntries[objectNumber] = new XrefStreamEntry(2, compressedEntry.ObjectStreamNumber, compressedEntry.ObjectIndex);
        }

        xrefEntries[xrefObjectNumber] = new XrefStreamEntry(1, xrefOffset, 0);

        List<IndexRange> indexRanges = [new IndexRange(0, size)];
        byte[] xrefPayload = BuildXrefStreamPayload(indexRanges, xrefEntries);
        PdfDictionaryObject xrefDictionary = BuildXrefStreamDictionary(file.Trailer, size, indexRanges, previousXrefOffset: null);
        PdfStreamObject xrefStream = new(xrefDictionary, xrefPayload);
        WriteIndirectObject(writer, new PdfObjectId(xrefObjectNumber, 0), xrefStream);

        WriteAscii(writer, "startxref\n");
        WriteAscii(writer, xrefOffset.ToString(CultureInfo.InvariantCulture));
        WriteAscii(writer, "\n%%EOF\n");
        return writer.ToArray();
    }

    private static byte[] WriteIncrementalClassic(
        PdfFile file,
        byte[] sourceBytes,
        int previousXrefOffset,
        List<PdfIndirectObject> objectsToRewrite)
    {
        ByteBufferWriter writer = CreateAppendWriter(sourceBytes);

        Dictionary<int, PdfXrefEntry> mergedEntries = new(file.XrefEntries);
        Dictionary<int, PdfObjectId> objectIds = [];
        foreach (PdfIndirectObject objectItem in file.Objects)
        {
            objectIds[objectItem.ObjectId.ObjectNumber] = objectItem.ObjectId;
        }

        foreach (PdfIndirectObject objectItem in objectsToRewrite)
        {
            int objectNumber = objectItem.ObjectId.ObjectNumber;
            int offset = writer.WrittenCount;
            WriteIndirectObject(writer, objectItem.ObjectId, objectItem.Value);
            mergedEntries[objectNumber] = new PdfXrefEntry(offset, objectItem.ObjectId.GenerationNumber);
        }

        int xrefOffset = writer.WrittenCount;
        WriteCrossReferenceTable(writer, mergedEntries, objectIds);

        PdfDictionaryObject trailer = BuildIncrementalTrailer(
            file.Trailer,
            mergedEntries,
            previousXrefOffset);
        WriteAscii(writer, "trailer\n");
        writer.Write(PdfObjectWriter.Write(trailer));
        WriteAscii(writer, "\nstartxref\n");
        WriteAscii(writer, xrefOffset.ToString(CultureInfo.InvariantCulture));
        WriteAscii(writer, "\n%%EOF\n");
        return FinalizeIncrementalWrite(writer, sourceBytes);
    }

    private static byte[] WriteIncrementalWithXrefStream(
        PdfFile file,
        byte[] sourceBytes,
        int previousXrefOffset,
        List<PdfIndirectObject> objectsToRewrite)
    {
        ByteBufferWriter writer = CreateAppendWriter(sourceBytes);
        HashSet<int> forcedUncompressedObjectNumbers = GetTrailerReferenceObjectNumbers(file.Trailer);
        List<PdfIndirectObject> uncompressedObjectsToRewrite = [];
        List<PdfIndirectObject> objectsForObjectStream = [];
        foreach (PdfIndirectObject objectItem in objectsToRewrite)
        {
            bool mustBeUncompressed = objectItem.Value is PdfStreamObject
                || objectItem.ObjectId.GenerationNumber != 0
                || forcedUncompressedObjectNumbers.Contains(objectItem.ObjectId.ObjectNumber);
            if (mustBeUncompressed)
            {
                uncompressedObjectsToRewrite.Add(objectItem);
            }
            else
            {
                objectsForObjectStream.Add(objectItem);
            }
        }

        Dictionary<int, PdfXrefEntry> rewrittenUncompressedEntries = [];
        foreach (PdfIndirectObject objectItem in uncompressedObjectsToRewrite)
        {
            int offset = writer.WrittenCount;
            WriteIndirectObject(writer, objectItem.ObjectId, objectItem.Value);
            rewrittenUncompressedEntries[objectItem.ObjectId.ObjectNumber] = new PdfXrefEntry(offset, objectItem.ObjectId.GenerationNumber);
        }

        Dictionary<int, CompressedEntryInfo> compressedEntries = [];
        int nextObjectNumber = GetNextObjectNumber(file.Objects.Select(static objectItem => objectItem.ObjectId.ObjectNumber));
        if (objectsForObjectStream.Count > 0)
        {
            PdfIndirectObject objectStream = BuildObjectStreamObject(objectsForObjectStream, nextObjectNumber, compressedEntries);
            int objectStreamOffset = writer.WrittenCount;
            WriteIndirectObject(writer, objectStream.ObjectId, objectStream.Value);
            rewrittenUncompressedEntries[objectStream.ObjectId.ObjectNumber] = new PdfXrefEntry(objectStreamOffset, objectStream.ObjectId.GenerationNumber);
            nextObjectNumber++;
        }

        int xrefObjectNumber = nextObjectNumber;
        int xrefOffset = writer.WrittenCount;

        Dictionary<int, XrefStreamEntry> xrefEntries = [];
        xrefEntries[0] = new XrefStreamEntry(0, 0, 65535);
        foreach ((int objectNumber, PdfXrefEntry rewrittenEntry) in rewrittenUncompressedEntries)
        {
            xrefEntries[objectNumber] = new XrefStreamEntry(1, rewrittenEntry.Offset, rewrittenEntry.Generation);
        }

        foreach ((int objectNumber, CompressedEntryInfo compressedEntry) in compressedEntries)
        {
            xrefEntries[objectNumber] = new XrefStreamEntry(2, compressedEntry.ObjectStreamNumber, compressedEntry.ObjectIndex);
        }

        xrefEntries[xrefObjectNumber] = new XrefStreamEntry(1, xrefOffset, 0);

        List<IndexRange> indexRanges = BuildContiguousRanges(xrefEntries.Keys);
        int maxObjectNumber = Math.Max(
            xrefObjectNumber,
            Math.Max(
                GetMaxObjectNumber(file.Objects.Select(static objectItem => objectItem.ObjectId.ObjectNumber)),
                GetMaxObjectNumber(file.XrefEntries.Keys)));
        int size = maxObjectNumber + 1;
        byte[] xrefPayload = BuildXrefStreamPayload(indexRanges, xrefEntries);
        PdfDictionaryObject xrefDictionary = BuildXrefStreamDictionary(file.Trailer, size, indexRanges, previousXrefOffset);
        PdfStreamObject xrefStream = new(xrefDictionary, xrefPayload);
        WriteIndirectObject(writer, new PdfObjectId(xrefObjectNumber, 0), xrefStream);

        WriteAscii(writer, "startxref\n");
        WriteAscii(writer, xrefOffset.ToString(CultureInfo.InvariantCulture));
        WriteAscii(writer, "\n%%EOF\n");
        return FinalizeIncrementalWrite(writer, sourceBytes);
    }

    private static byte[] FinalizeIncrementalWrite(ByteBufferWriter writer, byte[] sourceBytes)
    {
        byte[] output = writer.ToArray();
        if (!output.AsSpan(0, sourceBytes.Length).SequenceEqual(sourceBytes))
        {
            throw new PdfFormatException("Incremental writer modified bytes prior to append boundary.");
        }

        return output;
    }

    private static ByteBufferWriter CreateAppendWriter(byte[] sourceBytes)
    {
        ByteBufferWriter writer = new();
        writer.Write(sourceBytes.AsSpan());
        if (sourceBytes.Length > 0)
        {
            byte lastByte = sourceBytes[^1];
            if (lastByte is not (byte)'\n' and not (byte)'\r')
            {
                WriteAscii(writer, "\n");
            }
        }

        return writer;
    }

    private static PdfIndirectObject BuildObjectStreamObject(
        List<PdfIndirectObject> objectsForObjectStream,
        int objectStreamNumber,
        Dictionary<int, CompressedEntryInfo> compressedEntries)
    {
        ByteBufferWriter headerWriter = new();
        ByteBufferWriter contentWriter = new();

        int index = 0;
        foreach (PdfIndirectObject objectItem in objectsForObjectStream.OrderBy(static item => item.ObjectId.ObjectNumber))
        {
            WriteAscii(headerWriter, $"{objectItem.ObjectId.ObjectNumber} {contentWriter.WrittenCount} ");
            contentWriter.Write(PdfObjectWriter.Write(objectItem.Value));
            WriteAscii(contentWriter, "\n");
            compressedEntries[objectItem.ObjectId.ObjectNumber] = new CompressedEntryInfo(objectStreamNumber, index);
            index++;
        }

        ByteBufferWriter objectStreamDataWriter = new();
        objectStreamDataWriter.Write(headerWriter.WrittenSpan);
        int first = headerWriter.WrittenCount;
        objectStreamDataWriter.Write(contentWriter.WrittenSpan);

        PdfDictionaryObject dictionary = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("ObjStm")),
            new PdfDictionaryEntry("N", new PdfNumberObject(objectsForObjectStream.Count, isInteger: true)),
            new PdfDictionaryEntry("First", new PdfNumberObject(first, isInteger: true)),
        ]);

        PdfStreamObject objectStream = new(dictionary, objectStreamDataWriter.ToArray());
        return new PdfIndirectObject(new PdfObjectId(objectStreamNumber, 0), objectStream);
    }

    private static List<IndexRange> BuildContiguousRanges(IEnumerable<int> objectNumbers)
    {
        List<int> sorted = objectNumbers
            .Where(static objectNumber => objectNumber >= 0)
            .Distinct()
            .OrderBy(static objectNumber => objectNumber)
            .ToList();
        if (sorted.Count == 0)
        {
            return [];
        }

        List<IndexRange> ranges = [];
        int currentStart = sorted[0];
        int previous = currentStart;
        int count = 1;

        for (int index = 1; index < sorted.Count; index++)
        {
            int value = sorted[index];
            if (value == previous + 1)
            {
                count++;
                previous = value;
                continue;
            }

            ranges.Add(new IndexRange(currentStart, count));
            currentStart = value;
            previous = value;
            count = 1;
        }

        ranges.Add(new IndexRange(currentStart, count));
        return ranges;
    }

    private static PdfDictionaryObject BuildXrefStreamDictionary(
        PdfDictionaryObject originalTrailer,
        int size,
        List<IndexRange> indexRanges,
        int? previousXrefOffset)
    {
        List<PdfDictionaryEntry> entries = [];
        foreach (PdfDictionaryEntry entry in originalTrailer.Entries)
        {
            if (!string.Equals(entry.Key, "Size", StringComparison.Ordinal)
                && !string.Equals(entry.Key, "Prev", StringComparison.Ordinal)
                && !XrefStreamOnlyKeys.Contains(entry.Key))
            {
                entries.Add(entry);
            }
        }

        entries.Add(new PdfDictionaryEntry("Type", new PdfNameObject("XRef")));
        entries.Add(new PdfDictionaryEntry("Size", new PdfNumberObject(size, isInteger: true)));
        entries.Add(new PdfDictionaryEntry(
            "W",
            new PdfArrayObject(
            [
                new PdfNumberObject(1, isInteger: true),
                new PdfNumberObject(XrefStreamField2Width, isInteger: true),
                new PdfNumberObject(XrefStreamField3Width, isInteger: true),
            ])));
        entries.Add(new PdfDictionaryEntry("Index", BuildIndexArray(indexRanges)));
        if (previousXrefOffset.HasValue)
        {
            entries.Add(new PdfDictionaryEntry("Prev", new PdfNumberObject(previousXrefOffset.Value, isInteger: true)));
        }

        return new PdfDictionaryObject(entries);
    }

    private static PdfArrayObject BuildIndexArray(List<IndexRange> ranges)
    {
        List<PdfObject> items = [];
        foreach (IndexRange range in ranges)
        {
            items.Add(new PdfNumberObject(range.StartObjectNumber, isInteger: true));
            items.Add(new PdfNumberObject(range.Count, isInteger: true));
        }

        return new PdfArrayObject(items);
    }

    private static byte[] BuildXrefStreamPayload(List<IndexRange> indexRanges, Dictionary<int, XrefStreamEntry> xrefEntries)
    {
        ByteBufferWriter payloadWriter = new();
        foreach (IndexRange range in indexRanges)
        {
            for (int objectNumber = range.StartObjectNumber; objectNumber < range.StartObjectNumber + range.Count; objectNumber++)
            {
                XrefStreamEntry entry = xrefEntries.TryGetValue(objectNumber, out XrefStreamEntry value)
                    ? value
                    : objectNumber == 0
                        ? new XrefStreamEntry(0, 0, 65535)
                        : new XrefStreamEntry(0, 0, 0);

                payloadWriter.WriteByte(entry.Type);
                WriteUnsignedInt32(payloadWriter, entry.Field2);
                WriteUnsignedInt16(payloadWriter, entry.Field3);
            }
        }

        return payloadWriter.ToArray();
    }

    private static void WriteUnsignedInt32(ByteBufferWriter writer, int value)
    {
        if (value < 0)
        {
            throw new PdfFormatException("Cross-reference stream field value cannot be negative.");
        }

        writer.WriteByte((byte)((value >> 24) & 0xFF));
        writer.WriteByte((byte)((value >> 16) & 0xFF));
        writer.WriteByte((byte)((value >> 8) & 0xFF));
        writer.WriteByte((byte)(value & 0xFF));
    }

    private static void WriteUnsignedInt16(ByteBufferWriter writer, int value)
    {
        if (value < 0 || value > ushort.MaxValue)
        {
            throw new PdfFormatException("Cross-reference stream field value exceeded 16-bit range.");
        }

        writer.WriteByte((byte)((value >> 8) & 0xFF));
        writer.WriteByte((byte)(value & 0xFF));
    }

    private static HashSet<int> GetTrailerReferenceObjectNumbers(PdfDictionaryObject trailer)
    {
        HashSet<int> objectNumbers = [];
        foreach (PdfDictionaryEntry entry in trailer.Entries)
        {
            if ((string.Equals(entry.Key, "Root", StringComparison.Ordinal)
                    || string.Equals(entry.Key, "Info", StringComparison.Ordinal)
                    || string.Equals(entry.Key, "Encrypt", StringComparison.Ordinal))
                && entry.Value is PdfReferenceObject reference)
            {
                objectNumbers.Add(reference.ObjectId.ObjectNumber);
            }
        }

        return objectNumbers;
    }

    private static int GetMaxObjectNumber(IEnumerable<int> objectNumbers)
    {
        int max = 0;
        bool hasAny = false;
        foreach (int objectNumber in objectNumbers)
        {
            hasAny = true;
            if (objectNumber > max)
            {
                max = objectNumber;
            }
        }

        return hasAny ? max : 0;
    }

    private static int GetNextObjectNumber(IEnumerable<int> objectNumbers)
    {
        return GetMaxObjectNumber(objectNumbers) + 1;
    }

    private static void WriteIndirectObject(ByteBufferWriter writer, PdfObjectId objectId, PdfObject value)
    {
        WriteAscii(writer, $"{objectId.ObjectNumber} {objectId.GenerationNumber} obj\n");
        WriteIndirectObjectValue(writer, value);
        WriteAscii(writer, "\nendobj\n");
    }

    private static void WriteIndirectObjectValue(ByteBufferWriter writer, PdfObject value)
    {
        if (value is PdfStreamObject streamObject)
        {
            PdfDictionaryObject streamDictionary = BuildStreamDictionaryWithLength(streamObject.Dictionary, streamObject.Data.Length);
            writer.Write(PdfObjectWriter.Write(streamDictionary));
            WriteAscii(writer, "\nstream\n");
            writer.Write(streamObject.Data.Span);
            WriteAscii(writer, "\nendstream");
            return;
        }

        writer.Write(PdfObjectWriter.Write(value));
    }

    private static PdfDictionaryObject BuildStreamDictionaryWithLength(PdfDictionaryObject originalDictionary, int length)
    {
        List<PdfDictionaryEntry> entries = [];

        foreach (PdfDictionaryEntry entry in originalDictionary.Entries)
        {
            if (!string.Equals(entry.Key, "Length", StringComparison.Ordinal))
            {
                entries.Add(entry);
            }
        }

        entries.Add(new PdfDictionaryEntry("Length", new PdfNumberObject(length, isInteger: true)));
        return new PdfDictionaryObject(entries);
    }

    private static void WriteHeader(ByteBufferWriter writer, string version)
    {
        WriteAscii(writer, $"%PDF-{version}\n");
        writer.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });
    }

    private static Dictionary<int, PdfXrefEntry> BuildInUseXrefEntries(
        Dictionary<int, int> objectOffsets,
        Dictionary<int, PdfObjectId> objectIds)
    {
        Dictionary<int, PdfXrefEntry> entries = [];
        foreach ((int objectNumber, int offset) in objectOffsets)
        {
            if (!objectIds.TryGetValue(objectNumber, out PdfObjectId objectId))
            {
                continue;
            }

            entries[objectNumber] = new PdfXrefEntry(offset, objectId.GenerationNumber);
        }

        return entries;
    }

    private static void WriteCrossReferenceTable(
        ByteBufferWriter writer,
        Dictionary<int, PdfXrefEntry> xrefEntries,
        Dictionary<int, PdfObjectId> objectIds)
    {
        int maxObjectNumber = xrefEntries.Count == 0 ? 0 : xrefEntries.Keys.Max();

        WriteAscii(writer, "xref\n");
        WriteAscii(writer, $"0 {maxObjectNumber + 1}\n");
        WriteAscii(writer, "0000000000 65535 f \n");

        for (int objectNumber = 1; objectNumber <= maxObjectNumber; objectNumber++)
        {
            if (xrefEntries.TryGetValue(objectNumber, out PdfXrefEntry xrefEntry)
                && objectIds.TryGetValue(objectNumber, out PdfObjectId objectId))
            {
                WriteAscii(
                    writer,
                    $"{xrefEntry.Offset:D10} {objectId.GenerationNumber:D5} n \n");
            }
            else
            {
                WriteAscii(writer, "0000000000 00000 f \n");
            }
        }
    }

    private static PdfDictionaryObject BuildTrailerWithSize(PdfDictionaryObject originalTrailer, IReadOnlyDictionary<int, int> objectOffsets)
    {
        int size = objectOffsets.Count == 0 ? 1 : objectOffsets.Keys.Max() + 1;
        List<PdfDictionaryEntry> entries = [];

        foreach (PdfDictionaryEntry entry in originalTrailer.Entries)
        {
            if (!string.Equals(entry.Key, "Size", StringComparison.Ordinal))
            {
                entries.Add(entry);
            }
        }

        entries.Add(new PdfDictionaryEntry("Size", new PdfNumberObject(size, isInteger: true)));
        return new PdfDictionaryObject(entries);
    }

    private static PdfDictionaryObject BuildIncrementalTrailer(
        PdfDictionaryObject originalTrailer,
        Dictionary<int, PdfXrefEntry> xrefEntries,
        int previousXrefOffset)
    {
        int size = xrefEntries.Count == 0 ? 1 : xrefEntries.Keys.Max() + 1;
        List<PdfDictionaryEntry> entries = [];

        foreach (PdfDictionaryEntry entry in originalTrailer.Entries)
        {
            if (!string.Equals(entry.Key, "Size", StringComparison.Ordinal)
                && !string.Equals(entry.Key, "Prev", StringComparison.Ordinal))
            {
                entries.Add(entry);
            }
        }

        entries.Add(new PdfDictionaryEntry("Size", new PdfNumberObject(size, isInteger: true)));
        entries.Add(new PdfDictionaryEntry("Prev", new PdfNumberObject(previousXrefOffset, isInteger: true)));
        return new PdfDictionaryObject(entries);
    }

    private static void WriteAscii(ByteBufferWriter writer, string value)
    {
        writer.Write(Encoding.ASCII.GetBytes(value));
    }

    private static bool HasUsableIncrementalMetadata(PdfFile file)
    {
        if (file.SourceBytes is null
            || !file.StartXrefOffset.HasValue
            || file.XrefEntries.Count == 0)
        {
            return false;
        }

        if (file.StartXrefOffset.Value < 0 || file.StartXrefOffset.Value >= file.SourceBytes.Length)
        {
            return false;
        }

        foreach ((int objectNumber, PdfXrefEntry entry) in file.XrefEntries)
        {
            if (objectNumber <= 0
                || entry.Offset < 0
                || entry.Offset >= file.SourceBytes.Length
                || entry.Generation < 0)
            {
                return false;
            }
        }

        return true;
    }
}
