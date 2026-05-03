namespace ModernPDF.Primitives;

internal readonly struct PdfObjectId : IEquatable<PdfObjectId>
{
    public PdfObjectId(int objectNumber, int generationNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(objectNumber, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(generationNumber);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(generationNumber, ushort.MaxValue);

        ObjectNumber = objectNumber;
        GenerationNumber = (ushort)generationNumber;
    }

    public int ObjectNumber { get; }

    public ushort GenerationNumber { get; }

    public bool Equals(PdfObjectId other)
    {
        return ObjectNumber == other.ObjectNumber && GenerationNumber == other.GenerationNumber;
    }

    public override bool Equals(object? obj)
    {
        return obj is PdfObjectId other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(ObjectNumber, GenerationNumber);
    }

    public override string ToString()
    {
        return $"{ObjectNumber} {GenerationNumber} R";
    }

    public static bool operator ==(PdfObjectId left, PdfObjectId right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(PdfObjectId left, PdfObjectId right)
    {
        return !(left == right);
    }
}
