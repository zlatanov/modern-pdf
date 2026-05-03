using ModernPDF.Primitives;

namespace ModernPDF.Format.Objects;

internal abstract class PdfObject
{
}

internal sealed class PdfNullObject : PdfObject
{
    private PdfNullObject()
    {
    }

    public static PdfNullObject Instance { get; } = new PdfNullObject();
}

internal sealed class PdfBooleanObject : PdfObject
{
    public PdfBooleanObject(bool value)
    {
        Value = value;
    }

    public bool Value { get; }
}

internal sealed class PdfNumberObject : PdfObject
{
    public PdfNumberObject(double value, bool isInteger)
    {
        Value = value;
        IsInteger = isInteger;
    }

    public double Value { get; }

    public bool IsInteger { get; }
}

internal sealed class PdfNameObject : PdfObject
{
    public PdfNameObject(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Name cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }
}

internal sealed class PdfStringObject : PdfObject
{
    public PdfStringObject(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    public string Value { get; }
}

internal sealed class PdfByteStringObject : PdfObject
{
    public PdfByteStringObject(ReadOnlyMemory<byte> bytes)
    {
        Bytes = bytes;
    }

    public ReadOnlyMemory<byte> Bytes { get; }
}

internal sealed class PdfReferenceObject : PdfObject
{
    public PdfReferenceObject(PdfObjectId objectId)
    {
        ObjectId = objectId;
    }

    public PdfObjectId ObjectId { get; }
}

internal sealed class PdfArrayObject : PdfObject
{
    public PdfArrayObject(IEnumerable<PdfObject> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items.ToArray();
    }

    public IReadOnlyList<PdfObject> Items { get; }
}

internal sealed class PdfDictionaryEntry
{
    public PdfDictionaryEntry(string key, PdfObject value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Dictionary key cannot be empty.", nameof(key));
        }

        ArgumentNullException.ThrowIfNull(value);

        Key = key;
        Value = value;
    }

    public string Key { get; }

    public PdfObject Value { get; }
}

internal sealed class PdfDictionaryObject : PdfObject
{
    public PdfDictionaryObject(IEnumerable<PdfDictionaryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Entries = entries.ToArray();
    }

    public IReadOnlyList<PdfDictionaryEntry> Entries { get; }
}

internal sealed class PdfStreamObject : PdfObject
{
    public PdfStreamObject(PdfDictionaryObject dictionary, ReadOnlyMemory<byte> data)
    {
        Dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
        Data = data;
    }

    public PdfDictionaryObject Dictionary { get; }

    public ReadOnlyMemory<byte> Data { get; }
}
