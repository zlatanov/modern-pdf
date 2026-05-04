using System.Globalization;
using System.Text;
using ModernPDF.Format.Objects;
using ModernPDF.Primitives;

namespace ModernPDF.Format.Files;

internal static class PdfFileWriter
{
    public static byte[] Write(PdfFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        ByteBufferWriter writer = new();
        Dictionary<int, PdfObjectId> objectIds = [];
        Dictionary<int, int> objectOffsets = [];

        WriteHeader(writer, file.Version);

        foreach (PdfIndirectObject indirectObject in file.Objects.OrderBy(item => item.ObjectId.ObjectNumber))
        {
            int objectNumber = indirectObject.ObjectId.ObjectNumber;
            objectIds[objectNumber] = indirectObject.ObjectId;
            objectOffsets[objectNumber] = writer.WrittenCount;

            WriteAscii(writer, $"{objectNumber} {indirectObject.ObjectId.GenerationNumber} obj\n");
            WriteIndirectObjectValue(writer, indirectObject.Value);
            WriteAscii(writer, "\nendobj\n");
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

    public static byte[] WriteIncremental(PdfFile file, IReadOnlyCollection<PdfObjectId> dirtyObjectIds)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(dirtyObjectIds);

        if (file.SourceBytes is null
            || !file.StartXrefOffset.HasValue
            || file.XrefEntries.Count == 0)
        {
            return Write(file);
        }

        if (dirtyObjectIds.Count == 0)
        {
            return file.SourceBytes.ToArray();
        }

        HashSet<PdfObjectId> dirtyIds = [.. dirtyObjectIds];
        List<PdfIndirectObject> objectsToRewrite = file.Objects
            .Where(objectItem => dirtyIds.Contains(objectItem.ObjectId) || !file.XrefEntries.ContainsKey(objectItem.ObjectId.ObjectNumber))
            .OrderBy(objectItem => objectItem.ObjectId.ObjectNumber)
            .ToList();
        if (objectsToRewrite.Count == 0)
        {
            return file.SourceBytes.ToArray();
        }

        ByteBufferWriter writer = new();
        writer.Write(file.SourceBytes.AsSpan());
        if (file.SourceBytes.Length > 0)
        {
            byte lastByte = file.SourceBytes[^1];
            if (lastByte is not (byte)'\n' and not (byte)'\r')
            {
                WriteAscii(writer, "\n");
            }
        }

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
            WriteAscii(writer, $"{objectNumber} {objectItem.ObjectId.GenerationNumber} obj\n");
            WriteIndirectObjectValue(writer, objectItem.Value);
            WriteAscii(writer, "\nendobj\n");
            mergedEntries[objectNumber] = new PdfXrefEntry(offset, objectItem.ObjectId.GenerationNumber);
        }

        int xrefOffset = writer.WrittenCount;
        WriteCrossReferenceTable(writer, mergedEntries, objectIds);

        PdfDictionaryObject trailer = BuildIncrementalTrailer(
            file.Trailer,
            mergedEntries,
            file.StartXrefOffset.Value);
        WriteAscii(writer, "trailer\n");
        writer.Write(PdfObjectWriter.Write(trailer));
        WriteAscii(writer, "\nstartxref\n");
        WriteAscii(writer, xrefOffset.ToString(CultureInfo.InvariantCulture));
        WriteAscii(writer, "\n%%EOF\n");
        return writer.ToArray();
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
}
