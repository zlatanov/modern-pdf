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
        WriteCrossReferenceTable(writer, objectOffsets, objectIds);

        PdfDictionaryObject trailer = BuildTrailerWithSize(file.Trailer, objectOffsets);
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

    private static void WriteCrossReferenceTable(
        ByteBufferWriter writer,
        Dictionary<int, int> objectOffsets,
        Dictionary<int, PdfObjectId> objectIds)
    {
        int maxObjectNumber = objectOffsets.Count == 0 ? 0 : objectOffsets.Keys.Max();

        WriteAscii(writer, "xref\n");
        WriteAscii(writer, $"0 {maxObjectNumber + 1}\n");
        WriteAscii(writer, "0000000000 65535 f \n");

        for (int objectNumber = 1; objectNumber <= maxObjectNumber; objectNumber++)
        {
            if (objectOffsets.TryGetValue(objectNumber, out int offset)
                && objectIds.TryGetValue(objectNumber, out PdfObjectId objectId))
            {
                WriteAscii(
                    writer,
                    $"{offset:D10} {objectId.GenerationNumber:D5} n \n");
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

    private static void WriteAscii(ByteBufferWriter writer, string value)
    {
        writer.Write(Encoding.ASCII.GetBytes(value));
    }
}
